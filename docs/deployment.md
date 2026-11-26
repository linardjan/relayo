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
| `relayo-whatsapp` (профиль `whatsapp`, необязателен) | Шлюз WhatsApp: номера подключаются по QR-коду, сообщения идут в Relayo. Хранит ключи сессий в томе `whatsapp-sessions`. **Один экземпляр** | Внутренняя сеть | 5400 |
| `mssql` | MS SQL Server 2022: все данные (для стенда — контейнер; в бою — сервер компании) | Внутренняя сеть | 1433 |
| `rabbitmq` | RabbitMQ 4.3: события между сервисами | Внутренняя сеть | 5672 |
| `redis` | Redis 8: общие лимиты, realtime операторов и опрос Telegram при нескольких экземплярах внутреннего сервиса. На диск ничего не пишет | Внутренняя сеть | 6379 |
| S3 (MinIO компании; для стенда — контейнер `s3`) | Файлы виджетов `widget_<ключ>.js` и картинки кнопок (бакет виджетов, читается всеми), вложения сообщений (закрытый бакет, только временные ссылки) | Внутренняя сеть, чтение — через прокси | 9000 |

Связи, которые должны быть разрешены в сети:

```
Браузер оператора ──HTTPS──► [TLS-прокси] ──► relayo-admin:8080 ──► relayo-internal:5100
Посетитель сайта ──HTTPS──► [TLS-прокси] ──► relayo-public:5200
Посетитель сайта ──HTTPS──► [TLS-прокси, только чтение] ──► S3: бакет relayo-widgets (файлы widget_*)
Посетитель и оператор ──HTTPS──► [TLS-прокси, только GET] ──► S3: бакет relayo-attachments (временные ссылки)
Telegram ──────────HTTPS──► [TLS-прокси] ──► relayo-public:5200      (режим webhook)

relayo-public ──► relayo-internal:5101 (gRPC)      ┐ только эти два соединения
relayo-public ──► rabbitmq:5672                    ┘ из публичной зоны во внутреннюю
relayo-internal ──► api.telegram.org:443 (напрямую или через HTTP-прокси)
relayo-internal ──► сервер AD:636 или 389 (если включён вход через AD)
relayo-internal ──► S3 API :9000 (публикация виджетов, вложения)
relayo-internal ──► MS SQL :1433
relayo-internal ──► relayo-whatsapp:5400           ┐ если включён шлюз WhatsApp
relayo-whatsapp ──► relayo-internal:5100            │ (общий ключ WHATSAPP_GATEWAY_KEY)
relayo-whatsapp ──► серверы WhatsApp :443           ┘ web.whatsapp.com, *.whatsapp.net — напрямую, без прокси
```

Redis нужен внутреннему сервису, чтобы его экземпляры работали как один (раздел 4.1). Если он недоступен,
сервис продолжает работать, но экземпляры перестают делить лимиты и события.

## 2. Что нужно заранее

- Сервер Linux x86-64, для стенда достаточно 2 CPU, 6 ГБ RAM и 30 ГБ диска (SQL Server требует не меньше 2 ГБ RAM).
  Образ SQL Server есть только для x86-64; на ARM64 используйте сервер MS SQL компании.
- MS SQL Server 2019 или новее (любая редакция: секционирование таблиц есть во всех с 2016 SP1).
  Для стенда — контейнер `mssql` (редакция Developer, не для боя); в бою — сервер компании и лицензия.
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
   В паролях RabbitMQ лучше обойтись без символов `/`, `+`, `=`: `openssl rand -base64 32 | tr -d '/+='`.
   `MSSQL_PASSWORD` должен содержать буквы разного регистра и цифры, иначе SQL Server его не примет:
   например, `echo "Rl$(openssl rand -hex 16)"`.
2. `BOOTSTRAP_ADMIN_PASSWORD` — пароль первого администратора, не короче 10 символов.
3. `PUBLIC_BASE_URL` — внешний HTTPS-адрес публичного сервиса. Он попадает в код вставки виджета на сайты
   и в адрес вебхука Telegram, поэтому должен быть тем адресом, который видят посетители.
4. `TELEGRAM_UPDATES_MODE`: `webhook`, если `PUBLIC_BASE_URL` доступен из интернета по HTTPS;
   `polling` — если нет (тестовый стенд), но тогда внутренний сервис должен работать в одном экземпляре.
