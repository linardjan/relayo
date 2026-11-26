# Перенос из Usedesk

План переноса данных и настроек из Usedesk в Relayo (п. 3.8 ТЗ): что лежит в таблицах Usedesk, куда это ложится
в Relayo, чего в Relayo не хватает и как пройти день переключения. Составлено по DDL базы Usedesk (MySQL);
цифры по нашей компании ещё не собраны — запросы для них в разделе 9.

Коротко:

- Данные Usedesk (операторы, клиенты, тикеты, чаты, сообщения) ложатся на модель Relayo без принципиальных
  противоречий: клиент → тикет → чат → сообщение.
- Для переноса в модели не хватало трёх вещей: **id записей Usedesk** (повторный импорт без дублей),
  **внутренних заметок** (иначе клиенты увидят служебные комментарии) и **темы тикета**. Они и остальные столбцы
  высокой и средней важности уже добавлены (раздел 3); осталась сама программа импорта (раздел 5).
- Мессенджеры переносятся с сохранением переписки: Telegram-боты — автоматически (токены есть в БД Usedesk),
  номера WhatsApp — повторным сканированием QR (сессии Usedesk жили в облаке Pact).
- Настройки (сотрудники, почтовые ящики, Telegram-боты, номера WhatsApp, виджеты) переносит временная страница
  «Settings → Import from Usedesk»: Relayo сам читает базу Usedesk (MySQL) — раздел 5.1.
- В БД Usedesk открытым текстом лежат токены ботов, пароли почтовых ящиков и Pact. Выгрузка — секрет (раздел 8).

## 1. Что переносим

По ТЗ: тикеты, чаты и текст сообщений; клиенты — в нужном объёме (сайт, clientId, идентичности в каналах);
операторы — чтобы у сообщений были авторы. Вложения не переносятся. Перенесённые данные не запускают правила,
CSAT и ботов. Импорт можно запускать повторно: история переносится заранее, в день переключения догружаются изменения.

Настройки (виджеты, боты, номера WhatsApp, пункты виджета) переносит страница импорта (раздел 5.1); в день переключения
остаётся включить ботов и отсканировать QR номеров; остальное — вручную (раздел 7).

## 2. Таблицы Usedesk и куда они ложатся

Во всех таблицах Usedesk данные всех компаний экземпляра: нашу отбираем по `company_id`
(у `ticket_comments` его нет — через `tickets`). Нулевые даты MySQL (`0000-00-00 00:00:00`) при чтении переводим в NULL.

### 2.1. `users` → `users`

| Usedesk | Relayo | Как переносим |
|---|---|---|
| `id` | — | **Добавить** `users.usedesk_id`: по нему сообщения находят автора |
| `users_guid_migration.duplicate_guid` (objectGUID из LDAP) | `ad_object_guid` | Главный ключ сопоставления с пользователями из AD. Берём из `usedesk.users_guid_migration` по `original_id` = `users.id`: в самой `users` поле `guid` очищено, чтобы синхронизация Usedesk с LDAP не перезаписывала `name` (раздел ниже). Проверить формат: Usedesk (PHP) может хранить байты не в том порядке, что .NET `Guid` |
| `email` | `email` | Запасной ключ, если GUID в `users_guid_migration` не нашёлся |
| `name` | `alias` | Псевдоним, который видел клиент в Usedesk. Длиннее 100 символов — обрезаем. `full_name` до синхронизации с AD временно равен `name`, синхронизация заменит его на `displayName`; у тех, кого в AD нет, так и остаётся |
| `position`, `phone`, `lang` | `position`, `phone`, `locale` | Как есть |
| `role`: `admin` / `employee` / `support` / `bot` | `role` | `admin` → Admin, `employee` → Operator; `support` (учётки Usedesk) не переносим; сообщения `bot` — автоответы |
| `user_group_id`, `default_group` | `group_ids`, `default_group_id` | Через `agent_groups.usedesk_id` (2.11). Если сотрудник бывает в нескольких группах, их список — в отдельной таблице (запрос в разделе 9) |
| `deleted`, `deleted_at`, `status` | `is_blocked` | Уволенных переносим **заблокированными** — чтобы в истории остались имена |
| `password`, `remember_token`, `token`, `confirmation_code` | — | **Не переносим** (секреты) |
| остальное (`avatar`, подписи, `online_status`, `zendesk_id`, …) | — | Не нужно |

Пользователей переносим **до** синхронизации с AD: синхронизация свяжет их по `ad_object_guid`, а не создаст дубли.
Тех, у кого нашёлся GUID, создаём с `source = ActiveDirectory`: синхронизация сопоставляет по GUID только таких,
а локального пользователя с тем же логином сочтёт конфликтом.
Авторов, которых не нашли вовсе, заменяет служебный пользователь «Импорт из Usedesk» (не может войти).
Псевдоним переносим из `name`: в Usedesk операторы подписывались им перед клиентами, поэтому старые ответы в истории
виджета остаются подписаны так же, а оператор сразу может отвечать. Синхронизация с AD псевдоним не трогает —
она меняет только ФИО, логин, почту, телефон и должность.

### 2.2. `tickets` → `tickets`

