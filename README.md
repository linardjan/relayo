# Relayo

Система поддержки: операторы ведут переписку с клиентами из веб-виджета, Telegram и WhatsApp.
Техническое задание — [relayo-tz.md](relayo-tz.md).

## Состав

| Путь | Что это |
|---|---|
| `src/Relayo.Internal` | Внутренний сервис: API админки (порт 5100), gRPC для публичного сервиса (5101), фоновые задачи. Только внутренняя сеть |
| `src/Relayo.Public` | Публичный сервис: API виджета, вебхуки мессенджеров (5200). Доступен из интернета, **доступа к БД не имеет** |
| `src/Relayo.Core` | Доменная модель и данные (EF Core, MS SQL Server). Используется только внутренним сервисом |
| `src/Relayo.Contracts` | Контракты между сервисами: gRPC (`Protos/`) и сообщения шины |
| `src/Relayo.ServiceDefaults` | Общая обвязка: `.env`, проверка настроек, логи, трассировка, health checks |
| `web/admin` | Админка: React + TypeScript + AntD (порт 5173) |
| `web/widget` | Виджет чата: Preact в Shadow DOM. Собирается в `src/Relayo.Public/wwwroot/widget/core.js` |
| `tests/Relayo.Tests` | Тесты, включая проверку, что публичный сервис не зависит от БД |
| `tools/TestSite` | Тестовый сайт с виджетом, запускается в Docker (порты 5300 и 5301 — два зеркала) |
| `tools/test-page` | Простая страница с виджетом для VS Code Live Server |
| `tools/test-ad` | Скрипт наполнения тестового AD |
| `tools/checks` | Проверки работающего стенда и скриншоты ([README](tools/checks/README.md)) |
| `CLAUDE.md`, `.claude/skills` | Заметки и скиллы для Claude Code: правила проекта, запуск и проверка стенда, порядок добавления функций |

Как устроена система: компоненты, связи, сценарии и тестовые стенды — [docs/architecture.md](docs/architecture.md).
Развёртывание на серверах (без среды разработки) — [docs/deployment.md](docs/deployment.md).

## Что нужно

- .NET SDK 10
- Node.js 22+
- Docker с Compose

## Первый запуск

```bash
# 1. Настройки
cp .env.example .env
# Заполнить пустые значения с пометкой GENERATE (openssl rand -base64 32)
# и BOOTSTRAP_ADMIN_PASSWORD — пароль первого администратора.

# 2. Инфраструктура: MS SQL, Redis, RabbitMQ, S3 (RustFS), Seq и тестовый сайт
#    Mac с Apple Silicon: образ SQL Server только для amd64 и требует Rosetta в Docker
#    (Settings → General → «Use Rosetta»). Без неё в .env: MSSQL_IMAGE=mcr.microsoft.com/azure-sql-edge:latest
docker compose up -d
# С тестовым AD (Samba):
docker compose --profile ad up -d
docker compose exec samba sh /seed/seed.sh   # папка Support и тестовые операторы

# 3. Виджет (публичный сервис раздаёт собранный файл)
cd web/widget && npm install && npm run build && cd ../..

# 4. Сервисы (в отдельных терминалах)
dotnet tool restore
dotnet run --project src/Relayo.Internal
dotnet run --project src/Relayo.Public

# 5. Админка
cd web/admin && npm install && npm run dev
```

Админка: http://localhost:5173, вход `admin` и пароль из `BOOTSTRAP_ADMIN_PASSWORD`.
Администратор создаётся при первом запуске, если в БД ещё нет пользователей. Базу `relayo` внутренний сервис
создаёт сам; все миграции собраны в одну (`Initial`).

Запрос к БД из терминала: `node tools/checks/db.cjs "SELECT TOP 5 number, status FROM tickets ORDER BY number DESC"`.

## Проверка виджета

