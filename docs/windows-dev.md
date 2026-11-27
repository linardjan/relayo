# Среда разработки на Windows

Инструкция для машины, где инфраструктура установлена прямо в Windows, без Docker:
MS SQL Server, MySQL, Redis, RabbitMQ, SeaweedFS (S3), Seq. Общий порядок запуска тот же, что в [README](../README.md);
здесь — только то, что на Windows делается иначе.

Команды — для PowerShell, из корня репозитория, если не сказано иное.

## 1. Что установить

| Что | Зачем | Проверка |
|---|---|---|
| Git for Windows | репозиторий; заодно даёт `openssl` в Git Bash | `git --version` |
| .NET SDK 10 (10.0.202 или новее, см. `global.json`) | Internal, Public, тестовый сайт, тесты | `dotnet --version` |
| Node.js 22.9+ (LTS) | админка, виджет, шлюз WhatsApp (нужен `--env-file-if-exists`), проверки | `node -v` |
| Google Chrome | скриншоты и проверки из `tools/checks` (Playwright запускает установленный Chrome) | — |
| Python 3 | только для `tools/checks/e2e.py` и `settings.py` | `py --version` |

Docker не нужен. Он понадобится только для тестового AD (Samba), см. п. 9.

Разовые настройки:

```powershell
git config --global core.longpaths true                       # длинные пути в node_modules
Set-ExecutionPolicy -Scope CurrentUser RemoteSigned           # иначе PowerShell не даёт запускать npm.ps1
```

Репозиторий лучше положить по короткому пути, например `C:\src\Relayo`. Сборку заметно ускоряет исключение
папки репозитория и `%USERPROFILE%\.nuget` из проверки Defender (по желанию).

## 2. MS SQL Server

Relayo подключается по TCP. Войти можно двумя способами (`MSSQL_AUTH` в `.env`):

- **`windows`** — учётка Windows, под которой запущен внутренний сервис (Integrated Security). Пароль в `.env` не нужен;
- **`sql`** (по умолчанию) — логин и пароль SQL Server, нужна смешанная аутентификация.

Версия SQL Server — 2019 или новее.

1. **SQL Server Configuration Manager → SQL Server Network Configuration → Protocols for <экземпляр> → TCP/IP → Enabled.**
   На вкладке IP Addresses, в блоке IPAll, задайте `TCP Port = 1433` (у именованного экземпляра, например `SQLEXPRESS`,
   порт по умолчанию динамический — его нужно зафиксировать). Перезапустите службу SQL Server.
   Имя экземпляра в настройках Relayo не указывается: строка подключения собирается как `MSSQL_HOST,MSSQL_PORT`.
2. **Учётка для Relayo** — по одному из вариантов ниже. Базу `relayo` внутренний сервис создаёт сам при первом запуске,
   если у учётки есть право создавать базы (роль `dbcreator`). Если это право давать не хочется, создайте базу заранее
   и сделайте учётку её владельцем (см. ниже). Не используйте для Relayo уже существующую базу с чужими таблицами:
   при первом запуске применяется миграция `Initial`.

### Вариант А: вход учёткой Windows

Если SQL Server ставили под своей учёткой, она обычно уже входит в `sysadmin` — тогда в SQL Server ничего делать
не нужно. Иначе (или для отдельной учётки, под которой Internal работает службой) добавьте вход:

```sql
CREATE LOGIN [MYPC\ivan] FROM WINDOWS;           -- доменная учётка: [CORP\ivan]
ALTER SERVER ROLE dbcreator ADD MEMBER [MYPC\ivan];
```

Без `dbcreator` — база заранее:

```sql
CREATE DATABASE relayo;
GO
USE relayo;
CREATE USER [MYPC\ivan] FOR LOGIN [MYPC\ivan];
ALTER ROLE db_owner ADD MEMBER [MYPC\ivan];
```

В `.env`:

```ini
MSSQL_AUTH=windows
MSSQL_HOST=localhost
MSSQL_PORT=1433
MSSQL_DB=relayo
# MSSQL_USER и MSSQL_PASSWORD не используются — можно оставить пустыми
# У локального сервера самоподписанный сертификат
MSSQL_TRUST_SERVER_CERTIFICATE=true
```

Сервер на другой машине домена: `MSSQL_HOST` — полное DNS-имя сервера (например, `sql01.corp.local`), а не IP,
иначе Kerberos не найдёт SPN службы SQL Server и вход не пройдёт.