| Usedesk | Relayo | Как переносим |
|---|---|---|
| `id` | `number` | Номер = id Usedesk (`IDENTITY_INSERT`), затем счётчик выше максимума: номера, которые знают клиенты, не меняются |
| `company_id` | — | Отбор нашей компании |
| `client_id` | `client_id` | Через `clients.usedesk_id` |
| `status_id` | `status` | Раздел 2.3 |
| `assignee_id` | `assignee_id` | Через `users.usedesk_id` |
| `subject` | `subject` | Как есть, длиннее 255 символов — обрезаем. В списке тикетов тема показывается вместо превью |
| `channel` | `last_client_channel`, `source_channel` | В `source_channel` — значение Usedesk как есть (у всех перенесённых). `last_client_channel` — по таблице ниже |
| — | `source` | `Usedesk` у всех перенесённых |
| `group` | `group_id` | Через `agent_groups.usedesk_id` (2.11) |
| `email_channel_id` | — | Канал Usedesk → наш виджет / бот / номер (раздел 2.7) |
| `social_id` | `client_identities.external_id` | id клиента в мессенджере (чат Telegram, номер WhatsApp): ключ продолжения переписки |
| `priority`, `type` | — | Переносить, только если ими пользовались (`tickets.priority`) |
| `published_at` | `created_at` | Нулевая дата → дата первого сообщения |
| `last_updated_at` | `updated_at` | По нему догрузка (индекс `company_id, last_updated_at`) |
| `status_updated_at` | `closed_at` | Для закрытых |
| — | `last_message_at`, `last_message_preview` | Вычисляются после переноса сообщений |
| `email_channel_subject`, `email_channel_email`, `notification_email`, `contact` | — | Почтовые поля; не нужны, пока почта — только история |
| `telephony_id`, `additional_id` | — | Признак звонков: проверить, есть ли у нас такие тикеты |
| `nps_sent`, `spam_score`, `owner_id`, `ticket_type`, `zendesk_id`, `last_viewed_at`, `status_sort_*` | — | Не нужно |

Ссылки на тикет, в который объединили, в таблице нет — вероятно, она в тексте сообщения `type = merged`.

Значения `tickets.channel` в нашей базе Usedesk (`SELECT DISTINCT channel FROM tickets`):

| `channel` | `last_client_channel` | Что это |
|---|---|---|
| `chat`, `usedesk_chat` | `Widget` | Чат виджета на сайте |
| `telegram` | `Telegram` | Бот Telegram — продолжает работать в Relayo |
| `pact_telegram_personal` | `Telegram` | Личный аккаунт Telegram через Pact — в Relayo такого канала нет, только история |
| `whatsapp` | `WhatsApp` | Номер WhatsApp через Pact |
| `email`, `telephony`, `api` | `Widget` | Почта, звонки, тикеты через API — в Relayo таких каналов нет, только история |

Для каналов, которых в Relayo нет, `last_client_channel` условный (столбец обязательный, а своих значений
`Email` / `Call` / `Other` пока нет); настоящий канал оператор видит в карточке тикета («Imported from Usedesk · email»).
Ответить в такой тикет можно только в виджет, если клиент там появится.

### 2.3. `ticket_statuses` → статус тикета

Набор общий для всего Usedesk (своих статусов у компании нет).

| id | Usedesk | Relayo |
|---|---|---|
| 8 | New | New (нет назначенного) / InProgress (есть) |
| 1 | Opened | New / InProgress — по тому же правилу |
| 6 | In progress | InProgress (без назначенного — New) |
| 5 | Pending | WaitingClient |
| 3 | Closed | Closed |
| 2 | Solved | Closed — см. ниже |
| 10 | Merged | Closed + `merged_into_id`, если цель удастся найти |
| 4 | Deleted | Не переносим |
| 7 | Spam | Не переносим |
| 9 | Delivery | ❓ Выяснить (возможно, исходящие рассылки) |

**Solved.** В Usedesk ответ клиента, скорее всего, снова открывает решённый тикет; в Relayo закрытый тикет не
открывается — создаётся новый. Для истории разницы нет, но тикеты, которые в день переключения стоят в Solved,
при ответе клиента разделятся на два. Варианты: принять это (прошлые тикеты видны в карточке клиента) или добавить
статус Solved с повторным открытием в течение N дней. Решение за руководителем поддержки.

### 2.4. `chats` → `chats` и идентичности клиента

Чат Usedesk — сессия общения внутри тикета.

| Usedesk | Relayo | Как переносим |
|---|---|---|
| `id` | — | **Добавить** `chats.usedesk_id` |
| `ticket_id` (бывает NULL) | `ticket_id` (обязателен) | Чаты без тикета: пропустить или завести закрытый тикет — решить по количеству |
| `client_id` | `client_id` | Через `clients.usedesk_id` |
| `user_id` | `tickets.assignee_id` | Назначение у нас — у тикета |
| `token` | `client_identities` (Visitor, хэш токена) | Посетитель виджета. Если наш виджет найдёт токен Usedesk в localStorage сайта, посетитель увидит свою историю (раздел 4, п. 5) |
| `platform`, `channel_id` | `messages.channel`, наш канал | Платформа и канал Usedesk (раздел 2.7) |
| `additional_id` | `client_identities.external_id` | Вероятно, id в мессенджере — проверить на примерах |
| `url`, `browser` | — | **Добавить** `chats.page_url`, `chats.user_agent` (нужны операторам и без миграции) |
| `email` | — | Email из формы перед чатом; переносить, если нужен операторам (`clients.email`) |
| `created_at` / `connected_at`, `last_comment_at` | `created_at`, `last_message_at` | Как есть |
| `html` | — | ❓ Похоже на расшифровку чата (дубль сообщений). Проверить: если для какой-то платформы сообщения есть только здесь, подход меняется |
| `socket_id`, `online`, `disconnected_at`, `is_missed`, `missed_at`, `rised_at`, `bot_id`, `company_id` | — | Не нужно (`company_id` — отбор) |

### 2.5. `ticket_comments` → `messages`