5. Хранилище файлов: `S3_ENDPOINT`, `S3_ACCESS_KEY`, `S3_SECRET_KEY` — MinIO компании (учётка с правом писать
   в бакеты `S3_BUCKET_WIDGETS` и `S3_BUCKET_ATTACHMENTS`). Для стенда без MinIO запустите контейнер `s3` (профиль `storage`) и укажите
   `S3_ENDPOINT=http://s3:9000`. `WIDGET_FILES_BASE_URL` — внешний адрес, по которому браузеры читают файлы
   виджетов (раздел 3.3). **Он входит в код вставки на сайтах:** после подключения сайтов его не меняют.
   `S3_PUBLIC_URL` — внешний адрес S3 для временных ссылок на вложения (раздел 3.3), `ATTACHMENTS_*` — лимиты на файлы.

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

Первая сборка занимает 5–10 минут. При первом запуске внутренний сервис сам создаёт базу `MSSQL_DB`, таблицы
и первого администратора. SQL Server в контейнере стартует 20–40 секунд: внутренний сервис его дождётся.

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

# Вложения: временные ссылки на закрытый бакет. Отдельное имя хоста, путь и Host передаются без изменений —
# они входят в подпись ссылки. S3_PUBLIC_URL=https://files.company.com
server {
    listen 443 ssl;
    server_name files.company.com;
    ssl_certificate     /etc/ssl/relayo/files.crt;
    ssl_certificate_key /etc/ssl/relayo/files.key;

    location /relayo-attachments/ {
        limit_except GET HEAD { deny all; }
        proxy_pass http://minio.company.local:9000;
        proxy_set_header Host $host;
        proxy_hide_header Set-Cookie;
        add_header X-Content-Type-Options nosniff always;
    }
}

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

### 3.4. Шлюз WhatsApp (подключение по QR)

Нужен, только если клиентов WhatsApp ведут в Relayo. Шлюз `relayo-whatsapp` подключается к WhatsApp как
«связанное устройство» телефона (протокол WhatsApp Web, библиотека Baileys) и пересылает сообщения во внутренний сервис.

```bash
# в deploy/.env:
#   WHATSAPP_GATEWAY_URL=http://relayo-whatsapp:5400
#   WHATSAPP_GATEWAY_KEY=<openssl rand -base64 32>
docker compose --profile internal --profile public --profile whatsapp up -d --build
```

- **Риски.** Это неофициальный способ: WhatsApp может заблокировать номер, а после обновлений WhatsApp шлюз иногда
  перестаёт работать до обновления библиотеки. Подключайте отдельный рабочий номер, не отправляйте массовых рассылок.
  Официальная альтернатива — WhatsApp Business Platform (Cloud API) от Meta.
- **Телефон** с номером должен хотя бы раз в 14 дней выходить в интернет, иначе WhatsApp отвяжет устройство.
  Если номер отвязали в телефоне, канал станет «Not connected» — отсканируйте новый QR.
- **Один экземпляр.** Сессия номера живёт в одном процессе; второй шлюз с тем же томом выбьет первый.
- **Том `whatsapp-sessions`** — ключи доступа к номерам. Делайте резервную копию вместе с БД и храните как секрет.
  Без него после переустановки номера придётся подключить заново.
- Пока шлюз недоступен, ответы операторов ждут в очереди `whatsapp-outgoing` и уходят после его запуска
  (повторы: 5 с … 30 мин). Входящие, пришедшие при недоступном внутреннем сервисе, шлюз повторяет до ~3 часов.

## 4. Два сервера: внутренний и публичный

По ТЗ (п. 2.2) публичный сервис стоит в зоне, доступной из интернета, а всё остальное — во внутренней сети.
Сетевые правила между зонами определяет отдел ИБ.

**Внутренний сервер** — MS SQL (или сервер компании), RabbitMQ, внутренний сервис, админка:

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

### 4.1. Несколько экземпляров

**Внутренний сервис.** Экземпляры работают вместе через MS SQL, RabbitMQ и Redis (`REDIS_HOST`, по умолчанию
контейнер `redis`):

| Что | Как делится между экземплярами |
|---|---|
| Realtime операторов | SignalR backplane в Redis: событие от любого экземпляра получают все операторы |
| Лимит входа в админку (10 в минуту с адреса) | Счётчик в Redis |
| Отправка в Telegram (30 в секунду на бота, 1 в секунду на чат) | Счётчики в Redis; очередь `telegram-outgoing` — в MS SQL |
| Опрос Telegram (`TELEGRAM_UPDATES_MODE=polling`) | Опрашивает один экземпляр — держатель аренды в Redis; если он пропал, через 30 секунд опрос берёт другой. В бою лучше `webhook` |
| Кэш токенов ботов | Сбрасывается на всех экземплярах через канал Redis |
| Правила, таймеры, синхронизация AD, секции сообщений, первый администратор | Блокировки и очереди в MS SQL (Wolverine сам распределяет работу между узлами) |

