# Развёртывание Relayo

Инструкция для администратора, который разворачивает Relayo на своих серверах.
Всё нужное лежит в папке [`deploy/`](../deploy): образы сервисов, `docker-compose.yml` и образец настроек.

Порядок проверен на чистом стенде: сборка образов, запуск с пустой базой, вход в админку,
сообщение из виджета до оператора и обратно, резервная копия и восстановление.

## 1. Из чего состоит Relayo

| Компонент | Что делает | Где работает | Порт в контейнере |
|---|---|---|---|
| `relayo-internal` | API админки, бизнес-логика, фоновые задачи, Telegram Bot API, синхронизация с AD | Внутренняя сеть | 5100 (HTTP), 5101 (gRPC) |
| `relayo-admin` | Админка (nginx): раздаёт интерфейс и передаёт `/api` и `/hubs` во `relayo-internal` | Внутренняя сеть | 8080 |
| `relayo-public` | API и realtime виджета, вебхуки Telegram. **Доступа к БД нет** | Доступен из интернета | 5200 |
| `postgres` | PostgreSQL 18: все данные | Внутренняя сеть | 5432 |
| `rabbitmq` | RabbitMQ 4.3: события между сервисами | Внутренняя сеть | 5672 |
| S3 (MinIO компании; для стенда — контейнер `s3`) | Файлы виджетов `widget_<ключ>.js`, которые подключают сайты | Внутренняя сеть, чтение — через прокси | 9000 |

Связи, которые должны быть разрешены в сети:

```
Браузер оператора ──HTTPS──► [TLS-прокси] ──► relayo-admin:8080 ──► relayo-internal:5100
Посетитель сайта ──HTTPS──► [TLS-прокси] ──► relayo-public:5200
Посетитель сайта ──HTTPS──► [TLS-прокси, только чтение] ──► S3: бакет relayo-widgets (файлы widget_*.js)
Telegram ──────────HTTPS──► [TLS-прокси] ──► relayo-public:5200      (режим webhook)

relayo-public ──► relayo-internal:5101 (gRPC)      ┐ только эти два соединения
relayo-public ──► rabbitmq:5672                    ┘ из публичной зоны во внутреннюю
relayo-internal ──► api.telegram.org:443 (напрямую или через HTTP-прокси)
relayo-internal ──► сервер AD:636 или 389 (если включён вход через AD)
relayo-internal ──► S3 API :9000 (публикация виджетов)
```

Redis пока не нужен: он понадобится для нескольких экземпляров внутреннего сервиса.

## 2. Что нужно заранее

- Сервер Linux (x86-64 или ARM64), для стенда достаточно 2 CPU, 4 ГБ RAM и 20 ГБ диска.
- Docker 24+ и Docker Compose 2.15+ (`docker compose version`).
- Доступ к Docker Hub и mcr.microsoft.com для сборки образов, а также к npm и NuGet.
  Если интернета на сервере нет, соберите образы на другой машине (раздел 8).
- Копия репозитория Relayo.
- Два адреса с HTTPS-сертификатами: для админки (только внутренняя сеть), например `https://relayo.company.local`,
  и для публичного сервиса (из интернета), например `https://chat.company.com`.
- TLS-прокси перед контейнерами: nginx, HAProxy или балансировщик компании. Сами контейнеры работают по HTTP.

## 3. Стенд на одном сервере

### 3.1. Настройки

```bash
cd relayo/deploy
cp .env.example .env
chmod 600 .env
```

Откройте `deploy/.env` и заполните:

1. Значения с пометкой `GENERATE`: каждое генерируется командой `openssl rand -base64 32`.
   В паролях PostgreSQL и RabbitMQ лучше обойтись без символов `/`, `+`, `=`:
   `openssl rand -base64 32 | tr -d '/+='`.
2. `BOOTSTRAP_ADMIN_PASSWORD` — пароль первого администратора, не короче 10 символов.
3. `PUBLIC_BASE_URL` — внешний HTTPS-адрес публичного сервиса. Он попадает в код вставки виджета на сайты
   и в адрес вебхука Telegram, поэтому должен быть тем адресом, который видят посетители.
4. `TELEGRAM_UPDATES_MODE`: `webhook`, если `PUBLIC_BASE_URL` доступен из интернета по HTTPS;
   `polling` — если нет (тестовый стенд), но тогда внутренний сервис должен работать в одном экземпляре.