Сообщения, несмотря на имя. Их около 118 млн во всём экземпляре.

| Usedesk | Relayo | Как переносим |
|---|---|---|
| `id` | — | **Добавить** `messages.usedesk_id` (int) и индекс `(usedesk_id, created_at)` |
| `ticket_id` | `chat_id` | **Связи с чатом нет.** Один чат на тикет; если чатов Usedesk у тикета несколько — по времени сообщения между `connected_at` и `last_comment_at` |
| `published_at` | `created_at` | Единственная дата сообщения. Нулевую заменяем датой предыдущего сообщения тикета (всегда одинаково — иначе повторный импорт создаст дубли). По дате сообщение ложится в секцию |
| `type = public` | обычное сообщение | |
| `type = private` | — | **Внутренняя заметка**: добавить `messages.visibility` |
| `type = info`, `log`, `error`, `merged` | `author = System` | Только для сотрудников (`visibility`). `log` можно не переносить — решить по примерам |
| `from = client` | `author = Client` | Идентичность — из чата или тикета (канал, `social_id`, `token`). От неё зависит, куда уйдёт первый ответ после переключения |
| `from = user` | `author = Operator`, `operator_id` | Через `users.usedesk_id` |
| `from = trigger` | автоответ (`author = Operator`, `operator_id` пуст) | Как наши автоответы правил |
| `message`, `is_html` | `text` | HTML → текст: переносы строк, ссылки «текст (url)», цитаты писем срезать. Предел у нас 10 000 символов — длинные письма обрезаются с пометкой |
| — | — | Вложения не переносим: в текст добавляется пометка «[Файл из Usedesk не перенесён: имя]» (раздел 2.6). У сообщения только с файлом текстом становится сама пометка |
| канала у сообщения нет | `channel` | Из тикета (`channel`) или чата (`platform`) |
| `by_mandrill`, `mandrill_*`, `extra_headers` | — | Признаки почты |
| `vk_id`, `twitter_id` | — | Каналы ВК и Twitter — проверить, есть ли у нас |
| `group_id`, `time`, `trigger_id`, `is_first` | — | Не нужно (`time`, вероятно, время ответа для SLA) |

### 2.6. `ticket_comment_attachments` → пометки о файлах

Вложения по ТЗ не переносятся; таблица нужна, чтобы оператор видел, что файл был.

| Usedesk | Как используем |
|---|---|
| `ticket_comment_id` | К какому сообщению добавить пометку |
| `attachment_name` | Имя файла в пометке «[Файл из Usedesk не перенесён: имя]» |
| `is_inline`, `content_id` | Картинки внутри HTML писем (`cid:`): логотипы, подписи, скриншоты в тексте. **Пометку не добавляем**, иначе почти каждое письмо получит «[logo.png]»; в тексте письма такая картинка заменяется на «[картинка]» |
| `company_id` | Отбор нашей компании напрямую, без `tickets` |
| `id`, `file_id` (UUID в `binary(16)`) | Сам файл — в другой таблице (по `file_id`). Понадобится, только если решим всё же переносить файлы |
| `ticket_id`, `client_id`, `user_id`, `created_at`, `updated_at` | Не нужно |

Если файлы позже понадобятся, перенос возможен: по `file_id` найти, где Usedesk хранит содержимое, и положить файлы
в наш S3 (`attachments`). Это отдельное решение — объём файлов может быть большим.

### 2.7. `company_email_channels` → каналы

Центральная таблица каналов Usedesk — всех видов, не только почты. На неё ссылаются `tickets.email_channel_id`,
`chats.channel_id`, `usedesk_widget.company_email_channel_id`, `telegram_settings.channel_id` (строкой),
`messenger_settings.channel_id`, `pact_settings.channel_id`.

| Usedesk | Relayo | |
|---|---|---|
| `id`, `name`, `type`, `sub_type` | виджет / Telegram-бот / номер WhatsApp | Тип определяет, во что превращается канал: виджет — через `usedesk_widget`, Telegram — через `telegram_settings`, WhatsApp — через `pact_settings`; почта, ВК и прочие — только история |
| `group_id` | — | Группа по умолчанию для тикетов канала (`user_groups.id`): понадобится для распределения; у каналов Relayo такого поля пока нет |
| `deleted`, `deleted_at` | — | Удалённые каналы встречаются в старых тикетах: такие тикеты — только история |
| `auto_reply`, `auto_reply_message` | правило | Заменяется нашим правилом |
| `csi` | — | Оценка (CSAT) по каналу — когда будет CSAT |
| `key`, `token`, `redirect_url` | — | Ключи канала (вероятно, виджета) — не переносим |
| `imap_*`, `smtp_*`, `from_*`, `signature`, `quotes`, `bcc`, `notification_*` | `email_channels` (вручную) | Почта. В Relayo есть страница почтового канала (`imap_*` / `smtp_*`, «Цитирование»), пока без приёма и отправки писем. Пароли в Usedesk — открытым текстом, программой не переносим: администратор вводит их на странице канала |

Таблица сопоставления «канал Usedesk → наш канал» составляется один раз (их немного) и передаётся импорту.

### 2.8. Настройки каналов

**`usedesk_widget` → `widgets`.** Переносит страница импорта (раздел 5.1); виджет создаётся неопубликованным.

