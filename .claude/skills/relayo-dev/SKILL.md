---
name: relayo-dev
description: Запуск, перезапуск и проверка локального стенда Relayo — инфраструктура в Docker, сервисы Internal и Public, админка, виджет, тестовый сайт; какие проверки из tools/checks запускать после изменений, скриншоты админки и виджета, БД, логи, очистка тестовых данных. Использовать, когда нужно запустить Relayo, проверить изменение в работе, сделать скриншот или разобраться, почему что-то не работает локально.
---

# Стенд Relayo: запуск и проверка

Все команды — из корня репозитория. Порты и пароли — из `.env` (не выводить секреты в ответ пользователю).

## 1. Что уже запущено

```bash
docker ps --format '{{.Names}}\t{{.Status}}'                  # relayo-mssql-1, -rabbitmq-1, -redis-1, -s3-1, -seq-1, -test-site-1, -samba-1 (профиль ad)
curl -s -o /dev/null -w '%{http_code}\n' localhost:5100/health/ready   # Internal
curl -s -o /dev/null -w '%{http_code}\n' localhost:5200/health/ready   # Public
curl -s -o /dev/null -w '%{http_code}\n' localhost:5173/               # админка (Vite)
lsof -nP -iTCP:5100,5200,5173 -sTCP:LISTEN
```

Если сервисы уже запущены фоновыми задачами этой сессии — не запускать второй экземпляр, а перезапустить (п. 3).

## 2. Запуск с нуля

```bash
docker compose up -d                       # + --profile ad, если нужен тестовый AD
(cd web/widget && npm run build)           # Public раздаёт собранный core.js
dotnet build Relayo.slnx
```

Затем фоновыми задачами (run_in_background), логи — в scratchpad:

```bash
dotnet run --project src/Relayo.Internal --no-build > <scratchpad>/logs/internal.log 2>&1
dotnet run --project src/Relayo.Public   --no-build > <scratchpad>/logs/public.log 2>&1
cd web/admin && npm run dev                # только если 5173 не слушается
cd src/Relayo.WhatsApp && npm run dev      # шлюз WhatsApp :5400 (только если нужен WhatsApp по QR)
```

Дождаться готовности перед проверками (Monitor с until-циклом, не sleep):
`until curl -sf localhost:5100/health/ready && curl -sf localhost:5200/health/ready; do sleep 1; done`.
Internal применяет миграции и DevSeed при старте; Public переподключается к gRPC за 1–5 с.

## 3. После изменений — что пересобрать и перезапустить

| Что менял | Действие |
|---|---|
| `src/Relayo.Core`, `src/Relayo.Internal` | `dotnet build Relayo.slnx`, остановить задачу Internal (TaskStop), запустить снова. Если менялся proto или Contracts — перезапустить и Public |
| `src/Relayo.Public`, `src/Relayo.Contracts` | build, перезапуск Public (и Internal, если менялся proto) |
| Миграция | см. скилл `relayo-feature`; применится при перезапуске Internal |
| `web/admin` | Vite подхватит сам. Проверка типов: `cd web/admin && npx tsc -b --noEmit`; полная сборка `npm run build` |
| `web/widget` | `cd web/widget && npm run build` (Public отдаёт файл с диска, перезапуск не нужен). Опубликованные в S3 виджеты обновятся только после «Publish» в админке |
| `tools/TestSite` | `docker compose up -d --build test-site` |
| `deploy/` | стенд `deploy/docker-compose.yml`, проверка `tools/checks/deploy.cjs` и `stand-s3.cjs` (порты 6080/6200/6300) |

Забыть перезапустить сервис после build — самая частая причина «изменение не работает».

## 4. Проверки

Модульные тесты: `dotnet test --project tests/Relayo.Tests`.

Проверки работающего стенда — `tools/checks` (один раз `cd tools/checks && npm install`). Каждая печатает `PASS`/`FAIL` и `ALL PASSED`.
Выбирать по затронутой области, не гонять всё подряд: вход в админку ограничен 10 попытками в минуту.

| Область | Скрипт |
|---|---|
| Виджет → тикет → ответ, identify, зеркало, история | `python3 -I tools/checks/e2e.py` |
| Настройки сайтов и виджетов | `python3 -I tools/checks/settings.py` |
| Realtime (SignalR) оператора и посетителя | `node tools/checks/realtime.cjs` |
| Правила: API | `node tools/checks/rules.cjs` |
| Правила: движок, таймеры, лимиты, журнал | `node tools/checks/engine.cjs` |
| Telegram (обновления подделываются через вебхук Public) | `node tools/checks/telegram.cjs` |
| Канал WhatsApp «только ссылка» и переход из виджета на wa.me | `node tools/checks/whatsapp.cjs` |
| WhatsApp по QR без телефона (поддельный шлюз на 5400; настоящий шлюз остановить) | `node tools/checks/whatsapp-gateway.cjs` |
| LDAP/AD (нужен профиль `ad` и засеянный Samba) | `node tools/checks/ldap.cjs` |
| Агенты, группы, статусы, поля из AD | `node tools/checks/agents.cjs` |
| Публикация виджета в S3, версии, откат | `node tools/checks/publish.cjs` |
| Вложения (S3), настройки канала, картинка кнопки | `node tools/checks/files.cjs` |
| Несколько экземпляров Internal + Redis (нужен второй на 5110/5111, запускать последней) | `node tools/checks/scale.cjs` |
| Новые обращения: счётчик во вкладке, подсветка | `node tools/checks/unread.cjs` |
| Заметки, страница сайта у чата, группа тикета | `node tools/checks/notes.cjs` |
| Боевой стенд `deploy/` | `node tools/checks/deploy.cjs`, `node tools/checks/stand-s3.cjs` |

Если проверки не хватает для новой функции — дописать её в `tools/checks` в том же стиле (`check(name, ok, extra)`, уникальный `run` в именах данных, уборка в конце).

## 5. Скриншоты

```bash
node tools/checks/shoot.cjs '/agents|agents' '/channels|channels' '/|inbox|collapse'   # путь|имя[|collapse — свернуть меню]
node tools/checks/widget.cjs                                                          # виджет на тестовом сайте: закрыт, открыт, свёрнут
```

Файлы — в `tools/checks/out/*.png`; открыть через Read и посмотреть самому. В выводе `CONSOLE:` — ошибки страницы;
401 на первом запросе до входа и 404 favicon — норма.

## 6. БД, шина, логи

```bash
node tools/checks/db.cjs "SELECT name FROM sys.tables ORDER BY name"        # MS SQL; подключение — из .env
```

- Таблицы в snake_case: `tickets`, `messages` (секционирована по месяцам), `attachments`, `clients`, `users`, `sites`, `widgets`, `rules`, `rule_executions`, `telegram_bots` …
- Логи: файлы фоновых задач в scratchpad; структурированные логи и трассы — Seq http://localhost:8081.
- RabbitMQ: http://localhost:15672, S3 (RustFS): http://localhost:9001, файлы виджетов — `http://localhost:9000/relayo-widgets/widget_<publicKey>.js`.

## 7. Уборка

Проверки создают тестовые тикеты, клиентов, правила и группы. После прогона удалить то, что они оставили
(данные с суффиксом `run`), не трогая сайт `test`, его виджеты и пользователей `admin`, `ivanov`, `petrova`, `sidorov`.
Перед удалением из БД — посмотреть, что именно удаляется.