Проверке `tools/checks/db.cjs` для этого режима нужен драйвер `msnodesqlv8` и
[ODBC Driver 18 for SQL Server](https://learn.microsoft.com/sql/connect/odbc/download-odbc-driver-for-sql-server)
(обычно ставится вместе с SSMS). Драйвер не входит в зависимости проверок, поставьте его отдельно:

```powershell
cd tools\checks; npm install --no-save msnodesqlv8; cd ..\..
```

`npm ci` удаляет его — после `npm ci` поставьте снова.

### Вариант Б: логин и пароль SQL Server

1. **Смешанная аутентификация:** SSMS → свойства сервера → Security → «SQL Server and Windows Authentication mode»,
   затем перезапустите службу.
2. Учётка:

   ```sql
   CREATE LOGIN relayo WITH PASSWORD = 'Relayo_dev_Passw0rd';
   ALTER SERVER ROLE dbcreator ADD MEMBER relayo;
   ```

   Без `dbcreator` — база заранее:

   ```sql
   CREATE DATABASE relayo;
   GO
   USE relayo;
   CREATE USER relayo FOR LOGIN relayo;
   ALTER ROLE db_owner ADD MEMBER relayo;
   ```

В `.env`:

```ini
MSSQL_AUTH=sql
MSSQL_HOST=localhost
MSSQL_PORT=1433
MSSQL_DB=relayo
MSSQL_USER=relayo
MSSQL_PASSWORD=Relayo_dev_Passw0rd
# У локального сервера самоподписанный сертификат
MSSQL_TRUST_SERVER_CERTIFICATE=true
```

## 3. Redis

Подойдёт любой Redis для Windows (Memurai, порт tporadowski) или Redis в WSL2: WSL пробрасывает `localhost`.
Нужны только `GET/SET/INCR/PEXPIRE`, pub/sub и Lua-скрипты, так что подойдёт любая версия от 3.2.

```ini
REDIS_HOST=localhost
REDIS_PORT=6379
# Пусто, если в Redis не задан requirepass
REDIS_PASSWORD=
REDIS_SSL=false
```

Если `REDIS_HOST` оставить пустым, сервисы будут работать без Redis, как один экземпляр.

## 4. RabbitMQ

В Docker vhost и пользователей создаёт `docker-compose.yml`; здесь это нужно сделать вручную. Откройте
**RabbitMQ Command Prompt (sbin dir)** из меню «Пуск» (или папку `sbin` установки) и выполните:

```bat
rabbitmq-plugins enable rabbitmq_management
rabbitmqctl add_vhost relayo
rabbitmqctl add_user relayo relayo_dev_password
rabbitmqctl set_permissions -p relayo relayo ".*" ".*" ".*"
rabbitmqctl set_user_tags relayo administrator
rabbitmqctl add_user relayo_public relayo_public_dev_password
rabbitmqctl set_permissions -p relayo relayo_public ".*" ".*" ".*"
```

`set_user_tags` нужен только для входа в веб-интерфейс http://localhost:15672. Очереди и обменники сервисы
создают сами при запуске.

В `.env` — как в `.env.example`: `RABBITMQ_HOST=localhost`, `RABBITMQ_PORT=5672`, `RABBITMQ_VHOST=relayo` и пароли выше.

## 5. SeaweedFS (S3)

В Docker вместо него работает RustFS; Relayo работает с хранилищем по S3 API, поэтому SeaweedFS подходит без изменений
в коде. Нужно две вещи: ключ доступа для Relayo и анонимное чтение бакета виджетов (браузер загружает
`widget_<ключ>.js` прямо из S3).

Создайте `C:\seaweedfs\s3.json`:

```json
{
  "identities": [
    {
      "name": "relayo",
      "credentials": [{ "accessKey": "relayo", "secretKey": "relayo_dev_password" }],
      "actions": ["Admin", "Read", "Write", "List", "Tagging"]
    },
    {
      "name": "anonymous",
      "actions": ["Read:relayo-widgets"]
    }
  ]
}
```

Запуск (если SeaweedFS уже запущен со своей конфигурацией — добавьте эти две записи в его `s3.json`):

```powershell
weed server -dir=C:\seaweedfs\data -s3 -s3.port=8333 -s3.config=C:\seaweedfs\s3.json
```

Бакеты `relayo-widgets`, `relayo-attachments` и `relayo-exports` внутренний сервис создаёт сам. Он также пытается
задать бакету виджетов политику публичного чтения; если SeaweedFS её не поддерживает, в логе будет предупреждение
`Could not set public-read policy on bucket relayo-widgets` — это не страшно, чтение уже открыто через `anonymous`.

S3 API у SeaweedFS слушает порт **8333**, а не 9000, как в `.env.example`. Поменяйте в `.env` все три адреса:

```ini
S3_ENDPOINT=http://localhost:8333
S3_PUBLIC_URL=http://localhost:8333
S3_FORCE_PATH_STYLE=true
S3_AUTH_MODE=keys
S3_ACCESS_KEY=relayo
S3_SECRET_KEY=relayo_dev_password
WIDGET_FILES_BASE_URL=http://localhost:8333/relayo-widgets
```

Консоли, как у RustFS (`:9001`), здесь нет; файлы можно посмотреть в filer SeaweedFS: http://localhost:8888/buckets/.

## 6. Seq

Seq для Windows по умолчанию принимает логи и показывает интерфейс на одном порту: http://localhost:5341.
Relayo отправляет трассы по OpenTelemetry (`/ingest/otlp`), для этого нужен Seq 2024.1 или новее.

```ini
SEQ_URL=http://localhost:5341
OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:5341/ingest/otlp
OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf
# Если в Seq включена аутентификация — создайте API key (Settings → API Keys) и укажите его
SEQ_API_KEY=
```

`SEQ_UI_PORT` и `SEQ_FIRSTRUN_ADMIN_PASSWORD` нужны только контейнеру, на Windows их можно не трогать.

## 7. MySQL (только для импорта из Usedesk)

MySQL нужен только для проверки импорта из Usedesk. Загрузите тестовую базу с фейковыми данными:

```powershell
mysql -u root -p -e "CREATE DATABASE usedesk CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci"
mysql -u root -p usedesk -e "source tools/usedesk-mock/01-schema.sql"
mysql -u root -p usedesk -e "source tools/usedesk-mock/02-seed.sql"
```

Подключение задаётся в админке, а не в `.env`: Settings → Import from Usedesk, сервер `localhost`, порт `3306`
(а не 3307, как у контейнера), БД `usedesk`, ваш пользователь MySQL, компания `7`.
Проверка `tools/checks/usedesk-import.cjs` подключается как `root` с портом и паролем из `.env`, поэтому для неё задайте
`TEST_USEDESK_MYSQL_PORT=3306` и `TEST_USEDESK_MYSQL_PASSWORD=<пароль root локального MySQL>`.

## 8. Файл `.env`

```powershell
Copy-Item .env.example .env
```

Внесите изменения из пп. 2–6 и заполните пустые значения:

| Переменная | Значение |
|---|---|
| `INTERNAL_GRPC_KEY`, `JWT_SIGNING_KEY`, `SECRETS_ENCRYPTION_KEY`, `TEST_SITE_SECRET`, `WHATSAPP_GATEWAY_KEY` | случайная строка, у каждой своя (команда ниже) |
| `BOOTSTRAP_ADMIN_PASSWORD` | пароль пользователя `admin` для первого входа в админку |

Случайная строка — в PowerShell 7:

```powershell
[Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
```

в Windows PowerShell 5.1:

```powershell
$b = New-Object byte[] 32; [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($b); [Convert]::ToBase64String($b)
```

или в Git Bash: `openssl rand -base64 32`.

`.env` не коммитится. `SECRETS_ENCRYPTION_KEY` не меняйте после первого запуска: им зашифрованы токены ботов
и ключи сайтов в БД.

## 9. Первый запуск

Каждый сервис — в своём окне терминала (удобно во вкладках Windows Terminal или в терминалах VS Code).

```powershell
# Один раз: инструменты и зависимости
dotnet tool restore
cd web\widget; npm ci; npm run build; cd ..\..      # Public раздаёт собранный core.js
cd web\admin;  npm ci; cd ..\..
dotnet build Relayo.slnx
```

```powershell
# Терминал 1 — внутренний сервис (создаёт базу, применяет миграции, тестовый сайт и виджеты)
dotnet run --project src\Relayo.Internal
```

```powershell
# Терминал 2 — публичный сервис (подключается к Internal по gRPC через 1–5 с после его старта)
dotnet run --project src\Relayo.Public
```

```powershell
# Терминал 3 — админка
cd web\admin; npm run dev
```

```powershell
# Терминал 4 — тестовый сайт (на Mac он работает в Docker), если он не установлен в IIS (см. ниже).
# Он не читает .env сам,
# поэтому сначала переносим переменные из .env в окружение этого терминала
Get-Content .env | Where-Object { $_ -match '^\s*([A-Za-z_][A-Za-z0-9_]*)\s*=(.*)$' } |
  ForEach-Object { Set-Item "env:$($Matches[1])" $Matches[2].Trim() }
dotnet run --project tools\TestSite
```

```powershell
# Терминал 5 — шлюз WhatsApp, только если нужна переписка в WhatsApp по QR
cd src\Relayo.WhatsApp; npm ci; npm run dev
```

При первом запуске Windows может спросить про доступ к сети для `dotnet.exe` или `node.exe`: для локальной
разработки хватит частных сетей.

### Тестовый сайт в IIS

Чтобы тестовый сайт работал постоянно, без открытого терминала, поставьте его в IIS. Один раз включите IIS
и установите модуль ASP.NET Core:

```powershell
# PowerShell от администратора
Enable-WindowsOptionalFeature -Online -All -FeatureName IIS-WebServerRole,IIS-WebServer,IIS-ManagementConsole,IIS-ApplicationInit
```

Затем — **ASP.NET Core Runtime 10.0 — Windows Hosting Bundle** с https://dotnet.microsoft.com/download/dotnet/10.0.
Если IIS включили уже после установки Hosting Bundle, запустите его установщик ещё раз и выберите Repair.

Установка и обновление сайта — в **Windows PowerShell** (не PowerShell 7) от администратора, из корня репозитория:

```powershell
powershell -ExecutionPolicy Bypass -File tools\TestSite\install-iis.ps1
```

Скрипт собирает сайт в `C:\inetpub\relayo-testsite`, переносит его настройки из `.env` в `appsettings.json`
рядом со сборкой и создаёт в IIS пул и сайт `relayo-testsite`. У сайта две привязки: `TEST_SITE_PORT` (5300) и
`TEST_SITE_MIRROR_PORT` (5301). Пул запускается вместе с IIS и не останавливается при простое.

- **После изменений** в `tools\TestSite` или в настройках тестового сайта в `.env` запустите скрипт снова: сам сайт
  `.env` не читает.
- Порты 5300 и 5301 должны быть свободны: перед установкой остановите `dotnet run` тестового сайта (терминал 4).
- Удаление: `install-iis.ps1 -Uninstall`. Пул и сайт удаляются, папка `C:\inetpub\relayo-testsite` остаётся.
- Параметры `-SiteName`, `-Path`, `-EnvFile` — если нужны другое имя, папка или файл настроек.

Ссылку на виджет можно поменять прямо на странице тестового сайта, в блоке **Widget script**: вставьте адрес
`…/widget_<ключ>.js` или код вставки целиком из Settings → Widgets и нажмите Apply. Ссылка хранится в браузере,
отдельно для каждого режима (signed token и direct) и для основного сайта и зеркала. Reset возвращает тестовый
виджет из `.env`.

Тестовый AD (Samba) работает только в Docker с Linux-контейнерами. Без него вход через AD можно проверить на тестовой
OU корпоративного AD (Settings → LDAP) или не проверять вовсе.

## 10. Проверка, что всё работает

```powershell
curl.exe -s http://localhost:5100/health/ready        # Internal: Healthy
curl.exe -s http://localhost:5200/health/ready        # Public: Healthy
curl.exe -s -o NUL -w "%{http_code}`n" http://localhost:8333/relayo-widgets/widget_testtoken1.js   # 200: виджет опубликован и читается без ключа
```

В Windows PowerShell 5.1 `curl` без `.exe` — это другая команда (`Invoke-WebRequest`), поэтому пишите `curl.exe`.

Дальше — как в README:

| Что | Адрес |
|---|---|
| Админка | http://localhost:5173, вход `admin` / `BOOTSTRAP_ADMIN_PASSWORD` |
| Тестовый сайт и зеркало | http://localhost:5300, http://localhost:5301 |
| Логи и трассы | http://localhost:5341 (Seq) |
| RabbitMQ | http://localhost:15672 (`relayo` / пароль из п. 4) |
| Файлы S3 | http://localhost:8888/buckets/ (filer SeaweedFS) |

Напишите в виджет на тестовом сайте: в админке появится тикет. Перед ответом задайте псевдоним
(меню пользователя → «Alias for clients»).

## 11. Тесты и проверки стенда

```powershell
dotnet test --project tests\Relayo.Tests

cd tools\checks; npm ci; cd ..\..
node tools\checks\db.cjs "SELECT TOP 5 number, status FROM tickets ORDER BY number DESC"
node tools\checks\requests.cjs
py -I tools\checks\e2e.py
```

Список проверок — в [tools/checks/README.md](../tools/checks/README.md). Учтите:

- вход в админку ограничен 10 попытками в минуту — не запускайте много проверок подряд;
- в `publish.cjs`, `whatsapp.cjs` и `settings.py` адрес файлов виджетов жёстко задан как `http://localhost:9000/relayo-widgets`:
  с SeaweedFS на 8333 они упадут на этих шагах (остальное в них проверяется как обычно);
- `ldap.cjs` нужен Samba в Docker, `deploy.cjs` и `stand-s3.cjs` проверяют боевой стенд `deploy/` в Docker;
- скилл `relayo-dev` для Claude Code написан под Mac с Docker: его команды `docker compose` здесь не нужны.

## 12. Если что-то не работает

| Признак | Что проверить |
|---|---|
| Internal: `A network-related or instance-specific error` | В SQL Server не включён TCP/IP или порт не 1433 (п. 2); служба SQL Server Browser тут не поможет: имя экземпляра не используется |
| Internal: `Login failed for user 'relayo'` | Не включена смешанная аутентификация или неверный пароль |
| Internal: `Login failed for user 'MYPC\ivan'` | Нет входа `FROM WINDOWS` для этой учётки или прав на базу (п. 2, вариант А). Учётка — та, под которой запущен `dotnet run` или служба |
| Internal: `Cannot generate SSPI context` | Kerberos: в `MSSQL_HOST` IP или короткое имя вместо полного DNS-имени, либо у службы SQL Server не зарегистрирован SPN — обратитесь к администратору домена |
| Тестовый сайт в IIS: `HTTP Error 500.19`, код `0x8007000d` | Не установлен Hosting Bundle или IIS включили после него: Repair установщика, затем `net stop was /y; net start w3svc` |
| Тестовый сайт в IIS: `HTTP Error 500.30` | Сайт не запустился. Причина — в «Просмотре событий» → Журналы Windows → Приложение, источник `IIS AspNetCore Module V2`. Чаще всего пустой `TEST_SITE_SECRET` в `.env`: исправьте и запустите `install-iis.ps1` снова |
| `install-iis.ps1`: `Port 5300 is already used by dotnet` | Тестовый сайт уже запущен через `dotnet run`: остановите его |
| `db.cjs`: `MSSQL_AUTH=windows needs msnodesqlv8` | Поставьте драйвер (п. 2, вариант А) |
| Internal: `CREATE DATABASE permission denied` | Нет роли `dbcreator`: создайте базу заранее с `db_owner` (п. 2) |
| `The certificate chain was issued by an authority that is not trusted` | `MSSQL_TRUST_SERVER_CERTIFICATE=true` |
| `ACCESS_REFUSED` или `vhost relayo not found` от RabbitMQ | Не создан vhost или не выданы права пользователю (п. 4) |
| Виджет не появляется на тестовом сайте, в консоли браузера 403/404 на `widget_*.js` | Нет identity `anonymous` с `Read:relayo-widgets` или `WIDGET_FILES_BASE_URL` указывает на 9000 (п. 5) |
| Publish виджета или вложения: «File storage (S3) is unavailable» | `S3_ENDPOINT`, ключи в `s3.json` и `.env` совпадают? SeaweedFS запущен с `-s3`? |
| Public: `/health/ready` не Healthy | Internal ещё стартует или не совпадает `INTERNAL_GRPC_KEY` (у обоих сервисов он из одного `.env`) |
| Тестовый сайт: `TEST_SITE_SECRET must be set` | Переменные из `.env` не загружены в этот терминал (п. 9, терминал 4) |
| Порт занят | `netstat -ano \| findstr :5100`, затем `Stop-Process -Id <PID>` |
| Изменение в коде «не работает» | После `dotnet build` перезапустите сервис (Ctrl+C и снова `dotnet run`) |
