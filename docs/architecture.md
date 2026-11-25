# Архитектура Relayo: компоненты и связи

Документ описывает, из каких частей состоит Relayo, какую роль выполняет каждая и как они связаны между собой.
Отдельно описаны компоненты для разработки и проверки, включая тестовый сайт.

Связанные документы: [техническое задание](../relayo-tz.md), [развёртывание](deployment.md), [README](../README.md).

## 1. Коротко

Relayo — система поддержки одной компании. Клиенты пишут из веб-виджета на сайтах компании и из Telegram,
операторы отвечают в админке.

Система разделена на две зоны:

- **внутренняя сеть** — данные, бизнес-логика и админка: `Relayo.Internal`, MS SQL Server, RabbitMQ, админка;
- **зона, доступная из интернета** — только `Relayo.Public`. Он обслуживает виджет и вебхуки и **не имеет доступа к БД**:
  читает и меняет данные только через внутренний сервис.

Если публичный сервис взломают, у атакующего не будет ни базы, ни паролей к ней, ни ключей шифрования.

## 2. Общая схема

```mermaid
flowchart LR
  subgraph Internet["Интернет"]
    Visitor["Посетитель сайта<br/>(виджет в браузере)"]
    Site["Сайты компании<br/>(backend выдаёт токен клиента)"]
    TG["Telegram"]
  end

  subgraph Public["Зона для интернета"]
    PUB["Relayo.Public<br/>:5200"]
  end

  subgraph Internal["Внутренняя сеть"]
    ADMIN["Админка<br/>(nginx + React)"]
    INT["Relayo.Internal<br/>:5100 HTTP · :5101 gRPC"]
    PG[("MS SQL Server")]
    MQ[["RabbitMQ"]]
    AD["Active Directory"]
    Operator["Оператор<br/>(браузер)"]
  end

  Visitor -- "HTTPS + WebSocket<br/>API и realtime виджета" --> PUB
  Site -. "токен клиента<br/>(через браузер посетителя)" .-> Visitor
  TG -- "вебхук (режим webhook)" --> PUB
  PUB -- "gRPC + ключ" --> INT
  INT -- "события для виджетов" --> MQ
  MQ -- "fanout" --> PUB
  Operator -- "HTTPS + WebSocket" --> ADMIN
  ADMIN -- "/api, /hubs" --> INT
  INT --> PG
  INT -- "Bot API" --> TG
  INT -- "LDAPS" --> AD
```

Стрелка показывает, кто устанавливает соединение.

## 3. Компоненты

### 3.1. Сервисы

| Компонент | Роль | С кем связан |
|---|---|---|
| **Relayo.Internal** (`src/Relayo.Internal`) | Центр системы. API админки, вся бизнес-логика, работа с БД, движок правил, публикация виджетов и вложения в S3, вызовы Telegram Bot API, синхронизация и вход через AD | MS SQL, RabbitMQ, S3, админка, Relayo.Public (принимает gRPC), Telegram, AD |
| **Relayo.Public** (`src/Relayo.Public`) | Всё, к чему обращаются из интернета: API и realtime виджета, раздача скрипта виджета `core.js`, вебхуки Telegram | Relayo.Internal (gRPC), RabbitMQ, браузеры посетителей, Telegram |
| **Админка** (`web/admin`) | Рабочее место операторов и настройки для администратора. React SPA | Relayo.Internal через `/api` и `/hubs` |
| **Виджет** (`web/widget`) | Окно чата на сайтах компании. Preact в Shadow DOM, ~28 КБ gzip. Сайт подключает опубликованный файл `widget_<ключ>.js` из S3, тот — ядро `core.js` с публичного сервиса | S3 (файл виджета), Relayo.Public |

**Relayo.Internal** слушает два порта:
- **5100** — HTTP API админки (`/api/...`) и realtime операторов (SignalR, `/hubs/operators`);
- **5101** — gRPC для публичного сервиса (HTTP/2). Каждый вызов проверяется по общему ключу в заголовке `x-relayo-internal-key`.