| Usedesk | Relayo |
|---|---|
| `name`, `title`, `color`, `aligment`, `language`, `attachment`, `z_index`, `hide_when_clicked_outside`, `operator_name`, `button_color` | `name`, `title`, `accent_color`, `position`, `default_locale`, настройки `options` |
| `badge_*`, `show_tooltip_type` | подсказка (`Tooltip*`) |
| `main_icon` | своя картинка кнопки — загрузить заново |
| `company_email_channel_id` | ключ сопоставления чатов с виджетом |
| `topics*`, `custom_fields`, `ticket_fields`, `callback_*`, `allow_rating`, `docs` | ❌ Выбор темы, форма перед чатом, обратный звонок, оценка, база знаний — у нас нет; делать то, что включено у наших виджетов |
| `offline_answer` | правило `out_of_hours_message` |

Язык виджетов Usedesk — `RU`, а у нашего виджета пока только английские тексты: **русские тексты нужны до переключения.**
Приветствия в таблице нет — найти, где оно хранится.

**`messenger_settings` → пункты виджета.** `active_json` — список пунктов в том же виде, что наша «Настройка пунктов»:

| `type` в `active_json` | Relayo |
|---|---|
| `CHAT_CALLBACK_FORM` | пункт `Chat` |
| `TG` (`externalLink` = ссылка на бота) | пункт `Telegram` + бот |
| `WHATSAPP` (`externalLink` = номер) | пункт `WhatsApp` + канал с этим номером |
| `facebook_url`, `vk_url`, `viber_url`, `ok_url`, `insta_url`, `skype_name` | пункты `Link` |

**`telegram_settings` → `telegram_bots`.** Токены ботов в открытом виде: страница импорта переносит ботов сама
(проверка токена через getMe, хранение зашифрованным) — **выключенными и без вебхука**. У бота один вебхук: включение
в Relayo выключает бота в Usedesk, поэтому включать **только в день переключения**. Id чатов Telegram не меняются: старые клиенты
бота продолжают переписку в своих тикетах.

**`pact_settings` → `whatsapp_channels`.** WhatsApp в Usedesk работал через Pact — облачный шлюз с подключением по QR
(образец страницы канала WhatsApp — это экран Pact). Сессии хранятся в Pact, перенести их нельзя: каждый номер
в день переключения сканируется в Relayo заново, затем устройство Pact отвязывается в телефоне — иначе сообщения
будут получать оба. `group_chats` — получать групповые чаты (у нас пока нет). Логин и пароль Pact не переносим.

### 2.9. `clients` → `clients`

Клиент Usedesk — общий для всей компании; почты, телефонов и мессенджеров в самой таблице нет (они в отдельных
таблицах контактов, ещё не разобраны — запрос в разделе 9).

| Usedesk | Relayo | Как переносим |
|---|---|---|
| `id` | `usedesk_id` (уже есть) | Ключ повторного импорта; по нему `tickets.client_id`, `chats.client_id`, `ticket_comments.client_id` находят клиента |
| `company_id` | — | Отбор нашей компании |
| `name` | `display_name` | Длиннее 200 символов — обрезаем. Имя видят операторы, им же подставляется `{client.name}` в автоответах |
| `lang` | `locale` | Как есть (`ru`, `en`…), пустое — NULL |
| `created_at` | `created_at` | NULL — дата первого тикета клиента |
| `note` | `note` | Заметка для сотрудников (до 4000 символов, длиннее — обрезаем). Видна на странице клиента и в панели Inbox; нужна условию правил «Заметка клиента» (п. 3.6.1 ТЗ) |
| `position` | `position` | Как есть, до 200 символов |
| `vip` | `is_vip` | Как есть (столбец 18, добавлен) |
| `spammer` | `is_spammer` | Как есть. Пока только пометка; блокировать сообщения спамеров — с чёрным списком |
| `client_company_id` | — | Компании клиентов: в Relayo их нет (меню «Companies» — «Coming soon») |
| `deleted_at` | — | Удалённых без перенесённых тикетов не переносим; с тикетами — переносим ради истории |
| `avatar` | — | Аватар у нас — файл в S3 (`avatar_key`); картинки Usedesk не переносим |
| `zendesk_id`, `updated_at` | — | Не нужно |

### 2.10. `client_identity_map` → clientId сайта

Здесь Usedesk хранит clientId, по которому сайт опознаёт клиента в виджете. Без него перенесённый клиент был бы
без clientId, и при первом входе на сайте с новым виджетом Relayo создал бы второго клиента — история разделилась бы.

| Usedesk | Relayo | Как переносим |
|---|---|---|
| `client_id` (varchar 255) | `clients.external_client_id` | clientId сайта — как есть. У нас до 128 символов и с учётом регистра: длину проверить запросом (раздел 9) |
| `client_brand` | `clients.site_id` | Product сайта → наш сайт по коду (`sites.code`). Бренды, для которых сайта нет, перечислить заранее: завести сайты или сопоставить вручную |
| `canonical_client_id` | `clients.usedesk_id` | id клиента Usedesk (внешний ключ на `clients.id`) |
| `id` | — | Не нужно |

Своего `company_id` у таблицы нет: отбор — через `clients.company_id`.

**Клиенты Relayo из этой таблицы.** Каждая строка — клиент Relayo на сайте бренда с этим clientId (как после
прямой передачи clientId). Он не подтверждён (`is_verified = 0`): первый `Relayo.identify(token)` с тем же clientId
найдёт этого клиента и подтвердит его, так что история продолжится. Имя, язык, дата создания — из `clients` (2.9).

**Один клиент Usedesk → несколько клиентов Relayo.** Если у клиента Usedesk строки с разными брендами, в Relayo
это разные клиенты на разных сайтах (у нас клиент принадлежит сайту). Тикеты и чаты раскладываются по ним по сайту
канала тикета (канал Usedesk → наш виджет, бот или номер → сайт). Поэтому уникальность `clients.usedesk_id`
(сейчас уникален сам по себе, столбец 4 раздела 3) нужно заменить на уникальность пары (`usedesk_id`, `site_id`).

