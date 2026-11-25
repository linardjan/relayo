const { createRequire } = require('module')
const signalR = createRequire(require('path').join(__dirname, '../..') + '/web/widget/')('@microsoft/signalr')
const fs = require('fs')

const env = Object.fromEntries(fs.readFileSync(require('path').join(__dirname, '../..') + '/.env', 'utf8').split('\n')
  .filter((l) => l && !l.startsWith('#') && l.includes('=')).map((l) => [l.slice(0, l.indexOf('=')), l.slice(l.indexOf('=') + 1).replace(/^"|"$/g, '')]))
const PUB = 'http://localhost:5200', ADM = 'http://localhost:5100', W = env.TEST_WIDGET_TOKEN_MODE_ID, ORIGIN = 'http://localhost:5300'
let ok = true
const check = (name, cond, extra) => { ok &&= !!cond; console.log((cond ? 'PASS ' : 'FAIL ') + name + (!cond && extra ? '  [' + JSON.stringify(extra) + ']' : '')) }
const wait = (pred, ms = 5000) => new Promise((res) => { const t0 = Date.now(); const i = setInterval(() => { if (pred() || Date.now() - t0 > ms) { clearInterval(i); res(pred()) } }, 50) })

async function json(url, opts = {}) { const r = await fetch(url, opts); return { status: r.status, body: r.status === 204 ? null : await r.json() } }

function widgetHub(token, origin = ORIGIN) {
  return new signalR.HubConnectionBuilder()
    .withUrl(`${PUB}/hubs/widget?widgetId=${W}`, { transport: signalR.HttpTransportType.WebSockets, skipNegotiation: true, accessTokenFactory: () => token, headers: { Origin: origin } })
    .configureLogging(signalR.LogLevel.None).build()
}

;(async () => {
  // Посетитель и две его вкладки
  const { body: session } = await json(`${PUB}/api/widget/${W}/session`, { method: 'POST', headers: { Origin: ORIGIN } })
  const token = session.visitorToken
  const tab1 = widgetHub(token), tab2 = widgetHub(token)
  const got1 = [], got2 = []
  tab1.on('message', (m) => got1.push(m)); tab2.on('message', (m) => got2.push(m))
  await tab1.start(); await tab2.start()
  check('widget hub connects from the site origin', tab1.state === 'Connected' && tab2.state === 'Connected')

  // Чужой домен
  const evil = widgetHub(token, 'https://evil.example')
  let evilClosed = false
  evil.onclose(() => { evilClosed = true })
  try { await evil.start() } catch { evilClosed = true }
  await wait(() => evilClosed, 3000)
  check('widget hub rejects a foreign origin', evilClosed)

  // Неизвестный токен
  const stranger = widgetHub('not-a-real-token')
  let strangerClosed = false
  stranger.onclose(() => { strangerClosed = true })
  try { await stranger.start() } catch { strangerClosed = true }
  await wait(() => strangerClosed, 3000)
  check('widget hub rejects an unknown visitor token', strangerClosed)

  // Оператор
  const { body: auth } = await json(`${ADM}/api/auth/login`, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ login: 'admin', password: env.BOOTSTRAP_ADMIN_PASSWORD }) })
  const op = new signalR.HubConnectionBuilder().withUrl(`${ADM}/hubs/operators`, { accessTokenFactory: () => auth.accessToken }).configureLogging(signalR.LogLevel.None).build()
  const opMessages = [], opTickets = []
  op.on('messageCreated', (e) => opMessages.push(e)); op.on('ticketChanged', (t) => opTickets.push(t))
  await op.start()
  check('operator hub connects with JWT', op.state === 'Connected')

  const noAuth = new signalR.HubConnectionBuilder().withUrl(`${ADM}/hubs/operators`).configureLogging(signalR.LogLevel.None).build()
  let noAuthFailed = false
  try { await noAuth.start() } catch { noAuthFailed = true }
  check('operator hub rejects connection without token', noAuthFailed)

  // Посетитель пишет
  const text = 'Realtime hello ' + Date.now()
  const { body: sent } = await json(`${PUB}/api/widget/${W}/messages`, { method: 'POST', headers: { Origin: ORIGIN, 'X-Relayo-Visitor': token, 'content-type': 'application/json' }, body: JSON.stringify({ text, clientMessageId: 'rt-' + Date.now() }) })
  await wait(() => opMessages.some((e) => e.message.text === text) && got2.some((m) => m.text === text))
  const opEvent = opMessages.find((e) => e.message.text === text)
  check('operator receives the visitor message instantly', !!opEvent, opMessages)
  check('operator receives ticketChanged with string status', opTickets.some((t) => t.id === opEvent?.ticketId && typeof t.status === 'string'), opTickets.slice(-1))
  check('second tab of the same visitor receives the message too', got2.some((m) => m.text === text && m.id === sent.id))

  // Оператор отвечает
  const ticketId = opEvent.ticketId, chatId = opEvent.message.chatId
  await json(`${ADM}/api/me/alias`, { method: 'PUT', headers: { authorization: 'Bearer ' + auth.accessToken, 'content-type': 'application/json' }, body: JSON.stringify({ alias: 'Anna' }) })
  const reply = 'Operator reply ' + Date.now()
  await json(`${ADM}/api/tickets/${ticketId}/messages`, { method: 'POST', headers: { authorization: 'Bearer ' + auth.accessToken, 'content-type': 'application/json' }, body: JSON.stringify({ chatId, text: reply }) })
  await wait(() => got1.some((m) => m.text === reply) && got2.some((m) => m.text === reply))
  const delivered = got1.find((m) => m.text === reply)
  check('visitor receives operator reply in realtime (both tabs)', delivered && got2.some((m) => m.text === reply))
  check('reply shows alias, not full name', delivered?.authorName === 'Anna' && !JSON.stringify(got1).includes('Administrator'), delivered)

  // Другой посетитель не получает чужие сообщения
  const { body: other } = await json(`${PUB}/api/widget/${W}/session`, { method: 'POST', headers: { Origin: ORIGIN } })
  const otherHub = widgetHub(other.visitorToken), gotOther = []
  otherHub.on('message', (m) => gotOther.push(m)); await otherHub.start()
  const reply2 = 'Private reply ' + Date.now()
  await json(`${ADM}/api/tickets/${ticketId}/messages`, { method: 'POST', headers: { authorization: 'Bearer ' + auth.accessToken, 'content-type': 'application/json' }, body: JSON.stringify({ chatId, text: reply2 }) })
  await wait(() => got1.some((m) => m.text === reply2))
  await new Promise((r) => setTimeout(r, 500))
  check('another visitor does not receive this conversation', gotOther.length === 0, gotOther)

  await Promise.all([tab1.stop(), tab2.stop(), op.stop(), otherHub.stop()])
  console.log(ok ? '\nALL PASSED' : '\nSOME CHECKS FAILED'); process.exit(ok ? 0 : 1)
})().catch((e) => { console.error(e); process.exit(1) })