Фоновые задачи внутри Relayo.Internal:

| Задача | Что делает |
|---|---|
| `DatabaseInitializer` | При запуске создаёт базу, если её нет (до старта Wolverine), применяет миграции, создаёт секции таблицы сообщений, первого администратора, в Development — тестовый сайт |
| `MessagePartitionMaintenance` | Раз в 6 часов добавляет границы секций сообщений на следующие месяцы (`SPLIT RANGE` функции секционирования) |
| `DirectorySyncScheduler` | Раз в минуту проверяет, не пора ли синхронизировать папку AD (интервал — в настройках LDAP) |
| `TelegramUpdatesService` | В режиме `polling` забирает сообщения у Telegram (при нескольких экземплярах — только держатель аренды `relayo:lease:telegram-polling` в Redis); в режиме `webhook` при запуске регистрирует вебхуки ботов |
| Очередь `telegram-outgoing` (Wolverine) | Отправляет сообщения и файлы в Telegram с соблюдением лимитов (30 в секунду на бота, 1 в секунду на чат). Файлы читаются из S3 в момент отправки. Хранится в БД и переживает перезапуск |
| Очередь `rules` (Wolverine) | Движок правил: события тикетов (сообщение клиента, ответ оператора, назначение, закрытие, смена статуса) и таймеры «не ответил N минут» (отложенные сообщения в БД). Действия правил новых событий не порождают; не больше 3 автоответов на тикет в час; каждое срабатывание пишется в журнал |
| `TestWidgetsPublisher` | Только Development: публикует тестовые виджеты в S3, если их ещё не публиковали |

**Relayo.Public** держит небольшие кэши, чтобы не ходить во внутренний сервис на каждый запрос:
- настройки виджета — 30 секунд (для проверок на сервере и прямого подключения без S3);
- список доменов всех сайтов для CORS — обновляется раз в минуту.

Оформление виджета на сайтах берётся из опубликованного файла, поэтому оно меняется по кнопке **Publish**,
а не сразу после сохранения. Новые домены сайтов доходят до публичного сервиса примерно за минуту.

### 3.2. Общие библиотеки

| Библиотека | Роль | Кто использует |
|---|---|---|
| **Relayo.Core** (`src/Relayo.Core`) | Предметная область и данные: сущности, сервисы (тикеты, клиенты, виджеты, Telegram, AD, правила), EF Core и миграции | Только Relayo.Internal |
| **Relayo.Contracts** (`src/Relayo.Contracts`) | Контракты между сервисами: gRPC (`Protos/internal_gateway.proto`) и сообщения шины | Оба сервиса |
| **Relayo.ServiceDefaults** (`src/Relayo.ServiceDefaults`) | Общая обвязка: чтение `.env`, проверка настроек при запуске, логи (Serilog, Seq), трассировка (OpenTelemetry), health checks, работа за reverse proxy, маскирование токенов в трассировке | Оба сервиса |

Relayo.Public не ссылается на Relayo.Core. Это проверяет автотест: в зависимостях публичного сервиса не должно быть
ни Core, ни EF Core, ни драйвера SQL Server (`Microsoft.Data.SqlClient`), ни хранилища Wolverine.

### 3.3. Инфраструктура