5. Хранилище файлов: `S3_ENDPOINT`, `S3_ACCESS_KEY`, `S3_SECRET_KEY` — MinIO компании (учётка с правом писать
   в бакет `S3_BUCKET_WIDGETS`). Для стенда без MinIO запустите контейнер `s3` (профиль `storage`) и укажите
   `S3_ENDPOINT=http://s3:9000`. `WIDGET_FILES_BASE_URL` — внешний адрес, по которому браузеры читают файлы
   виджетов (раздел 3.3). **Он входит в код вставки на сайтах:** после подключения сайтов его не меняют.

> **`SECRETS_ENCRYPTION_KEY` сохраните отдельно от резервных копий БД** (например, в хранилище паролей).
> Им зашифрованы токены Telegram-ботов, ключи сайтов и пароль служебной учётки AD.
> Без него данные из бэкапа восстановятся, но эти секреты придётся вводить заново.

Остальные настройки описаны в самом файле и в разделе 7.

### 3.2. Сборка и запуск

```bash
cd relayo/deploy
docker compose --profile internal --profile public up -d --build
# со своим S3 для стенда (без MinIO компании):
docker compose --profile internal --profile public --profile storage up -d --build
```

Первая сборка занимает 5–10 минут. При первом запуске внутренний сервис сам создаёт таблицы в БД
и первого администратора.

Проверка:

```bash
docker compose --profile internal --profile public ps          # все сервисы Up, rabbitmq-init — Exited (0)
curl -s http://127.0.0.1:8080/api/auth/refresh -X POST -o /dev/null -w '%{http_code}\n'   # 401 — API отвечает
curl -s http://127.0.0.1:5200/health/ready                     # Healthy — публичный сервис видит внутренний
docker compose logs relayo-internal | grep -E 'migration|administrator|ERR'
```

Публичный сервис может перезапуститься один-два раза, пока поднимаются RabbitMQ и внутренний сервис. Это нормально.

### 3.3. TLS-прокси

Контейнеры по умолчанию слушают так:

| Что | Адрес на сервере | Переменные |
|---|---|---|
| Админка | `127.0.0.1:8080` | `ADMIN_BIND`, `ADMIN_PORT` |
| Публичный сервис | `0.0.0.0:5200` | `PUBLIC_BIND`, `PUBLIC_PORT` |

Пример для nginx на этом же сервере (сертификаты подставьте свои):

```nginx
# Админка: только внутренняя сеть
server {
    listen 443 ssl;
    server_name relayo.company.local;
    ssl_certificate     /etc/ssl/relayo/admin.crt;
    ssl_certificate_key /etc/ssl/relayo/admin.key;

    location / {
        proxy_pass http://127.0.0.1:8080;
        proxy_http_version 1.1;
        proxy_set_header Host $host;
        proxy_set_header X-Forwarded-For $remote_addr;
        proxy_set_header X-Forwarded-Proto https;
        # Realtime операторов (WebSocket)
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection $connection_upgrade;
        proxy_read_timeout 1h;
    }
}

# Публичный сервис: доступен из интернета
server {
    listen 443 ssl;
    server_name chat.company.com;
    ssl_certificate     /etc/ssl/relayo/public.crt;
    ssl_certificate_key /etc/ssl/relayo/public.key;

    location / {
        proxy_pass http://127.0.0.1:5200;
        proxy_http_version 1.1;
        proxy_set_header Host $host;
        proxy_set_header X-Forwarded-For $remote_addr;
        proxy_set_header X-Forwarded-Proto https;
        # Realtime виджета (WebSocket)
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection $connection_upgrade;
        proxy_read_timeout 1h;
        client_max_body_size 25m;
    }
}

# Файлы виджетов из S3: только чтение файлов widget_*.js публичного бакета.
# В этом же server {} публичного сервиса; WIDGET_FILES_BASE_URL=https://chat.company.com/w
#   location ~ ^/w/(widget_[A-Za-z0-9]+(\.v[0-9]+)?\.js)$ {
#       limit_except GET HEAD { deny all; }
#       proxy_pass http://minio.company.local:9000/relayo-widgets/$1;
#       proxy_hide_header Set-Cookie;
#   }

# В блоке http {}:
map $http_upgrade $connection_upgrade {
    default upgrade;
    ''      close;
}
```

**Обязательно укажите, каким прокси доверять** — иначе лимиты «на IP» (вход в админку, запросы виджета)
будут считаться по адресу прокси, то есть станут одним общим лимитом на всех:

- прокси на этом же сервере: в `deploy/.env` задайте `PUBLIC_TRUSTED_PROXIES=172.30.0.1`
  (адрес шлюза сети контейнеров; если меняли `RELAYO_SUBNET`, возьмите первый адрес своей подсети).
  Для админки шлюз уже входит в доверенные;
- отдельный балансировщик: укажите его адрес в `PUBLIC_TRUSTED_PROXIES` и `INTERNAL_TRUSTED_PROXIES`
  и откройте ему порты `ADMIN_BIND`/`PUBLIC_BIND`.

После изменения `.env`: `docker compose --profile internal --profile public up -d`.

> Админка работает только по HTTPS: cookie входа помечены `Secure`, и по обычному HTTP браузер их не сохранит
> (исключение — `http://localhost` при проверке на самом сервере).
> Не открывайте порт 8080 наружу в обход прокси: `ADMIN_BIND` оставьте `127.0.0.1`.

## 4. Два сервера: внутренний и публичный

По ТЗ (п. 2.2) публичный сервис стоит в зоне, доступной из интернета, а всё остальное — во внутренней сети.
Сетевые правила между зонами определяет отдел ИБ.

**Внутренний сервер** — PostgreSQL, RabbitMQ, внутренний сервис, админка:

```bash
cd relayo/deploy
# в .env: INTERNAL_BIND=<IP внутреннего сервера, доступный публичному серверу>
docker compose --profile internal up -d --build
```

**Публичный сервер** — только публичный сервис. Ему нужен свой `deploy/.env`, в котором достаточно этих строк:

```bash
COMPOSE_PROJECT_NAME=relayo-public
ASPNETCORE_ENVIRONMENT=Production
INTERNAL_GRPC_URL=http://<IP внутреннего сервера>:5101
PUBLIC_RABBITMQ_HOST=<IP внутреннего сервера>
INTERNAL_GRPC_KEY=<то же значение, что на внутреннем сервере>
RABBITMQ_VHOST=relayo
RABBITMQ_PUBLIC_USER=relayo_public
RABBITMQ_PUBLIC_PASSWORD=<то же значение, что на внутреннем сервере>
PUBLIC_TRUSTED_PROXIES=<адрес TLS-прокси или балансировщика>
```

```bash
cd relayo/deploy
docker compose --profile public up -d --build
```

Паролей БД и ключей шифрования на публичном сервере нет, и они ему не нужны.

Firewall: на внутреннем сервере порты 5101 и 5672 разрешите **только** с адреса публичного сервера.
gRPC между серверами идёт без TLS, вызовы защищены ключом `INTERNAL_GRPC_KEY`; если ИБ требует шифрования
внутри сети, поставьте между серверами TLS-туннель или прокси.

## 5. Первая настройка в админке

1. Откройте адрес админки и войдите: логин `admin` (или `BOOTSTRAP_ADMIN_LOGIN`), пароль `BOOTSTRAP_ADMIN_PASSWORD`.
2. **Agents → Administrator → Set password**: смените пароль. Строку `BOOTSTRAP_ADMIN_PASSWORD` из `.env`
   после этого можно удалить: администратор создаётся, только когда в БД нет пользователей.
3. Нажмите на свою строку «no alias set» в левом меню и задайте псевдоним: его видят клиенты,
   без него нельзя отвечать.
4. **Settings → Sites → Add site**: код сайта (product) и домены, включая зеркала. Виджет работает только на них.
5. **Channels → Add channel → Widget**: настройки виджета. Нажмите **Publish**: Relayo положит файл
   `widget_<ключ>.js` в S3. Ниже на странице — код вставки (одна строка), его вставляют на все страницы сайта
   перед `</body>`. Изменения настроек попадают на сайты только после следующей публикации; там же история версий
   и откат.
6. **Channels → Add channel → Telegram bot** (если нужен): токен от @BotFather. Затем выберите бота в настройках виджета —
   в виджете появится кнопка Telegram.
7. **Settings → LDAP** (если нужен вход через AD): сервер, служебная учётка с правами только на чтение,
   папка (OU) с операторами. «Save» сразу проверяет подключение, «Import users» заводит пользователей.
   Сертификат AD должен быть доверенным для контейнера; для тестового AD проверку можно выключить.
8. **Agents**: назначьте роли импортированным пользователям или создайте локальных.

## 6. Обновление