Перед экземплярами нужен балансировщик:

- для админки (`/api`, `/hubs`) — HTTP с поддержкой WebSocket. «Липкие» сессии не нужны: админка подключается
  к `/hubs/operators` сразу по WebSocket, без отдельного запроса negotiate;
- для gRPC (порт 5101) — балансировщик HTTP/2 (например, nginx `grpc_pass` или L4 на TCP), его адрес — в
  `INTERNAL_GRPC_URL` публичного сервиса.

Ключи в `.env` (`JWT_SIGNING_KEY`, `SECRETS_ENCRYPTION_KEY`, `INTERNAL_GRPC_KEY`) у всех экземпляров одинаковые.

**Публичный сервис.** Экземпляры и так работают вместе: события для виджетов получают все экземпляры через RabbitMQ,
а виджет подключается по WebSocket без negotiate. Без Redis лимиты на IP у каждого экземпляра свои (при N экземплярах
посетитель получает до N×лимита). Чтобы лимиты были общими, задайте `PUBLIC_REDIS_HOST` — **отдельный Redis в
публичной зоне**: из интернет-зоны во внутреннюю открыты только gRPC и RabbitMQ. В Redis публичного сервиса
лежат только счётчики запросов.

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
   **Channels → Add channel → WhatsApp** (если нужен): «Link only» — виджет только открывает чат с номером;
   «QR connection» — отсканируйте QR-код телефоном с этим номером (WhatsApp → Связанные устройства), и сообщения
   пойдут в Relayo. Для QR нужен шлюз (раздел 3.4). Номер выбирается в пунктах виджета.
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

Всё состояние Relayo — в MS SQL и в S3 (вложения и файлы виджетов). RabbitMQ хранит только сообщения в пути.
Бакеты S3 копируются средствами MinIO (`mc mirror`) или вместе с его томом.

**Копия** (можно делать на работающей системе, например по cron каждую ночь):

```bash
cd relayo/deploy
docker compose exec mssql bash -c '/opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -C \
  -Q "BACKUP DATABASE relayo TO DISK = N'"'"'/var/opt/mssql/data/relayo.bak'"'"' WITH INIT, COMPRESSION"'
docker compose cp mssql:/var/opt/mssql/data/relayo.bak /backup/relayo-$(date +%F).bak
```

На сервере MS SQL компании копии делает его администратор обычными средствами (планы обслуживания, агент).

Храните копии вне сервера. И отдельно от них — `SECRETS_ENCRYPTION_KEY` (раздел 3.1).

**Восстановление:**

```bash
cd relayo/deploy
docker compose stop relayo-internal relayo-public
docker compose cp /backup/relayo-2026-11-25.bak mssql:/var/opt/mssql/data/relayo.bak
docker compose exec mssql bash -c '/opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -C \
  -Q "RESTORE DATABASE relayo FROM DISK = N'"'"'/var/opt/mssql/data/relayo.bak'"'"' WITH REPLACE"'
docker compose --profile internal --profile public up -d
```

`SECRETS_ENCRYPTION_KEY` в `.env` должен быть тем же, что при создании копии.

## 8. Справочник настроек `deploy/.env`

| Переменная | Обязательна | Что задаёт |
|---|---|---|
| `MSSQL_PASSWORD`, `RABBITMQ_PASSWORD`, `RABBITMQ_PUBLIC_PASSWORD` | да | Пароли БД и шины |
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
| `MSSQL_HOST`, `MSSQL_PORT`, `MSSQL_DB`, `MSSQL_USER` | — | Сервер MS SQL компании вместо контейнера и учётка |
| `MSSQL_TRUST_SERVER_CERTIFICATE` | — | `true` только для самоподписанного сертификата (контейнер). Соединение шифруется всегда |
| `MSSQL_IMAGE`, `MSSQL_PID` | — | Образ и редакция SQL Server в контейнере стенда |
| `REDIS_PASSWORD` | да | Пароль контейнера `redis` |
| `REDIS_HOST`, `REDIS_PORT`, `REDIS_SSL` | — | Свой Redis компании вместо контейнера |
| `PUBLIC_REDIS_HOST`, `PUBLIC_REDIS_PORT`, `PUBLIC_REDIS_PASSWORD` | — | Redis в публичной зоне: общие лимиты нескольких экземпляров публичного сервиса |
| `RELAYO_SUBNET` | — | Подсеть контейнеров, если `172.30.0.0/24` занята |
| `SEQ_URL`, `SEQ_API_KEY`, `OTEL_EXPORTER_OTLP_ENDPOINT` | — | Отправка логов и трассировки в Seq |
| `RELAYO_VERSION` | — | Тег образов |
| `S3_ENDPOINT`, `S3_ACCESS_KEY`, `S3_SECRET_KEY`, `S3_REGION`, `S3_FORCE_PATH_STYLE`, `S3_BUCKET_WIDGETS` | да | Хранилище файлов виджетов (MinIO). Бакет создаётся сам; чтение `widget_*` разрешается всем |
| `S3_BUCKET_ATTACHMENTS`, `S3_PUBLIC_URL`, `S3_PRESIGNED_URL_MINUTES` | — | Закрытый бакет вложений, внешний адрес для временных ссылок и срок их жизни (15 минут) |
| `ATTACHMENTS_MAX_SIZE_MB`, `ATTACHMENTS_ALLOWED_TYPES` | — | Суммарный размер файлов одного сообщения (20 МБ) и разрешённые типы. Читают оба сервиса. Больше 20 МБ — поднимите и `client_max_body_size` в прокси |
| `PUBLIC_RATE_LIMIT_UPLOADS_PER_MINUTE` | — | Сообщений с файлами в минуту с одного адреса (10) |
| `WIDGET_FILES_BASE_URL` | да | Внешний адрес файлов виджетов. Входит в код вставки на сайтах |