| Компонент | Роль | Статус |
|---|---|---|
| **MS SQL Server 2019+** | Все данные: пользователи, сайты, виджеты, клиенты, тикеты, сообщения, вложения (описания), боты, правила, настройки AD. Схема `wolverine` — очереди и outbox Wolverine. Таблицы и колонки в snake_case; ключи и токены — с двоичным сравнением строк (`Latin1_General_100_BIN2`), остальное — по умолчанию без учёта регистра | Используется. Локально — контейнер SQL Server 2022 или Azure SQL Edge (arm64) |
| **RabbitMQ 4.3** | Шина между сервисами: события для виджетов (fanout на все экземпляры публичного сервиса) и служебные сообщения | Используется |
| **Seq** | Логи и трассировка | Необязателен |
| **Redis** | Для нескольких экземпляров. Внутренний сервис: SignalR backplane операторов, лимит входа, лимиты отправки в Telegram, аренда опроса Telegram, сброс кэша токенов ботов. Публичный: лимиты на IP (свой Redis в публичной зоне). Все ключи с префиксом `relayo:`. Без `REDIS_HOST` — один экземпляр, всё в памяти; при сбое Redis сервисы работают, но каждый сам по себе (`/health/ready` — Degraded) | Используется, необязателен |
| **S3 (MinIO; локально RustFS)** | Бакет `relayo-widgets`: файлы виджетов `widget_<ключ>.js`, версии `widget_<ключ>.v<N>.js` и картинки кнопок `widget_<ключ>_icon_*` (чтение `widget_*` открыто всем). Бакет `relayo-attachments`: вложения сообщений `<год>/<месяц>/<id>`, закрыт, файлы отдаются по временным ссылкам (`S3_PRESIGNED_URL_MINUTES`). Позже — выгрузки | Используется |

### 3.4. Внешние системы

| Система | Связь с Relayo |
|---|---|
| **Сайты компании** | Подключают виджет одной строкой `<script>`. Backend сайта подписывает токен клиента ключом сайта (HS256), чтобы Relayo знал, кто пишет (п. 3.3.1 ТЗ) |
| **Telegram** | Relayo.Internal вызывает Bot API (отправка, `getUpdates`, регистрация вебхука). Входящие сообщения: в режиме `webhook` Telegram шлёт их на Relayo.Public, в режиме `polling` их забирает Relayo.Internal |
| **Active Directory** | Relayo.Internal читает папку (OU) служебной учёткой для синхронизации и проверяет пароль оператора при входе (LDAP bind). Только LDAPS или StartTLS |

## 4. Каналы связи

| Откуда → куда | Протокол | Защита | Что передаётся |
|---|---|---|---|
| Браузер посетителя → S3 (через прокси) | HTTPS | Только чтение файлов `widget_*` | Файл виджета с настройками и загрузчиком |
| Relayo.Internal → S3 | S3 API | Ключи доступа (`S3_ACCESS_KEY`) | Публикация файлов виджетов, вложения (запись, чтение для Telegram) |
| Браузер посетителя или оператора → S3 (через прокси) | HTTPS | Временная подписанная ссылка на один файл, только GET | Вложения сообщений |
| Браузер посетителя → Relayo.Public | HTTPS, WebSocket (SignalR `/hubs/widget`) | Проверка Origin по доменам сайта, лимит запросов на IP (для сообщений с файлами — отдельный), токен посетителя в заголовке `X-Relayo-Visitor` | Настройки виджета, сообщения с файлами, история, ссылка на Telegram |
| Relayo.Public → Relayo.Internal | gRPC (HTTP/2), порт 5101 | Общий ключ `INTERNAL_GRPC_KEY`, сетевая изоляция | Все операции виджета (файлы посетителей — в самом вызове) и вебхуки Telegram |
| Relayo.Internal → RabbitMQ → Relayo.Public | AMQP | Отдельный пользователь RabbitMQ у публичного сервиса | Новые сообщения для открытых виджетов (`WidgetMessagePublished`) |
| Relayo.Public → RabbitMQ → Relayo.Internal | AMQP | То же | Служебное `PublicServiceStarted` (экземпляр запустился) |
| Relayo.Internal ↔ Redis | RESP (TCP 6379), можно TLS | Пароль `REDIS_PASSWORD` | Лимиты, аренды, SignalR backplane, сброс кэша ботов |
| Браузер оператора → админка → Relayo.Internal | HTTPS, WebSocket (SignalR `/hubs/operators`, сразу WebSocket без negotiate) | JWT (15 минут) + refresh-cookie (`Secure`, `HttpOnly`, `SameSite=Strict`), лимит попыток входа | API админки и события для операторов |
| Relayo.Internal → MS SQL | TDS (TCP 1433) | Логин SQL Server, шифрование соединения обязательно | Все данные |
| Relayo.Internal → api.telegram.org | HTTPS (можно через прокси) | Токен бота в адресе; в логах и трассировке маскируется | Сообщения и обновления Telegram |
| Telegram → Relayo.Public | HTTPS (вебхук) | Секрет вебхука в заголовке, проверяет Relayo.Internal | Входящие сообщения |
| Relayo.Internal → AD | LDAPS / StartTLS | Служебная учётка только на чтение; проверка сертификата | Пользователи папки, проверка пароля при входе |

