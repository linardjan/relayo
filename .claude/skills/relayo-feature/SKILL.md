---
name: relayo-feature
description: Порядок добавления или изменения функции Relayo сквозь все слои — доменная сущность в Relayo.Core, EF-конфигурация и миграция, сервис, minimal API эндпоинт в Relayo.Internal, gRPC для Relayo.Public, события Wolverine и realtime, API-клиент и страница админки в стиле Usedesk, i18n, тесты и проверка на стенде. Использовать при любой новой функции, поле, разделе админки, настройке или канале.
---

# Новая функция в Relayo: от сущности до экрана

Сначала прочитать `CLAUDE.md` (правила пользователя) и найти ближайший похожий пример — копировать его устройство,
а не изобретать новое. Хороший сквозной образец — группы агентов (`AgentGroup`): сущность, конфигурация, сервис, эндпоинты, `settingsApi`, вкладка на `AgentsPage`.

## 0. Решить, где живёт настройка

- Это сущность, которую администратор меняет во время работы (бот, канал, подключение, расписание, шаблон)? → **таблица + страница в админке**. Не `.env`, не DevSeed.
- Это инфраструктура или секрет развёртывания (адреса, ключи шифрования, порты)? → `.env` + `.env.example` + `deploy/.env.example` + `docs/deployment.md`, проверка при старте в `*Settings.cs`.
- Секрет, вводимый в админке (токен, пароль) → шифровать `SecretProtector`, наружу отдавать только `hasX: bool`, пустое значение при сохранении = «оставить прежнее».

## 1. Домен — `src/Relayo.Core/<Область>/`

- Класс с `private set`, фабричный метод или конструктор и методы изменения с проверками; константы длины (`NameMaxLength`).
- Нарушение правил → `throw new DomainException(ErrorCodes.X, "Понятный текст на английском")`. Новый код ошибки — в `DomainException.cs`
  и при необходимости статус в `src/Relayo.Internal/Api/DomainExceptionHandler.cs` (404/409/502, иначе 400). Текст ошибки админка покажет как есть (`errorText`).
- Сервис-фасад (`XxxService`) с записями `XxxView` (наружу) и `XxxInput` (внутрь); регистрация в `CoreServiceCollectionExtensions`.
- Время — через `TimeProvider`, не `DateTime.Now`.

## 2. Данные — `src/Relayo.Core/Data/`

1. `IEntityTypeConfiguration<T>` в `Configurations.cs` (или профильном файле), `DbSet` в `RelayoDbContext`.
2. Миграция:
   ```bash
   dotnet tool restore
   dotnet ef migrations add <Name> --project src/Relayo.Core --startup-project src/Relayo.Core --output-dir Data/Migrations
   ```
3. Прочитать сгенерированную миграцию: у новых NOT NULL колонок — разумный `defaultValue`/`defaultValueSql`, старые строки заполнены
   (`migrationBuilder.Sql(...)`), случайные значения — `gen_random_uuid()`. Ничего лишнего (ошибочные drop/rename) не попало.
4. Миграция применяется при старте Internal. Применённую миграцию не удалять — исправлять следующей.

## 3. API — `src/Relayo.Internal/Api/`

- Эндпоинты в подходящем `*Endpoints.cs` (настройки — `SettingsEndpoints`, правила — `AutomationEndpoints`, тикеты — `TicketEndpoints`),
  права через `RequireAuthorization(SettingsEndpoints.AdminPolicy)` и т. п. Новый файл — подключить в `Program.cs` рядом с `MapSettingsEndpoints()`.
- Если изменение видят другие операторы или реагируют правила — опубликовать событие (`ConversationEvents`, `AutomationEvents`).
- Внешние вызовы (Telegram, S3, LDAP) — через `IHttpClientFactory`/обёртки в Internal, ошибки сети → понятная `DomainException` (502).

## 4. Публичная часть (только если нужна виджету или вебхуку)

Public не ходит в БД. Нужен новый метод — `src/Relayo.Contracts/Protos/internal_gateway.proto`, реализация в
`src/Relayo.Internal/Grpc/InternalGatewayService.cs`, вызов из Public. Перезапустить оба сервиса. `ArchitectureTests` следят за границей.

## 5. Админка — `web/admin/src/`

- Типы и вызовы — `api/settings.ts` / `api/automation.ts` / `api/tickets.ts` (`api<T>(url, json('POST', body))`).
- Страница: `Page` (хлебные крошки) → `SectionCard` → `SettingsForm` из `layout/Page.tsx`; данные — `useQuery`/`useMutation`,
  после сохранения `invalidateQueries`, `message.success(t('settings.saved'))`, ошибки `message.error(errorText(e, ...))`.
- Раскладка — как в Usedesk (формы — отдельными страницами, списки — таблица с поиском и вкладками). Если пользователь прислал HTML-образец, повторить расположение.
- Маршрут и меню: `layout/menu.tsx` (пункт с `ready: true` и `roles`) и `App.tsx`. Ещё не сделанное — пункт без `ready` → «Coming soon».
- Все тексты — ключи в `i18n/locales/en.json`. Стили — `index.css` с классами, а не большие inline-style.
- `cd web/admin && npx tsc -b --noEmit` (неиспользуемые импорты — ошибка).

## 6. Виджет (если затронут) — `web/widget/src/`

Preact в Shadow DOM, тексты в `texts.ts`, стили в `styles.ts`, публичные методы `Relayo.*` в `controller.ts`.
Следить за размером (~28 KB gzip). `npm run build`. Изменения в виде виджета попадают на сайты, использующие S3-файл, только после «Publish».

## 7. Проверить

1. `dotnet build Relayo.slnx` и `dotnet test --project tests/Relayo.Tests`; для чистой логики (сопоставление, парсинг) — модульный тест.
2. Перезапустить сервисы и проверить на стенде по скиллу `relayo-dev`: нужный скрипт из `tools/checks`; для новой функции — дописать проверку.
3. Для UI — скриншот `node tools/checks/shoot.cjs '/path|name'` и посмотреть его.
4. Убрать тестовые данные.

## 8. Документация

- Поменялось развёртывание или `.env` → `docs/deployment.md`, `deploy/.env.example`, `.env.example`.
- Новый компонент, связь, очередь или сценарий → `docs/architecture.md`.
- Новый шаг ручной проверки → `README.md`. Новый подводный камень → раздел в `CLAUDE.md`.

В конце — коротко пользователю по-русски: что сделано, чем проверено, что осталось.