Настройки, которые меняются в работе (сайты, виджеты, боты, AD, сотрудники, правила), хранятся в БД
и задаются в админке, а не в `.env`.

**Свой MS SQL компании.** Администратор СУБД создаёт базу (или даёт право `CREATE DATABASE`) и учётку SQL Server
с ролью `db_owner` в этой базе: Relayo создаёт в ней таблицы, схему `wolverine` для очередей и секционирование
таблицы сообщений. Задайте `MSSQL_HOST`, `MSSQL_PORT`, `MSSQL_DB`, `MSSQL_USER`, `MSSQL_PASSWORD`;
`MSSQL_TRUST_SERVER_CERTIFICATE=false`, если сертификат сервера доверенный. Контейнер `mssql` при этом не нужен:
запускайте сервисы по именам —
`docker compose --profile internal up -d --build rabbitmq rabbitmq-init relayo-internal relayo-admin`.

**Сервер без интернета.** Соберите образы на машине с доступом в интернет и перенесите:

```bash
docker compose --profile internal --profile public build
docker save relayo-internal relayo-admin relayo-public mcr.microsoft.com/mssql/server:2022-latest rabbitmq:4.3-management-alpine redis:8-alpine \
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
| В `/health/ready` внутреннего сервиса `redis: Degraded` | Redis недоступен: сервис работает, но экземпляры не делят лимиты и события, а операторы на других экземплярах не видят новых сообщений без обновления страницы. Проверьте контейнер `redis` и `REDIS_PASSWORD` |
| `rabbitmq-init` завершился с ошибкой | RabbitMQ не принял пароль: в паролях RabbitMQ не используйте `"` и `\`; пересоздайте том `rabbitmq-data`, если меняли `RABBITMQ_PASSWORD` после первого запуска |
| Publish: «Could not upload the widget file to S3» | Проверьте `S3_ENDPOINT`, ключи и права учётки на бакет. Если MinIO не даёт Relayo задать политику бакета, администратор MinIO разрешает анонимное чтение объектов `widget_*` сам |
| Виджет не появляется, файл `widget_<ключ>.js` отвечает 403/404 | Виджет не опубликован или прокси `/w/` не пропускает чтение бакета (раздел 3.3) |
| Вложения не открываются: `SignatureDoesNotMatch` | Прокси перед S3 меняет путь или заголовок `Host`. Ссылка подписана под `S3_PUBLIC_URL`: нужен отдельный хост, путь без изменений и `proxy_set_header Host $host` (раздел 3.3) |
| Файл не отправляется: `storage_not_configured` / 503 в виджете | Не настроен или недоступен S3: проверьте `S3_ENDPOINT` и права учётки на бакет вложений |
| Внутренний сервис не стартует: `Login failed` или `Cannot open database` | Неверные `MSSQL_USER`/`MSSQL_PASSWORD` или у учётки нет права создать базу: создайте базу заранее и выдайте `db_owner` |
| `The certificate chain was issued by an authority that is not trusted` | Сертификат SQL Server не доверенный: добавьте УЦ в образ или для стенда задайте `MSSQL_TRUST_SERVER_CERTIFICATE=true` |
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

- Несколько экземпляров внутреннего сервиса требуют Redis (раздел 4.1). Без него — один экземпляр.
- WhatsApp: только подключение по QR через шлюз (Cloud API от Meta не реализован), групповые чаты не принимаются.
  CSAT ещё не реализован; файлы, которые клиент присылает в Telegram, пока не принимаются.