Наружу (в виджет, в Telegram) из данных сотрудников уходит только **псевдоним** оператора. Логин, ФИО и данные из AD
в публичные API не попадают.

### 4.1. Шина RabbitMQ

| Обменник / очередь | Тип | Кто пишет | Кто читает |
|---|---|---|---|
| `relayo.widget-events` | fanout-обменник | Relayo.Internal | Все экземпляры Relayo.Public |
| `relayo.public.<машина>-<id>` | очередь экземпляра, удаляется при его отключении | — | Свой экземпляр Relayo.Public |
| `relayo.internal` | очередь | Relayo.Public | Relayo.Internal |

У каждого экземпляра публичного сервиса своя очередь, поэтому событие получают все экземпляры, и посетитель
получит ответ, к какому бы экземпляру ни был подключён его виджет.

## 5. Основные сценарии

### 5.1. Посетитель пишет в виджет, оператор отвечает

```mermaid
sequenceDiagram
  participant W as Виджет (браузер)
  participant P as Relayo.Public
  participant I as Relayo.Internal
  participant DB as MS SQL
  participant MQ as RabbitMQ
  participant A as Админка (оператор)

  Note over W: сайт подключает widget_<ключ>.js из S3,<br/>в нём настройки и загрузчик core.js
  W->>P: POST /session (создаёт посетителя)
  P->>I: gRPC StartVisitor
  W->>P: WebSocket /hubs/widget
  W->>P: POST /messages
  P->>I: gRPC SendVisitorMessage
  I->>DB: клиент, тикет, чат, сообщение
  I-->>A: SignalR: новое сообщение, тикет изменён
  I->>MQ: WidgetMessagePublished (другие вкладки посетителя)
  A->>I: POST /api/tickets/{id}/messages
  I->>DB: сообщение оператора
  I->>MQ: WidgetMessagePublished
  MQ->>P: всем экземплярам
  P-->>W: SignalR: ответ с псевдонимом оператора
```

Посетитель создаётся только при первом открытии чата или если в браузере уже есть его токен. Простой просмотр
страниц сайта записей не создаёт.

### 5.2. Файлы в переписке

```mermaid
sequenceDiagram
  participant W as Виджет
  participant P as Relayo.Public
  participant I as Relayo.Internal
  participant S3 as S3 (бакет вложений)
  participant A as Админка
  participant T as Telegram

  W->>P: POST /messages (multipart: текст + файлы)
  P->>P: включены ли файлы у виджета, число, размер, заявленный тип
  P->>I: gRPC SendVisitorMessage (файлы в вызове)
  I->>I: тип по содержимому (сигнатура), размер, имя
  I->>S3: PUT <год>/<месяц>/<id>
  I-->>A: SignalR: сообщение с временными ссылками
  A->>I: POST /api/tickets/{id}/messages (multipart)
  I->>S3: PUT
  I->>T: через очередь telegram-outgoing: sendDocument / sendPhoto (файл читается из S3)
  I-->>W: через RabbitMQ и Public: сообщение с временными ссылками
  W->>S3: GET по временной ссылке (через прокси, S3_PUBLIC_URL)
```

Файлы загружаются в S3 до транзакции с сообщением, чтобы не держать блокировки во время загрузки. Повтор
того же сообщения (тот же `clientMessageId`) файлы заново не загружает. Ссылки живут `S3_PRESIGNED_URL_MINUTES`:
админка и открытый виджет обновляют их раз в 10 минут. Файлы, которые клиент присылает в Telegram, пока
не принимаются: бот отвечает, что они не поддерживаются.

