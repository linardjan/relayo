# Relayo

Система поддержки: операторы ведут переписку с клиентами из веб-виджета, Telegram и WhatsApp.
Техническое задание — [relayo-tz.md](relayo-tz.md).

## Состав

| Путь | Что это |
|---|---|
| `src/Relayo.Internal` | Внутренний сервис: API админки (порт 5100), gRPC для публичного сервиса (5101), фоновые задачи. Только внутренняя сеть |
| `src/Relayo.Public` | Публичный сервис: API виджета, вебхуки мессенджеров (5200). Доступен из интернета, **доступа к БД не имеет** |
| `src/Relayo.Core` | Доменная модель и данные (EF Core, PostgreSQL). Используется только внутренним сервисом |
| `src/Relayo.Contracts` | Контракты между сервисами: gRPC (`Protos/`) и сообщения шины |
| `src/Relayo.ServiceDefaults` | Общая обвязка: `.env`, проверка настроек, логи, трассировка, health checks |
| `web/admin` | Админка: React + TypeScript + AntD (порт 5173) |
| `tests/Relayo.Tests` | Тесты, включая проверку, что публичный сервис не зависит от БД |
| `tools/test-ad` | Скрипт наполнения тестового AD |

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

# 2. Инфраструктура: PostgreSQL, Redis, RabbitMQ, S3 (RustFS), Seq
docker compose up -d
# С тестовым AD (Samba):
docker compose --profile ad up -d
docker compose exec samba sh /seed/seed.sh   # папка Support и тестовые операторы

# 3. Сервисы (в отдельных терминалах)
dotnet tool restore
dotnet run --project src/Relayo.Internal
dotnet run --project src/Relayo.Public

# 4. Админка
cd web/admin && npm install && npm run dev
```

Админка: http://localhost:5173, вход `admin` и пароль из `BOOTSTRAP_ADMIN_PASSWORD`.
Администратор создаётся при первом запуске, если в БД ещё нет пользователей.

## Полезные адреса

| Что | Адрес |
|---|---|
| Админка | http://localhost:5173 |
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

Тестовый AD: домен `example.local`, пользователи `ivanov`, `petrova`, `sidorov` в `OU=Support`,
`smirnov` в `OU=Sales` (не должен импортироваться), служебная учётка `relayo-sync`.
Пароль у всех `Oper_Passw0rd`.