```bash
cd relayo
git pull                                   # или распакуйте новую версию
cd deploy
# сначала резервная копия БД (раздел 7)
docker compose --profile internal --profile public up -d --build
```

Миграции БД применяются при запуске внутреннего сервиса. Они только добавляют изменения, поэтому откат на старую
версию кода после миграции не поддерживается: для отката восстановите БД из копии, снятой перед обновлением.

Если сервисы на двух серверах, обновляйте сначала внутренний, затем публичный.

## 7. Резервные копии

Всё состояние Relayo — в PostgreSQL (RabbitMQ хранит только сообщения в пути).

**Копия** (можно делать на работающей системе, например по cron каждую ночь):

```bash
cd relayo/deploy
docker compose exec -T postgres sh -c 'pg_dump -U "$POSTGRES_USER" -d "$POSTGRES_DB" -Fc' \
  > /backup/relayo-$(date +%F).dump
```

Храните копии вне сервера. И отдельно от них — `SECRETS_ENCRYPTION_KEY` (раздел 3.1).

**Восстановление:**

```bash
cd relayo/deploy
docker compose stop relayo-internal relayo-public
docker compose cp /backup/relayo-2026-11-25.dump postgres:/tmp/relayo.dump
docker compose exec postgres sh -c \
  'pg_restore -U "$POSTGRES_USER" -d "$POSTGRES_DB" --clean --if-exists --no-owner /tmp/relayo.dump'
docker compose --profile internal --profile public up -d
```

`SECRETS_ENCRYPTION_KEY` в `.env` должен быть тем же, что при создании копии.

## 8. Справочник настроек `deploy/.env`

| Переменная | Обязательна | Что задаёт |
|---|---|---|
| `POSTGRES_PASSWORD`, `RABBITMQ_PASSWORD`, `RABBITMQ_PUBLIC_PASSWORD` | да | Пароли БД и шины |
| `JWT_SIGNING_KEY` | да | Подпись токенов входа в админку. Смена ключа разлогинит всех |
| `SECRETS_ENCRYPTION_KEY` | да | Шифрование секретов в БД. **Не менять и не терять** |
| `INTERNAL_GRPC_KEY` | да | Ключ вызовов публичного сервиса во внутренний. Одинаковый на обоих серверах |
| `BOOTSTRAP_ADMIN_PASSWORD` | при первом запуске | Пароль первого администратора |
| `PUBLIC_BASE_URL` | да | Внешний HTTPS-адрес публичного сервиса |
| `TELEGRAM_UPDATES_MODE` | — | `webhook` (по умолчанию) или `polling` |
| `TELEGRAM_PROXY_URL` | — | HTTP-прокси для выхода к api.telegram.org |
| `PUBLIC_TRUSTED_PROXIES`, `INTERNAL_TRUSTED_PROXIES` | за прокси — да | Адреса прокси, которым верить в `X-Forwarded-For` |
| `ADMIN_BIND`, `ADMIN_PORT`, `PUBLIC_BIND`, `PUBLIC_PORT` | — | Где слушают админка и публичный сервис |
| `INTERNAL_BIND`, `INTERNAL_GRPC_HOST_PORT`, `RABBITMQ_HOST_PORT` | для двух серверов | Где открыты gRPC и RabbitMQ для публичного сервиса |
| `POSTGRES_HOST`, `POSTGRES_PORT` | — | Внешний PostgreSQL вместо контейнера |
| `RELAYO_SUBNET` | — | Подсеть контейнеров, если `172.30.0.0/24` занята |
| `SEQ_URL`, `SEQ_API_KEY`, `OTEL_EXPORTER_OTLP_ENDPOINT` | — | Отправка логов и трассировки в Seq |
| `RELAYO_VERSION` | — | Тег образов |
| `S3_ENDPOINT`, `S3_ACCESS_KEY`, `S3_SECRET_KEY`, `S3_REGION`, `S3_FORCE_PATH_STYLE`, `S3_BUCKET_WIDGETS` | да | Хранилище файлов виджетов (MinIO). Бакет создаётся сам; чтение `widget_*` разрешается всем |
| `WIDGET_FILES_BASE_URL` | да | Внешний адрес файлов виджетов. Входит в код вставки на сайтах |

Настройки, которые меняются в работе (сайты, виджеты, боты, AD, сотрудники, правила), хранятся в БД
и задаются в админке, а не в `.env`.