### 5.3. Сайт сообщает, кто клиент: `Relayo.identify(token)`

```mermaid
sequenceDiagram
  participant B as Браузер
  participant S as Backend сайта (например, TestSite)
  participant P as Relayo.Public
  participant I as Relayo.Internal

  B->>S: пользователь вошёл на сайт
  B->>S: GET /token
  S-->>B: JWT HS256 (clientId, locale, exp), kid = id ключа сайта
  B->>P: Relayo.identify(token) → POST /identify
  P->>I: gRPC Identify
  I->>I: ключ по kid → проверка подписи и срока
  I-->>P: посетитель привязан к клиенту (сайт, clientId)
  P-->>B: история переписки клиента
```

Секретный ключ сайта есть только у backend сайта и в Relayo (в БД, зашифрованным). В браузер он не попадает.
Если сайт не может выдавать токены, виджет переключается в режим прямой передачи `Relayo.setClientId()`.
Этот режим небезопасен: такой посетитель видит только свою переписку, а оператор видит пометку «clientId не подтверждён».

### 5.4. Переход из виджета в Telegram

```mermaid
sequenceDiagram
  participant W as Виджет
  participant P as Relayo.Public
  participant I as Relayo.Internal
  participant T as Telegram

  W->>P: POST /telegram-link (заранее, до клика)
  P->>I: gRPC CreateTelegramLink
  I-->>W: t.me/<бот>?start=<одноразовый токен, 10 мин>
  W->>T: клиент открывает ссылку и жмёт Start
  alt режим webhook
    T->>P: POST /telegram/{botId}/webhook
    P->>I: gRPC HandleTelegramUpdate (+ секрет)
  else режим polling
    I->>T: getUpdates
  end
  I->>I: Telegram-аккаунт привязан к посетителю, переписка продолжается в том же чате
  I->>T: sendMessage (через очередь с лимитами)
```

Ссылка запрашивается заранее и обновляется в фоне: браузеры (особенно Safari на iOS) блокируют переход,
если перед ним по клику шёл сетевой запрос.

### 5.5. Вход оператора

- **Локальная учётка:** пароль проверяет Relayo.Internal (хеш в БД).
- **Учётка AD:** пользователь должен быть импортирован из папки AD. Пароль проверяет сам AD: Relayo.Internal
  подключается к нему как `CN=<логин>,<папка>`. Пароль в Relayo не хранится.

После входа админка получает access-токен (JWT, 15 минут, хранится в памяти вкладки) и refresh-токен в cookie.
Cookie браузер сам отправляет на `/api/auth/refresh`, а JavaScript прочитать её не может.

## 6. Где хранятся данные

| Данные | Где | Особенности |
|---|---|---|
| Пользователи, роли, псевдонимы | MS SQL | Пользователи из AD сопоставляются по `objectGUID` |
| Сайты, ключи сайтов, виджеты | MS SQL | Секреты ключей зашифрованы (AES-256-GCM, ключ `SECRETS_ENCRYPTION_KEY`) |
| Клиенты и их каналы (браузер, Telegram) | MS SQL | От токена посетителя хранится только SHA-256 |
| Тикеты, чаты | MS SQL | Тикет 1 → N чатов |
| Сообщения | MS SQL, таблица секционирована по месяцам (функция `pf_messages_month`, схема `ps_messages_month`) | Границы секций создаются заранее |
| Вложения | Описание — MS SQL (`attachments`), файл — S3, бакет вложений | Тип сверяется с содержимым файла; ссылки на скачивание временные |
| Telegram-боты | MS SQL | Токен и секрет вебхука зашифрованы |
| Настройки AD | MS SQL | Пароль служебной учётки зашифрован |
| Правила автоматизации | MS SQL (JSON в `nvarchar(max)`) | С историей изменений и журналом срабатываний |
| Внешний вид виджета (подпись, кнопка, подсказка, пункты) | MS SQL (JSON в `widgets.options`) и опубликованный файл виджета | Картинка кнопки — в бакете виджетов |
| Опубликованные версии виджетов | MS SQL (снимок настроек) и S3 (файлы) | Откат — повторная публикация старой версии |
| Исходящие в Telegram, outbox | MS SQL, схема `wolverine` | Переживают перезапуск |
| Токен посетителя | localStorage браузера | По одному на виджет и домен |