**Несколько clientId у одного клиента на одном сайте** (уникален только `(client_id, client_brand)`, так бывает
после объединения клиентов в Usedesk). У клиента Relayo один clientId, поэтому: основной — по последнему тикету,
остальные — отдельными клиентами без истории или пропустить. Решить по количеству (запрос в разделе 9).

**Клиенты без строки в таблице** — те, кто писал только в мессенджер или анонимно в виджет. Переносятся без
clientId на сайт канала тикета, с идентичностями из каналов: токен виджета (`chats.token`), чат Telegram, номер
WhatsApp.

### 2.11. `user_groups` → `agent_groups`

Группы сотрудников (отделы). Их немного — переносятся страницей импорта, **до сотрудников**: тогда сотрудникам
сразу проставляются группы. Раздела «Groups» на странице импорта пока нет (раздел 5.1).

| Usedesk | Relayo | Как переносим |
|---|---|---|
| `id` | `agent_groups.usedesk_id` | Столбец 17 (добавлен): повторный импорт без дублей; по нему `users.user_group_id`, `users.default_group`, `tickets.group`, `company_email_channels.group_id` находят группу |
| `name` | `name` | У нас до 100 символов и уникально: длиннее — обрезаем, совпадение с уже заведённой группой — привязываем к ней, а не создаём вторую |
| `company_id` | — | Отбор нашей компании |
| `deleted`, `deleted_at` | — | Удалённые группы не переносим. Если на них ссылаются перенесённые тикеты, у таких тикетов группа будет пустой — либо перенести их с пометкой в имени; решить по количеству (запрос в разделе 9) |
| `is_demo` | — | Демо-группы не переносим |
| `custom_working_time`, `timezone` | — | Своё рабочее время группы: понадобится вместе с рабочими часами (п. 3.5, 3.6 ТЗ) |
| `client_personal_data_access` | — | Скрывать от группы персональные данные клиентов: в Relayo такого ограничения нет; если где-то выключено — решить, нужно ли |
| `is_workflow_enabled` | — | Не нужно |

## 3. Какие столбцы добавить в Relayo

По убыванию важности. Миграцией EF, как обычно; у новых NOT NULL столбцов — значение по умолчанию.

**Столбцы 1–10 и 16–20 добавлены**, у столбца 4 уникальность — пара (`usedesk_id`, `site_id`); все миграции до первого
выпуска объединены в одну `Initial` (26.11.2026). Вместе с ними: внутренние заметки
операторов (вкладка «Comment» у поля ответа), выбор группы в карточке тикета, тема и пометка «Imported from Usedesk»
в карточке, страница сайта у чата (виджет передаёт её с каждым сообщением, у чата запоминается первая).
Проверка на стенде — `tools/checks/notes.cjs`. Столбцы 11–15 — по решению, они не блокируют перенос.

| # | Таблица.столбец | Тип | Из Usedesk | Зачем | Важность |
|---|---|---|---|---|---|
| 1 | `messages.visibility` | `nvarchar(16)` NOT NULL, по умолчанию `Public` (`Public` / `Internal`) | `ticket_comments.type` (`private`, `info`, `log`, `error`, `merged` → `Internal`) | Без него перенос показывает клиентам служебные комментарии в истории виджета. Внутренние сообщения не уходят в виджет, Telegram и WhatsApp. Нужно и без миграции: заметки есть в Usedesk | Блокирует перенос |
| 2 | `messages.usedesk_id` | `int` NULL, индекс `(usedesk_id, created_at)` | `ticket_comments.id` | Повторный импорт и догрузка без дублей. Таблица секционирована, поэтому уникальность — с датой | Блокирует перенос |
| 3 | `chats.usedesk_id` | `bigint` NULL, уникальный (без NULL) | `chats.id` | То же для чатов | Блокирует перенос |
| 4 | `clients.usedesk_id` | `bigint` NULL, уникальна пара с `site_id` (без NULL) | `tickets.client_id`, `chats.client_id` | Привязка тикетов и сообщений к клиенту, повторный импорт | Блокирует перенос. Уникальна пара (`usedesk_id`, `site_id`): клиент Usedesk с несколькими брендами — несколько клиентов Relayo (2.10) |
| 5 | `users.usedesk_id` | `int` NULL, уникальный (без NULL) | `users.id` | Автор сообщения (`ticket_comments.user_id`), назначенный (`assignee_id`) | Блокирует перенос |
| 6 | `tickets.subject` | `nvarchar(255)` NULL | `tickets.subject` | Тема письма у почтовых тикетов; столбец списка, как в Usedesk | Высокая |
| 7 | `tickets.source` | `nvarchar(16)` NOT NULL, по умолчанию `Relayo` (`Relayo` / `Usedesk`) | — | Пометка перенесённых: правила, отчёты, интерфейс | Высокая |
| 8 | `tickets.source_channel` | `nvarchar(50)` NULL | `tickets.channel` | Исходный канал как в Usedesk (`email`, `telephony`, `api`…), значения — раздел 2.2 | Высокая |
| 9 | `tickets.group_id` | `uniqueidentifier` NULL → `agent_groups` | `tickets.group`, `company_email_channels.group_id` | Очереди и распределение по группам | Средняя |
| 10 | `chats.page_url` | `nvarchar(2000)` NULL | `chats.url` | Со страницы какого сайта и раздела написал клиент; наш виджет сохраняет страницу первого сообщения чата | Средняя |
| 11 | `chats.user_agent` | `nvarchar(500)` NULL | `chats.browser` | Браузер и устройство клиента для операторов | Низкая |
| 12 | `tickets.priority` | `nvarchar(16)` NULL | `tickets.priority` | Только если приоритетом пользовались | Низкая |
| 13 | `clients.email` | `nvarchar(320)` NULL | `chats.email` | Только если форма перед чатом нужна операторам | Низкая |
| 14 | `whatsapp_channels.receive_group_chats` | `bit` NOT NULL, по умолчанию 0 | `pact_settings.group_chats` | Только если групповые чаты использовались | Низкая |
| 15 | `tickets.source_status` | `nvarchar(32)` NULL | `ticket_statuses.key` | Исходный статус (Solved, Delivery) для разбора спорных случаев | Низкая |
| 16 | `clients.note` | `nvarchar(4000)` NULL | `clients.note` | Заметка о клиенте для сотрудников; условие правил «Заметка клиента» (добавлен) | Средняя |
| 17 | `agent_groups.usedesk_id` | `int` NULL, уникальный (без NULL) | `user_groups.id` | Группы сотрудников и тикетов при переносе, повторный импорт | Блокирует перенос групп (добавлен) |
| 18 | `clients.is_vip` | `bit` NOT NULL, по умолчанию 0 | `clients.vip` | VIP-клиенты (добавлен) | Обязательно |
| 19 | `clients.position` | `nvarchar(200)` NULL | `clients.position` | Должность клиента (добавлен) | Низкая |
| 20 | `clients.is_spammer` | `bit` NOT NULL, по умолчанию 0 | `clients.spammer` | Пометка «Спамер» (добавлен) | Низкая |

