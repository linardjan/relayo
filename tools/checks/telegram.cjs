// E2E-проверка перехода из виджета в Telegram. Обновления Telegram подделываются через webhook публичного сервиса.
const fs = require('fs')
const crypto = require('crypto')
const { execFileSync } = require('child_process')

const ROOT = require('path').join(__dirname, '../..') + ''
const env = Object.fromEntries(
  fs.readFileSync(`${ROOT}/.env`, 'utf8').split('\n')
    .filter((l) => /^[A-Z_]+=/.test(l))
    .map((l) => { const i = l.indexOf('='); return [l.slice(0, i), l.slice(i + 1).replace(/^"|"$/g, '')] }),
)
const PUB = 'http://localhost:5200'
const ADMIN = 'http://localhost:5100'
const ORIGIN = 'http://localhost:5300'
const WIDGET = env.TEST_WIDGET_TOKEN_MODE_ID
const run = Date.now() % 1_000_000
const USER1 = 990_000_000 + run
const USER2 = 980_000_000 + run
const USER3 = 970_000_000 + run

let failed = 0
const check = (name, ok, extra = '') => { console.log(`${ok ? 'PASS' : 'FAIL'} ${name}${extra ? ' — ' + extra : ''}`); if (!ok) failed++ }

const sql = (q) => execFileSync('docker', ['exec', 'relayo-postgres-1', 'psql', '-U', env.POSTGRES_USER, '-d', env.POSTGRES_DB, '-At', '-F', '|', '-c', q], { encoding: 'utf8' }).trim()

function unprotect(value) {
  const key = Buffer.from(env.SECRETS_ENCRYPTION_KEY, 'base64').subarray(0, 32)
  const data = Buffer.from(value, 'base64')
  const d = crypto.createDecipheriv('aes-256-gcm', key, data.subarray(0, 12))
  d.setAuthTag(data.subarray(12, 28))
  return Buffer.concat([d.update(data.subarray(28)), d.final()]).toString('utf8')
}

async function widget(method, path, body, visitor) {
  const headers = { origin: ORIGIN }
  if (body) headers['content-type'] = 'application/json'
  if (visitor) headers['x-relayo-visitor'] = visitor
  const r = await fetch(`${PUB}/api/widget/${WIDGET}${path}`, { method, headers, body: body ? JSON.stringify(body) : undefined })
  return { status: r.status, body: await r.json().catch(() => null) }
}

let updateId = run * 100
async function tg(botId, secret, message) {
  const r = await fetch(`${PUB}/telegram/${botId}/webhook`, {
    method: 'POST',
    headers: { 'content-type': 'application/json', ...(secret ? { 'x-telegram-bot-api-secret-token': secret } : {}) },
    body: JSON.stringify({ update_id: ++updateId, message }),
  })
  return r.status
}
const msg = (user, id, extra) => ({
  message_id: id, date: Math.floor(Date.now() / 1000),
  from: { id: user, is_bot: false, first_name: 'Ivan', last_name: 'Testov', username: `ivan${user}`, language_code: 'ru' },
  chat: { id: user, type: 'private' }, ...extra,
})
const sleep = (ms) => new Promise((r) => setTimeout(r, ms))