**Свой PostgreSQL компании.** Задайте `POSTGRES_HOST`/`POSTGRES_PORT` и учётку с правом создавать таблицы
в своей БД. Контейнер `postgres` при этом не нужен: запускайте сервисы по именам —
`docker compose --profile internal up -d --build rabbitmq rabbitmq-init relayo-internal relayo-admin`.

**Сервер без интернета.** Соберите образы на машине с доступом в интернет и перенесите:

```bash
docker compose --profile internal --profile public build
docker save relayo-internal relayo-admin relayo-public postgres:18-alpine rabbitmq:4.3-management-alpine \
  curlimages/curl:latest | gzip > relayo-images.tar.gz
# на сервере:
docker load < relayo-images.tar.gz
docker compose --profile internal --profile public up -d       # без --build
```

## 9. Если что-то не работает

| Признак | Причина и что делать |
|---|---|
| После входа в админку снова открывается страница входа | Админка открыта по HTTP: cookie входа сохраняются только по HTTPS (раздел 3.3) |
| Все пользователи упираются в «Too many requests» | Не заданы `*_TRUSTED_PROXIES`: сервис считает всех одним клиентом с адресом прокси |
| Виджет не появляется, в консоли браузера 403 `origin_not_allowed` | Домена нет в настройках сайта (Settings → Sites). Изменения доходят до публичного сервиса за минуту |
| Виджет: 503 `service_unavailable`; `/health/ready` публичного сервиса — Unhealthy | Публичный сервис не достучался до внутреннего: проверьте `INTERNAL_GRPC_URL`, порт 5101 в firewall и одинаковый `INTERNAL_GRPC_KEY` |
| Ответы операторов не появляются в виджете без перезагрузки | Публичный сервис не подключён к RabbitMQ: `docker compose logs relayo-public`, проверьте `PUBLIC_RABBITMQ_HOST`, порт 5672 и пароль `RABBITMQ_PUBLIC_PASSWORD` |
| Telegram-бот не сохраняется: «Telegram delivers messages only to a public HTTPS address» | `PUBLIC_BASE_URL` не HTTPS. Нужен публичный HTTPS-адрес или `TELEGRAM_UPDATES_MODE=polling` |
| Бот не отвечает, в логах `Could not reach Telegram` | Внутреннему сервису нет выхода к api.telegram.org: откройте доступ или задайте `TELEGRAM_PROXY_URL` |
| LDAP: «the server certificate is not trusted» | Сертификат AD выдан внутренним УЦ: добавьте сертификат УЦ в образ `relayo-internal` (ниже) или, только для теста, выключите проверку |
| `rabbitmq-init` завершился с ошибкой | RabbitMQ не принял пароль: в паролях RabbitMQ не используйте `"` и `\`; пересоздайте том `rabbitmq-data`, если меняли `RABBITMQ_PASSWORD` после первого запуска |
| Publish: «Could not upload the widget file to S3» | Проверьте `S3_ENDPOINT`, ключи и права учётки на бакет. Если MinIO не даёт Relayo задать политику бакета, администратор MinIO разрешает анонимное чтение объектов `widget_*` сам |
| Виджет не появляется, файл `widget_<ключ>.js` отвечает 403/404 | Виджет не опубликован или прокси `/w/` не пропускает чтение бакета (раздел 3.3) |
| В логах предупреждения `DataProtection … not be persisted` | Не влияют на работу: Relayo не хранит данные через Data Protection |

**Сертификат внутреннего УЦ** (для LDAPS и прокси компании). Положите `company-ca.crt` в `deploy/`
и добавьте в `deploy/Dockerfile.internal` перед строкой `USER app`:

```dockerfile
USER root
COPY deploy/company-ca.crt /usr/local/share/ca-certificates/company-ca.crt
RUN update-ca-certificates
```

Затем пересоберите: `docker compose --profile internal up -d --build`.

Логи: `docker compose logs -f relayo-internal` (и `relayo-public`, `relayo-admin`). Готовность сервисов:
`/health/ready` на портах 5100 (изнутри контейнера) и 5200.

## 10. Ограничения текущей версии

- **Внутренний сервис — один экземпляр.** Realtime операторов работает внутри одного процесса (Redis backplane
  появится позже), а режим Telegram `polling` не допускает двух экземпляров. Публичных сервисов может быть несколько.
- Вложения, WhatsApp и CSAT ещё не реализованы.
- Правила работают в одном экземпляре внутреннего сервиса вместе с остальным (очередь и таймеры — в PostgreSQL).