1. Откройте тестовый сайт: http://localhost:5300 (зеркало — http://localhost:5301).
2. Нажмите кнопку чата в правом нижнем углу и напишите сообщение. Это анонимный посетитель.
3. В админке (http://localhost:5173) появится тикет. Перед ответом задайте псевдоним
   (меню пользователя → «Alias for clients»): клиент видит только его.
4. Ответьте: ответ сразу появится в виджете.
5. На тестовом сайте нажмите «Sign in as client 1001»: страница получит токен у своего backend
   и вызовет `Relayo.identify(token)`. Переписка привяжется к клиенту 1001, в админке появится
   пометка «clientId verified».
6. Откройте зеркало и войдите тем же клиентом: там будет та же переписка.
7. Ссылка «Switch to direct clientId» включает небезопасный режим: страница вызывает
   `Relayo.setClientId()`. Такой посетитель видит только свою переписку, а оператор — пометку
   «clientId not verified» и отдельный чат в тикете.

Тестовый сайт, его ключ и два виджета создаются автоматически при запуске внутреннего сервиса
в Development (`DEV_SEED_TEST_SITE=true`). Страница `tools/test-page/index.html` для Live Server
использует виджет в режиме прямой передачи clientId.

После изменений в `web/widget` пересоберите виджет: `npm run build` (или `npm run watch`).
После изменений в `tools/TestSite` пересоберите контейнер: `docker compose up -d --build test-site`.

## Проверка файлов

1. Откройте тестовый сайт и чат виджета. Нажмите скрепку или перетащите файл в окно чата, напишите текст и отправьте.
2. В админке файл виден в переписке: картинки — превью, остальное — ссылка на скачивание. Файл лежит в S3
   (http://localhost:9001, бакет `relayo-attachments`), ссылки временные (`S3_PRESIGNED_URL_MINUTES`).
3. Ответьте с файлом: скрепка под полем ответа или перетаскивание. Файл придёт в виджет (и в Telegram, если клиент там).
4. Лимиты — `ATTACHMENTS_MAX_SIZE_MB` и `ATTACHMENTS_ALLOWED_TYPES` в `.env`; у виджета файлы можно выключить
   («Channels → виджет → Allow attachments», затем «Publish»).

## Проверка перехода в Telegram

1. Создайте бота у [@BotFather](https://t.me/BotFather) и подключите его в админке:
   «Channels → Add channel → Telegram bot», вставьте токен и выберите сайт. Ботов может быть несколько.
2. В «Channels» откройте тестовый виджет и выберите этого бота в поле «Telegram bot». Локально работает режим
   `TELEGRAM_UPDATES_MODE=polling` из `.env`: сообщения забирает сам внутренний сервис, публичный адрес не нужен.
   Новый бот подхватывается в течение 10 секунд, изменения виджета — примерно за минуту.
3. На тестовом сайте напишите сообщение в виджет, затем нажмите синюю кнопку Telegram под кнопкой чата (или вызовите `Relayo.open('tg')`).
4. В Telegram нажмите «Start». Бот ответит, что переписка продолжается здесь, а в чате тикета появится
   пометка «The client continued the chat in Telegram».
5. Напишите боту: сообщение появится в том же чате тикета с меткой Telegram и в виджете.
6. Ответьте из админки: над полем ввода видно, что ответ уйдёт в Telegram. Ответ придёт в Telegram
   от бота с псевдонимом оператора в первой строке.

В бою используется `TELEGRAM_UPDATES_MODE=webhook`: тогда `PUBLIC_BASE_URL` должен быть публичным HTTPS-адресом,
а внутреннему сервису нужен исходящий доступ к `api.telegram.org` (или `TELEGRAM_PROXY_URL`).

## Полезные адреса

| Что | Адрес |
|---|---|
| Админка | http://localhost:5173 |
| Тестовый сайт и зеркало | http://localhost:5300, http://localhost:5301 |
| Логи и трассировка (Seq) | http://localhost:8081 (admin / `SEQ_FIRSTRUN_ADMIN_PASSWORD`) |
| RabbitMQ | http://localhost:15672 (`RABBITMQ_USER` / `RABBITMQ_PASSWORD`) |
| Консоль S3 (RustFS) | http://localhost:9001 (`MINIO_ROOT_USER` / `MINIO_ROOT_PASSWORD`) |
| Готовность сервисов | http://localhost:5100/health/ready, http://localhost:5200/health/ready |

## Разработка

```bash
dotnet build Relayo.slnx
dotnet test --project tests/Relayo.Tests

# Новая миграция БД (применяется при запуске внутреннего сервиса)
dotnet ef migrations add <Name> --project src/Relayo.Core --startup-project src/Relayo.Core --output-dir Data/Migrations
```

## Проверка входа через AD

Подключение к AD настраивается в админке: «Settings → LDAP». В `.env` его нет.

1. Запустите тестовый AD и наполните его: `docker compose --profile ad up -d`,
   затем `docker compose exec samba sh /seed/seed.sh`.
2. В «Settings → LDAP» заполните:

   | Поле | Значение |
   |---|---|
   | Host | `localhost` |
   | Service account | `CN=relayo-sync,OU=Service Accounts,DC=example,DC=local` |
   | Password | `Oper_Passw0rd` |
   | Encryption, Port | LDAPS (SSL), `636` |
   | Verify certificate | выключить: у тестового AD самоподписанный сертификат |
   | Users folder (OU) | `OU=Support,DC=example,DC=local` |

   Включите «Sign-in with AD» и нажмите Save: статус подключения покажет «Connected. Users in the folder: 3».
3. Нажмите «Import users»: в разделе Agents появятся `ivanov`, `petrova`, `sidorov` с ролью по умолчанию.
4. Войдите в админку как `ivanov` / `Oper_Passw0rd`: пароль проверяет AD.

Тестовый AD: домен `example.local`, пользователи `ivanov`, `petrova`, `sidorov` в `OU=Support`,
`smirnov` в `OU=Sales` (не должен импортироваться), служебная учётка `relayo-sync`.
Пароль у всех `Oper_Passw0rd`. Отключите пользователя (`docker compose exec samba samba-tool user disable sidorov`)
и снова импортируйте: он будет заблокирован в Relayo, а после `user enable` — разблокирован.