Номер тикета отдельного столбца не требует: `tickets.number` = id Usedesk.

## 4. Другие изменения (не столбцы)

1. **Секции сообщений за старые месяцы.** `MessagePartitions` создаёт секции только с прошлого месяца: до импорта
   добавить границы за весь период истории, иначе она ляжет в одну секцию.
2. **Номера тикетов:** импорт с `IDENTITY_INSERT`, затем `DBCC CHECKIDENT` выше максимального номера.
   Если Relayo начнёт принимать обращения раньше переноса истории, **до запуска** поднять счётчик номеров выше
   максимального id тикета Usedesk (`AUTO_INCREMENT` таблицы `tickets` — 9 216 185 на момент DDL): иначе новые номера
   совпадут с номерами старых тикетов, и перенести их под своими номерами не получится.
3. **Значения канала** `Email`, `Call`, `Other` — не добавлены: исходный канал хранится в `tickets.source_channel`
   (раздел 2.2). Добавить, если понадобятся значки этих каналов в списке тикетов или условия правил по ним.
4. **Русские тексты виджета** — до переключения.
5. **Токен Usedesk в виджете:** при первом запуске наш виджет читает токен посетителя Usedesk из localStorage сайта
   и обменивает на свой (нужно имя ключа). Перенесённые токены — Visitor-идентичности с хэшем.
6. **Полнотекстовый поиск** по `messages.text`: без него перенесённая история мало полезна.
7. **Решения**: статус Solved с повторным открытием; теги и доп. поля (меню уже есть, «Coming soon»); история оценок;
   функции виджета (форма перед чатом, темы, обратный звонок, оценка) — по тому, что включено у наших виджетов.

## 5.1. Импорт настроек в Relayo (сделан)

Страница «Settings → Import from Usedesk» (только администратор, временная). Relayo.Internal подключается к MySQL
Usedesk (сервер, БД, пользователь с правом чтения, `company_id` нашей компании; пароль хранится зашифрованным
до кнопки «Forget connection»), показывает предпросмотр по разделам и переносит выбранное тем же кодом, что админка.
Повторный импорт ничего не дублирует: у пользователей — `usedesk_id`, у каналов и виджетов — `usedesk_channel_id`
(`company_email_channels.id`; по нему же перенос истории найдёт канал тикета). Код — `src/Relayo.Internal/Usedesk`.

| Раздел | Откуда | Что создаётся |
|---|---|---|
| Agents | `users` (+ `users_guid_migration`) | admin/employee; с objectGUID — пользователь AD (синхронизация свяжет, а не создаст второго), без — локальный без пароля; уже заведённый в Relayo (GUID, почта, логин) только привязывается. Псевдоним — `name`. Удалённые — заблокированными. `support`, `bot` — нет |
| Email | `company_email_channels` с IMAP | Почтовый канал с паролями IMAP/SMTP (зашифрованы). `internal`, `gmail`, `microsoft_graph` — нет; пароли, зашифрованные Laravel (`eyJpdiI6…`), — вводятся вручную |
| Telegram | `telegram_settings` | Бот **выключенным, вебхук не трогается**: в Usedesk он работает дальше. Telegram только спрашивается `getMe`. В день переключения бота включают на его странице — вебхук переходит в Relayo |
| WhatsApp | `pact_settings` (не удалённые, провайдер WhatsApp) | Номер в режиме QR без подключения: в день переключения сканируется QR, затем отвязывается устройство Pact |
| Widgets | `usedesk_widget` + `messenger_settings` | Неопубликованный виджет: заголовок, цвета, сторона, язык, подсказка, вложения, z-index, пункты (ссылки http(s); Telegram — если бот из ссылки уже в Relayo). Чего у нас нет (темы, форма перед чатом, обратный звонок, оценка), перечислено в пометках |