## 7. Среда разработки и тестовые компоненты

Локально инфраструктура и тестовые компоненты запускаются в Docker (`docker-compose.yml` в корне),
а сервисы Relayo и админка — из IDE.

```mermaid
flowchart LR
  subgraph Docker["Docker (docker-compose.yml)"]
    TS["TestSite<br/>:5300 и :5301 (зеркало)"]
    PG[("MS SQL :1433")]
    MQ[["RabbitMQ :5672<br/>консоль :15672"]]
    SEQ["Seq :8081"]
    S3["RustFS (S3) :9000"]
    SAMBA["Samba AD :636<br/>(профиль ad)"]
    REDIS["Redis :6379"]
  end
  LS["tools/test-page<br/>VS Code Live Server :5500"]
  VITE["Админка в Vite :5173"]
  INT["Relayo.Internal :5100/:5101"]
  PUB["Relayo.Public :5200"]

  TS -- "страница с виджетом" --> PUB
  LS -- "страница с виджетом" --> PUB
  VITE -- "прокси /api, /hubs" --> INT
  PUB -- gRPC --> INT
  INT --> PG
  INT <--> MQ
  PUB <--> MQ
  INT -- LDAPS --> SAMBA
  INT -. логи .-> SEQ
  PUB -. логи .-> SEQ
```

RustFS хранит файлы виджетов и вложения. Redis хранит общие счётчики лимитов, аренды и события SignalR.
Проверка нескольких экземпляров: второй внутренний сервис на портах 5110/5111 и `node tools/checks/scale.cjs`.

### 7.1. Тестовый сайт — `tools/TestSite`

Это имитация сайта компании: небольшое ASP.NET-приложение со своим backend. Нужен, чтобы проверять
идентификацию клиентов так, как она будет работать на настоящих сайтах. Его код служит готовым примером для разработчиков сайтов.

| Что | Как устроено |
|---|---|
| Запуск | Контейнер `test-site` из `docker-compose.yml`, пересборка: `docker compose up -d --build test-site` |
| Адреса | `http://localhost:5300` — основной сайт, `http://localhost:5301` — зеркало. У зеркала свой localStorage, на нём проверяется смена зеркала посреди переписки |
| Виджет | Подключён одной строкой — файлом `widget_<ключ>.js` из S3 (`WIDGET_FILES_BASE_URL`, ключи `TEST_WIDGET_*_KEY`). Relayo публикует тестовые виджеты при запуске. Методы `Relayo.*` вызываются после события `relayo:ready` |
| «Вход на сайт» | Кнопки «Sign in as client 1001/1002/1003» (`/login`) и «Sign out» (`/logout`) имитируют авторизацию пользователя сайта (cookie сайта) |
| Выдача токена | `/token` подписывает JWT (HS256, `clientId`, `locale`, срок жизни, `kid`) ключом из `TEST_SITE_SECRET` — так, как это будет делать backend настоящего сайта |
| Негативные проверки | `/token?variant=expired` (просроченный токен), `/token?variant=wrong-key` (чужая подпись) — Relayo должен их отклонить |
| Режимы | По умолчанию виджет в режиме подписанного токена (виджет `…0001`); ссылка «Switch to direct clientId» (`?mode=direct`) переключает на режим прямой передачи (виджет `…0002`) |
| Кнопки виджета | `Relayo.open()`, `Relayo.close()`, `Relayo.open('tg')` и журнал событий виджета на странице |