;(async () => {
  const [botId, protectedSecret, username, protectedToken] = sql(`select id, protected_webhook_secret, username, protected_token from telegram_bots where is_enabled limit 1`).split('|')
  const botToken = unprotect(protectedToken)
  const secret = unprotect(protectedSecret)

  // 1. Конфиг виджета содержит бота
  const config = await widget('GET', '/config')
  check('widget config has telegram bot', config.body?.telegramBot === username, config.body?.telegramBot)

  // 2. Посетитель пишет в виджет
  const session = await widget('POST', '/session')
  const visitor = session.body.visitorToken
  const sent = await widget('POST', '/messages', { text: `TG test ${run}`, clientMessageId: `m-${run}` }, visitor)
  check('visitor message sent', sent.status === 200)
  const widgetChat = sent.body.chatId

  // 3. Ссылка
  const link = await widget('POST', '/telegram-link', undefined, visitor)
  const url = link.body?.url ?? ''
  const startToken = url.split('start=')[1]
  check('telegram link issued', link.status === 200 && url.startsWith(`https://t.me/${username}?start=`) && /^[A-Za-z0-9_-]{32}$/.test(startToken ?? ''), url.replace(/start=.*/, 'start=…'))
  const ttl = (Date.parse(link.body.expiresAt) - Date.now()) / 60000
  check('link lives ~10 minutes', ttl > 9 && ttl <= 10.1, ttl.toFixed(2))
  const noVisitor = await widget('POST', '/telegram-link')
  check('link requires visitor token', noVisitor.status === 401)
  const otherOrigin = await fetch(`${PUB}/api/widget/${WIDGET}/telegram-link`, { method: 'POST', headers: { origin: 'https://evil.example', 'x-relayo-visitor': visitor } })
  check('link rejected from foreign origin', otherOrigin.status === 403)

  // 4. Webhook без секрета и с неверным секретом
  check('webhook without secret → 401', (await tg(botId, null, msg(USER1, 1, { text: 'x' }))) === 401)
  check('webhook with wrong secret → 401', (await tg(botId, 'wrong', msg(USER1, 1, { text: 'x' }))) === 401)
  check('webhook for unknown bot → 401', (await tg(crypto.randomUUID(), secret, msg(USER1, 1, { text: 'x' }))) === 401)

  // 5. /start <token>
  check('/start accepted', (await tg(botId, secret, msg(USER1, 10, { text: `/start ${startToken}` }))) === 200)
  const identity = sql(`select i.id, i.linked_from_identity_id, i.display_name, c.display_name, i.client_id = v.client_id
    from client_identities i join client_identities v on v.id = i.linked_from_identity_id join clients c on c.id = i.client_id
    where i.type = 'Telegram' and i.external_id = '${botId.replace(/-/g, '')}:${USER1}'`).split('|')
  check('telegram identity linked to visitor', identity.length === 5 && identity[4] === 't', identity.join(' | '))
  check('identity display name', identity[2] === `Ivan Testov (@ivan${USER1})`)
  const system = sql(`select count(*) from messages where chat_id = '${widgetChat}' and author = 'System' and channel = 'Telegram'`)
  check('system message in the widget chat', system === '1', system)

  // 6. Сообщение из Telegram идёт в тот же чат; повтор не создаёт дубль
  check('telegram text accepted', (await tg(botId, secret, msg(USER1, 11, { text: `Hi from Telegram ${run}` }))) === 200)
  check('duplicate update accepted', (await tg(botId, secret, msg(USER1, 11, { text: `Hi from Telegram ${run}` }))) === 200)
  const tgMessages = sql(`select chat_id, count(*) from messages where text = 'Hi from Telegram ${run}' group by chat_id`).split('|')
  check('telegram message continues the widget chat', tgMessages[0] === widgetChat, tgMessages.join(' | '))
  check('no duplicate', tgMessages[1] === '1')

  // 7. Виджет видит переписку из Telegram
  const history = await widget('GET', '/messages', undefined, visitor)
  check('widget history includes telegram message', history.body.some((m) => m.text === `Hi from Telegram ${run}`))
  check('widget history includes system message', history.body.some((m) => m.author === 'system' && m.text.includes('Telegram')))

  // 8. Использованная ссылка не привязывает другого человека
  check('/start reused by another user', (await tg(botId, secret, msg(USER2, 20, { text: `/start ${startToken}` }))) === 200)
  const stolen = sql(`select count(*) from client_identities where external_id = '${botId.replace(/-/g, '')}:${USER2}'`)
  check('used link does not link another account', stolen === '0', stolen)
  // ...а повторный /start того же человека — не ошибка
  check('/start repeated by same user', (await tg(botId, secret, msg(USER1, 12, { text: `/start ${startToken}` }))) === 200)

  // 9. Оператор отвечает: ответ уходит в Telegram
  const login = await fetch(`${ADMIN}/api/auth/login`, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ login: 'admin', password: env.BOOTSTRAP_ADMIN_PASSWORD }) })
  const { accessToken } = await login.json()
  const auth = { authorization: `Bearer ${accessToken}`, 'content-type': 'application/json' }
  const ticketId = sql(`select ticket_id from chats where id = '${widgetChat}'`)
  const ticket = await (await fetch(`${ADMIN}/api/tickets/${ticketId}`, { headers: auth })).json()
  const chat = ticket.chats.find((c) => c.id === widgetChat)
  check('admin sees reply channel Telegram', chat?.replyChannel === 'Telegram', chat?.replyChannel)
  check('admin sees telegram channel on message', chat?.messages.some((m) => m.channel === 'Telegram' && m.author === 'Client'))
  const logBefore = fs.readFileSync(process.argv[2], 'utf8').length
  const reply = await fetch(`${ADMIN}/api/tickets/${ticketId}/messages`, { method: 'POST', headers: auth, body: JSON.stringify({ chatId: widgetChat, text: `Reply <b>${run}</b> & co` }) })
  const replyBody = await reply.json()
  check('operator reply saved with Telegram channel', reply.status === 200 && replyBody.channel === 'Telegram', `${reply.status} ${replyBody.channel}`)
  // Фейкового чата в Telegram нет: Bot API ответит 400 chat not found, это видно в логе
  let sentLog = ''
  for (let i = 0; i < 20 && !sentLog; i++) {
    await sleep(500)
    sentLog = fs.readFileSync(process.argv[2], 'utf8').slice(logBefore).split('\n').find((l) => l.includes(`chat ${USER1}`)) ?? ''
  }
  check('reply went to Telegram Bot API (chat not found for fake user)', /chat not found/i.test(sentLog), sentLog.slice(0, 160))

  // 10. Человек пишет боту напрямую: новый анонимный клиент с именем из Telegram
  check('direct message accepted', (await tg(botId, secret, msg(USER3, 30, { text: `Direct ${run}` }))) === 200)
  const direct = sql(`select c.display_name, c.external_client_id is null, t.number from client_identities i
    join clients c on c.id = i.client_id join tickets t on t.client_id = c.id
    where i.external_id = '${botId.replace(/-/g, '')}:${USER3}'`).split('|')
  check('direct writer gets own anonymous client and ticket', direct[0] === `Ivan Testov (@ivan${USER3})` && direct[1] === 't', direct.join(' | '))
  const list = await (await fetch(`${ADMIN}/api/tickets`, { headers: auth })).json()
  check('ticket list shows telegram name as client label', list.some((t) => t.clientLabel === `Ivan Testov (@ivan${USER3})`))

  // 11. Фото вместо текста
  check('photo accepted', (await tg(botId, secret, msg(USER3, 31, { photo: [{ file_id: 'x', width: 1, height: 1 }], caption: 'look' }))) === 200)
  const photo = sql(`select text from messages where text like 'look%' and created_at > now() - interval '1 minute' limit 1`)
  check('photo becomes a placeholder message', photo.includes('photo') && photo.includes('not supported'), photo.replace(/\n/g, ' / '))

  // 12. Группы игнорируются
  check('group message accepted', (await tg(botId, secret, { ...msg(USER3, 32, { text: `Group ${run}` }), chat: { id: -100123, type: 'group' } })) === 200)
  check('group message ignored', sql(`select count(*) from messages where text = 'Group ${run}'`) === '0')

  // 13. Настройки
  const bots = await (await fetch(`${ADMIN}/api/settings/telegram-bots`, { headers: auth })).json()
  check('settings list bots without token', bots.length >= 1 && !JSON.stringify(bots).includes('AAH') && bots[0].widgetCount >= 2, JSON.stringify(bots[0]))
  const bad = await fetch(`${ADMIN}/api/settings/telegram-bots`, { method: 'POST', headers: auth, body: JSON.stringify({ name: 'x', siteId: bots[0].siteId, isEnabled: true, token: 'not-a-token' }) })
  check('invalid token rejected', bad.status === 400 && (await bad.json()).code === 'invalid_bot_token')
  const fake = await fetch(`${ADMIN}/api/settings/telegram-bots`, { method: 'POST', headers: auth, body: JSON.stringify({ name: 'x', siteId: bots[0].siteId, isEnabled: true, token: '123456:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA' }) })
  check('token rejected by Telegram', fake.status === 400 && (await fake.json()).code === 'invalid_bot_token', String(fake.status))

  // 14. Виджет без бота
  const widgets = await (await fetch(`${ADMIN}/api/settings/widgets`, { headers: auth })).json()
  const direct2 = widgets.find((w) => w.widget.id === env.TEST_WIDGET_DIRECT_MODE_ID).widget
  const { id: _i, siteId: _s, siteCode: _c, createdAt: _d, ...input } = direct2
  const off = await fetch(`${ADMIN}/api/settings/widgets/${direct2.id}`, { method: 'PUT', headers: auth, body: JSON.stringify({ ...input, telegramBotId: null }) })
  check('widget bot can be removed', off.status === 200 && (await off.json()).widget.telegramBotId === null)
  const on = await fetch(`${ADMIN}/api/settings/widgets/${direct2.id}`, { method: 'PUT', headers: auth, body: JSON.stringify({ ...input, telegramBotId: botId }) })
  check('widget bot can be set back', on.status === 200 && (await on.json()).widget.telegramBotId === botId)

  // Токен бота нигде в логах
  const logs = fs.readFileSync(process.argv[2], 'utf8') + fs.readFileSync(process.argv[3], 'utf8')
  check('bot token not in logs', !logs.includes(botToken.split(':')[1]))

  console.log(failed ? `${failed} FAILED` : 'ALL PASSED')
  process.exit(failed ? 1 : 0)
})().catch((e) => { console.error(e); process.exit(1) })