Порядок: Agents → импорт из AD → Telegram и WhatsApp → Email → Widgets. Формат `active_json` не задокументирован:
понимаются массив `{"type", "externalLink"}` и объект «тип → включён»; непонятное — в пометках, остаётся пункт чата.
Проверка на стенде — `tools/checks/usedesk-import.cjs` с тестовой MySQL (`docker compose --profile usedesk up -d`,
схема из DDL Usedesk и фейковые данные — `tools/usedesk-mock`).

## 5. Программа импорта

- Отдельная консольная программа, не через сервисы Relayo: чтение из MySQL пакетами, запись `SqlBulkCopy`.
  События, правила, CSAT и боты не срабатывают.
- **Порядок:** группы → пользователи → клиенты и идентичности → тикеты → чаты → сообщения → вычисляемые поля
  тикетов (`last_message_at`, превью, `last_client_channel`).
- **Повторный прогон** — по `usedesk_id`: существующие записи обновляются, новые добавляются.
- **Догрузка:** тикеты с `last_updated_at` после прошлого прогона, сообщения с `id` больше последнего перенесённого.
- **Сопоставления** (готовятся заранее, их немного): каналы Usedesk → виджеты, боты и номера Relayo; группы.
- **Идентичности клиентов:** виджет — хэш `chats.token`; Telegram — `{наш бот}:{chat_id}`;
  WhatsApp — `{наш номер}:{номер клиента}@s.whatsapp.net` (номер — из `social_id` или контактов клиента).
- **Тексты:** HTML → текст, срезка цитат, пометки о вложениях (кроме встроенных в письма), обрезка длиннее
  10 000 символов с пометкой.
- **Проверка после прогона:** число тикетов и сообщений по месяцам и каналам в Usedesk и в Relayo, выборочное
  сравнение переписки.

## 6. День переключения

1. Последняя догрузка данных; после неё Usedesk для операторов — только чтение.
2. Включение перенесённых Telegram-ботов на их страницах (вебхуки переходят на Relayo, бот в Usedesk замолкает).
3. Номера WhatsApp: сканирование QR в Relayo, затем отвязка устройства Pact в телефоне.
4. Виджеты опубликованы; на сайтах — новый код вставки и вызовы `Relayo.identify(token)` / `Relayo.setClientId(id)`
   вместо вызовов API Usedesk (список сайтов и способ идентификации на каждом — собрать заранее).
5. Почта: решить, куда пойдут ответы клиентов на старые письма — в Relayo пока только настройки почтового канала, без приёма и отправки писем.
6. После: новые токены ботов (`/revoke` в @BotFather → «New token» в Relayo), закрытие Pact, удаление выгрузки Usedesk.

## 7. Что переносится вручную

| Что | Где в Usedesk | Куда |
|---|---|---|
| Сайты и домены | — | «Settings → Sites» |
| Виджеты: проверка после импорта, картинка кнопки, приветствие, публикация | `usedesk_widget` | «Channels → Widget» |
| Пункт WhatsApp виджета (номер известен после QR) | `messenger_settings.active_json` | «Настройка пунктов» на странице виджета |
| Автоответы каналов | `company_email_channels.auto_reply_message`, `usedesk_widget.offline_answer` | Правила |
| Группы | `user_groups` | Страница импорта (раздел «Groups» — сделать, 2.11) |

## 8. Безопасность

В БД Usedesk открытым текстом: токены ботов (`telegram_settings.token`), пароли почтовых ящиков
(`company_email_channels.imap_password`, `smtp_password`), токены и пароли Pact (`pact_settings`), API-токены
операторов (`users.token`).

- Выгрузку Usedesk хранить как секрет: узкий доступ, не пересылать, удалить после переноса.
- Импорт читает токены ботов прямо из БД и сразу сохраняет их зашифрованными. Не выгружать их в файлы и не писать
  в логи (правило проекта для токенов ботов).
- Пароли почтовых ящиков переносит страница импорта — сразу зашифрованными. Пароли и токены Pact, токены операторов не переносить.
- После переключения выпустить новые токены ботов.

## 9. Открытые вопросы

| Вопрос | Как выяснить |
|---|---|
| Сколько тикетов по каналам (список значений уже есть — раздел 2.2) | Запрос 1 |
| Сколько тикетов в каждом статусе, что такое Delivery | Запрос 2 |
| Как записано объединение тикетов | Запрос 3 |
| Типы сообщений, HTML, длина, нулевые даты | Запрос 4 |
| Что в `chats.html`, `chats.additional_id`, `tickets.social_id` | Запрос 5 |
| Какие функции включены у наших виджетов | Запрос 6 |
| Сколько вложений и сообщений только с файлом | Запрос 8 |
| Номера WhatsApp в Pact, групповые чаты | Запрос 7 |
| Формат `users_guid_migration.duplicate_guid` | Один оператор: значение в таблице и `Get-ADUser <логин> \| select ObjectGUID` |
| Ключ localStorage виджета Usedesk | DevTools на сайте с виджетом Usedesk |
| Нужны ли теги, доп. поля, оценки; почта после переключения; статус Solved | Решение руководителя поддержки |
| Бренды без сайта, длина clientId, несколько clientId на сайте, контакты клиента | Запросы «Клиенты» в конце раздела; DDL таблиц контактов (почта, телефоны) |
| Ещё не разобраны | Контакты клиентов, группы пользователей, приветствие виджета; таблица файлов — только если переносим вложения |