**Как TestSite связан с Relayo.** При запуске Relayo.Internal в Development (`DEV_SEED_TEST_SITE=true`) создаются:
- тестовый сайт с кодом `TEST_SITE_PRODUCT` и доменами `localhost:5300`, `localhost:5301` и адресами из `PUBLIC_CORS_EXTRA_ORIGINS`;
- ключ сайта `TEST_SITE_KEY_ID` с секретом `TEST_SITE_SECRET` — тем же, которым TestSite подписывает токены;
- два виджета с фиксированными id из `TEST_WIDGET_TOKEN_MODE_ID` и `TEST_WIDGET_DIRECT_MODE_ID` и ключами файлов
  `TEST_WIDGET_TOKEN_MODE_KEY` (`testtoken1`) и `TEST_WIDGET_DIRECT_MODE_KEY` (`testdirect`). Виджеты создаются один раз:
  правки из админки при перезапуске не затираются.

Так TestSite работает сразу после запуска, без настройки в админке. В других окружениях эти данные не создаются.

### 7.2. Страница для Live Server — `tools/test-page`

Простая HTML-страница с виджетом в режиме прямой передачи (файл `widget_testdirect.js`) и полем для `Relayo.setClientId()`.
Открывается через VS Code Live Server (`http://127.0.0.1:5500`). Нужна для быстрой проверки внешнего вида
и прямого режима без backend. Её адреса добавлены в `PUBLIC_CORS_EXTRA_ORIGINS`.

### 7.3. Тестовый AD — `tools/test-ad`

Samba AD DC в контейнере (профиль `ad`) отвечает по тому же протоколу, что настоящий AD, включая `objectGUID` и LDAPS.
Скрипт `tools/test-ad/seed.sh` создаёт:

| Объект | Назначение |
|---|---|
| `OU=Support`: `ivanov`, `petrova`, `sidorov` | Операторы, которых импортирует Relayo |
| `OU=Sales`: `smirnov` | Пользователь другой папки: не должен импортироваться |
| `OU=Service Accounts`: `relayo-sync` | Служебная учётка для синхронизации |

Пароль у всех `Oper_Passw0rd`. Значения для «Settings → LDAP» — в README.

### 7.4. Порты локальной среды

| Порт | Что |
|---|---|
| 5100 / 5101 | Relayo.Internal: API админки / gRPC |
| 5200 | Relayo.Public |
| 5173 | Админка в Vite (проксирует `/api` и `/hubs` на 5100) |
| 5300 / 5301 | TestSite и его зеркало |
| 5500 | Live Server (`tools/test-page`) |
| 1433, 5672, 15672 | MS SQL, RabbitMQ, консоль RabbitMQ |
| 8081, 5341 | Seq: интерфейс, приём логов |
| 9000 / 9001 | RustFS: S3 API / консоль |
| 389 / 636 | Samba AD (профиль `ad`) |

## 8. Где что лежит

| Путь | Содержимое |
|---|---|
| `src/Relayo.Internal` | Внутренний сервис: `Api/` (эндпоинты админки), `Grpc/` (сервер для публичного сервиса), `Channels/Telegram/`, `Messaging/`, `Realtime/` |
| `src/Relayo.Public` | Публичный сервис: `Widget/` (API и hub виджета, CORS), `Telegram/` (вебхук), `Internal/` (gRPC-клиент) |
| `src/Relayo.Core` | Предметная область: `Clients/`, `Conversations/`, `Sites/`, `Channels/`, `Directory/` (AD), `Automation/` (правила), `Users/`, `Auth/`, `Data/` (EF Core, миграции) |
| `src/Relayo.Contracts` | `Protos/internal_gateway.proto`, сообщения шины, имена очередей |
| `src/Relayo.ServiceDefaults` | Общая обвязка сервисов |
| `web/admin` | Админка (React, AntD); меню — `src/layout/menu.tsx` |
| `web/widget` | Виджет (Preact); сборка кладёт `core.js` в `src/Relayo.Public/wwwroot/widget` |
| `tests/Relayo.Tests` | Автотесты, включая проверку изоляции публичного сервиса |
| `tools/` | TestSite, страница для Live Server, наполнение тестового AD |
| `deploy/` | Образы и compose для боевого развёртывания |
| `docs/` | Документация |
