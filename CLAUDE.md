# Relayo — заметки для Claude

Система поддержки, заменяющая Usedesk: операторы отвечают клиентам из веб-виджета и Telegram (WhatsApp позже).
ТЗ — [relayo-tz.md](relayo-tz.md), устройство — [docs/architecture.md](docs/architecture.md),
развёртывание — [docs/deployment.md](docs/deployment.md), запуск для разработки — [README.md](README.md).

Скиллы проекта:
- `relayo-dev` — запустить или перезапустить стенд, проверить изменения, сделать скриншоты, посмотреть БД и логи;
- `relayo-feature` — добавить функцию сквозь все слои: сущность → миграция → сервис → эндпоинт → API-клиент → страница админки.

## Карта

| Где | Что |
|---|---|
| `src/Relayo.Core` | Домен и EF Core (PostgreSQL, snake_case). Папки по областям: `Conversations`, `Clients`, `Channels`, `Sites`, `Users`, `Directory` (LDAP), `Automation` (правила), `Auth`, `Security`, `Data` (конфигурации, миграции, DevSeed) |
| `src/Relayo.Internal` | Внутренний сервис: API админки `:5100` (`Api/*Endpoints.cs`), gRPC `:5101` для Public, SignalR `OperatorHub`, Wolverine-обработчики, Telegram (`Channels/Telegram`), правила (`Automation/RuleRunner.cs`), S3 (`Storage`) |
| `src/Relayo.Public` | Публичный сервис `:5200`: API виджета, `WidgetHub`, вебхук Telegram. **Без доступа к БД** — всё через gRPC к Internal (это проверяет `ArchitectureTests`) |
| `src/Relayo.Contracts` | `Protos/internal_gateway.proto` и сообщения шины |
| `src/Relayo.ServiceDefaults` | `.env`, логи, OpenTelemetry, ForwardedHeaders, редактирование секретов в трассах |
| `web/admin` | React 19 + AntD 6 + TanStack Query + react-router + i18next, `:5173` (Vite проксирует `/api` и `/hubs` на 5100) |
| `web/widget` | Preact в Shadow DOM, сборка в `src/Relayo.Public/wwwroot/widget/core.js` |
| `tests/Relayo.Tests` | Модульные тесты: xUnit v3 на Microsoft.Testing.Platform |
| `tools/checks` | Проверки работающего стенда и скриншоты (см. `tools/checks/README.md`) |
| `tools/TestSite` | Тестовый сайт с виджетом в Docker, `:5300` и зеркало `:5301` |
| `deploy/` | Боевые Dockerfile и compose (профили internal/public/storage) |

## Правила, которые задал пользователь

- **Боты, каналы, LDAP/AD и прочие сущности времени работы настраиваются в админке**, а не в `.env` и не в DevSeed.
  В `.env` — только инфраструктура и секреты. Секреты из админки хранятся зашифрованными (`SecretProtector`, AES-GCM) и наружу не отдаются.
- **Админка повторяет раскладку Usedesk** (меню, расположение кнопок, формы-страницы, Inbox в 3 колонки), но не логотипы, название и цвета.
  Нереализованное показывается как «Coming soon» (`ComingSoonPage`, пункт меню без `ready`).
- Пользователь присылает HTML-образцы экранов Usedesk; разметку повторяем по расположению, функциональность — по возможности.
- Общение с пользователем — по-русски. Комментарии в коде — по-русски. Тексты интерфейса — по-английски через i18n
  (`web/admin/src/i18n/locales/en.json`, в виджете `web/widget/src/texts.ts`).
- Коммитить и пушить только по просьбе. `.env` не коммитить никогда.
- Токен Telegram-бота не должен попадать в файлы репозитория, тесты, логи и документацию. В тестах — только явно фейковые токены.
- Не подключать скрипты и стили с CDN.

## Соглашения

- Домен: сущности с приватными сеттерами и методами (`Ticket.SetStatus`, `User.SetProfile`), ошибки — `DomainException(ErrorCodes.X, "text")`,
  в HTTP их переводит `DomainExceptionHandler`. Сервисы Core регистрируются в `CoreServiceCollectionExtensions`.
- Эндпоинты — minimal API в `Api/*Endpoints.cs`, права через политики (`SettingsEndpoints.AdminPolicy` и т. п.).
- Изменения домена, на которые реагируют другие части (realtime, правила, Telegram), публикуются событиями Wolverine
  (`ConversationEvents`, `AutomationEvents`); долгие действия — durable-очереди (`telegram-outgoing`, `rules`).
- Новое поле в БД — миграция в `src/Relayo.Core/Data/Migrations`; у новых NOT NULL колонок задавать default и заполнять старые строки.
  Миграции применяются при старте Internal. Удалить уже применённую миграцию нельзя — делаем следующую.
- Админка: страница = `Page` + `SectionCard` + `SettingsForm` из `layout/Page.tsx`; запросы — `api/*.ts` + TanStack Query;
  маршрут и пункт меню — `layout/menu.tsx` и `App.tsx`.
- Ответ оператора требует псевдонима (alias): клиент видит только его, ФИО — только сотрудники.

## Подводные камни (уже наступали)

- После `dotnet build` работающие сервисы надо **перезапустить** — иначе проверяешь старую сборку.
- Вход в админку ограничен 10 попытками в минуту: не запускать подряд много проверок, которые логинятся.
- После перезапуска Internal публичный сервис переподключается к gRPC за 1–5 с; проверки сразу после рестарта ждут `/health/ready`.
- `DevSeed` только создаёт тестовый сайт и виджеты, существующие не перезаписывает.
- В Chrome headless тестовые страницы с `file://` упираются в Local Network Access — открывать через реальный локальный сервер.
- Compose 2.15: нет `depends_on.required`. nginx: `add_header` не наследуется в `location` — повторять заголовки.
- В миграциях PostgreSQL нет `gen_random_bytes` без pgcrypto — использовать `gen_random_uuid()`.