```sql
-- 1. Каналы и объём
SELECT channel, COUNT(*), MIN(published_at), MAX(published_at)
FROM tickets WHERE company_id = ? GROUP BY channel;
SELECT id, name, type, sub_type, deleted FROM company_email_channels WHERE company_id = ?;

-- 2. Статусы
SELECT status_id, COUNT(*), MAX(last_updated_at) FROM tickets WHERE company_id = ? GROUP BY status_id;
SELECT channel, COUNT(*) FROM tickets WHERE company_id = ? AND status_id = 9 GROUP BY channel;

-- 3. Объединение
SELECT c.ticket_id, LEFT(c.message, 200) FROM ticket_comments c JOIN tickets t ON t.id = c.ticket_id
WHERE t.company_id = ? AND c.type = 'merged' LIMIT 5;

-- 4. Сообщения
SELECT c.type, c.`from`, c.is_html, c.by_mandrill, COUNT(*), MAX(CHAR_LENGTH(c.message))
FROM ticket_comments c JOIN tickets t ON t.id = c.ticket_id WHERE t.company_id = ? GROUP BY 1, 2, 3, 4;
SELECT SUM(c.published_at = '0000-00-00 00:00:00'), SUM(c.published_at > NOW()), COUNT(*)
FROM ticket_comments c JOIN tickets t ON t.id = c.ticket_id WHERE t.company_id = ?;

-- 5. Чаты и идентичности (значения замаскировать)
SELECT platform, COUNT(*), SUM(ticket_id IS NULL) FROM chats WHERE company_id = ? GROUP BY platform;
SELECT platform, channel_id, LEFT(token, 8), LEFT(additional_id, 5), LENGTH(html)
FROM chats WHERE company_id = ? AND platform = ? LIMIT 2;
SELECT channel, LEFT(social_id, 5), CHAR_LENGTH(social_id) FROM tickets
WHERE company_id = ? AND social_id IS NOT NULL LIMIT 10;

-- 6. Виджеты
SELECT id, name, company_email_channel_id, language, show_type, topics_required,
       custom_fields, ticket_fields, chat_callback_enabled, allow_rating, docs, offline_answer
FROM usedesk_widget WHERE company_id = ?;

-- 7. WhatsApp (Pact)
SELECT id, channel_id, provider_type, is_business, group_chats, is_active
FROM pact_settings WHERE company_id = ? AND deleted_at IS NULL;

-- 8. Вложения: обычные и встроенные в письма; сообщения только с файлом
SELECT is_inline, COUNT(*) FROM ticket_comment_attachments WHERE company_id = ? GROUP BY is_inline;
SELECT COUNT(DISTINCT a.ticket_comment_id) FROM ticket_comment_attachments a
JOIN ticket_comments c ON c.id = a.ticket_comment_id
WHERE a.company_id = ? AND a.is_inline = 0 AND TRIM(c.message) = '';

-- Клиенты: сколько, удалённые, заметки, компании, VIP и спамеры
SELECT COUNT(*), SUM(deleted_at IS NOT NULL), SUM(note IS NOT NULL AND note <> ''),
       SUM(client_company_id IS NOT NULL), SUM(vip), SUM(spammer)
FROM clients WHERE company_id = ?;
-- clientId сайтов: бренды (должны совпасть с кодами сайтов Relayo), длина clientId
SELECT m.client_brand, COUNT(*), MAX(CHAR_LENGTH(m.client_id)), SUM(CHAR_LENGTH(m.client_id) > 128)
FROM client_identity_map m JOIN clients c ON c.id = m.canonical_client_id
WHERE c.company_id = ? GROUP BY m.client_brand;
-- Клиенты с несколькими брендами и с несколькими clientId в одном бренде
SELECT SUM(brands > 1), SUM(ids > brands) FROM (
  SELECT m.canonical_client_id, COUNT(DISTINCT m.client_brand) brands, COUNT(*) ids
  FROM client_identity_map m JOIN clients c ON c.id = m.canonical_client_id
  WHERE c.company_id = ? GROUP BY m.canonical_client_id) x;
-- Клиенты с тикетами, но без clientId (мессенджеры, анонимные посетители)
SELECT COUNT(DISTINCT t.client_id) FROM tickets t
LEFT JOIN client_identity_map m ON m.canonical_client_id = t.client_id
WHERE t.company_id = ? AND m.id IS NULL;
-- Группы: живые, удалённые, демо, своё рабочее время, ограничение персональных данных
SELECT deleted > 0, is_demo, custom_working_time, client_personal_data_access, COUNT(*)
FROM user_groups WHERE company_id = ? GROUP BY 1, 2, 3, 4;
-- Тикеты, которые ссылаются на удалённые группы
SELECT COUNT(*) FROM tickets t JOIN user_groups g ON g.id = t.`group`
WHERE t.company_id = ? AND g.deleted > 0;
-- Повторяющиеся имена живых групп (у нас имя уникально)
SELECT name, COUNT(*) FROM user_groups WHERE company_id = ? AND deleted = 0 GROUP BY name HAVING COUNT(*) > 1;
-- Таблица «сотрудник — группы», если сотрудник бывает в нескольких группах
SHOW TABLES LIKE '%group%';
-- Остальные таблицы клиентов (почта, телефоны) — прислать их DDL
SHOW TABLES LIKE 'client%';

-- Операторы
SELECT u.role, u.deleted > 0, u.status, g.duplicate_guid IS NOT NULL, COUNT(*)
FROM users u LEFT JOIN usedesk.users_guid_migration g ON g.original_id = u.id
WHERE u.company_id = ? GROUP BY 1, 2, 3, 4;
-- Операторы, у которых несколько GUID (должно быть пусто)
SELECT original_id, COUNT(*) FROM usedesk.users_guid_migration GROUP BY original_id HAVING COUNT(*) > 1;
```
