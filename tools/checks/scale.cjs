// Несколько экземпляров внутреннего сервиса с Redis.
// Нужен второй экземпляр на портах 5110/5111:
//   INTERNAL_HTTP_PORT=5110 INTERNAL_GRPC_PORT=5111 dotnet run --project src/Relayo.Internal --no-build
// Проверка в конце исчерпывает лимит входа на минуту: запускать последней.
//   node tools/checks/scale.cjs
const { createRequire } = require('module')
const path = require('path')
const { execFileSync } = require('child_process')
const signalR = createRequire(path.join(__dirname, '../..') + '/web/widget/')('@microsoft/signalr')
const { env } = require('./db.cjs')

const A = 'http://localhost:5100'
const B = 'http://localhost:5110'
const PUB = 'http://localhost:5200'
const ORIGIN = 'http://localhost:5300'
const W = env.TEST_WIDGET_TOKEN_MODE_ID
const run = Date.now() % 1_000_000

let failed = 0
const check = (name, ok, extra = '') => { console.log(`${ok ? 'PASS' : 'FAIL'} ${name}${extra ? ' — ' + extra : ''}`); if (!ok) failed++ }
const wait = (pred, ms = 8000) => new Promise((res) => { const t0 = Date.now(); const i = setInterval(() => { if (pred() || Date.now() - t0 > ms) { clearInterval(i); res(pred()) } }, 50) })
const redis = (...args) => execFileSync('docker', ['exec', '-e', `REDISCLI_AUTH=${env.REDIS_PASSWORD}`, 'relayo-redis-1', 'redis-cli', ...args], { encoding: 'utf8' }).trim()

async function login(base, password = env.BOOTSTRAP_ADMIN_PASSWORD) {
  const r = await fetch(`${base}/api/auth/login`, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ login: 'admin', password }) })
  return { status: r.status, body: await r.json().catch(() => null) }
}

function operatorHub(base, token) {
  return new signalR.HubConnectionBuilder()
    .withUrl(`${base}/hubs/operators`, { transport: signalR.HttpTransportType.WebSockets, skipNegotiation: true, accessTokenFactory: () => token })
    .configureLogging(signalR.LogLevel.None).build()
}

;(async () => {
  // 1. Оба экземпляра готовы и видят Redis
  for (const [name, base] of [['A', A], ['B', B]]) {
    const r = await fetch(`${base}/health/ready`)
    check(`instance ${name} is ready`, r.status === 200 && (await r.text()) === 'Healthy')
  }

  // 2. Токен, выданный одним экземпляром, принимает другой (общий ключ подписи)
  const auth = await login(A)
  const token = auth.body.accessToken
  const me = await fetch(`${B}/api/settings/widgets`, { headers: { authorization: `Bearer ${token}` } })
  check('token from A works on B', me.status === 200)

  // 3. Realtime: оператор подключён к B, сообщение посетителя обрабатывает A (публичный сервис ходит в A по gRPC)
  const hubB = operatorHub(B, token)
  const seen = []
  hubB.on('messageCreated', (e) => seen.push(e.message.text))
  hubB.on('ticketChanged', (t) => seen.push(`ticket:${t.number}`))
  await hubB.start()
  check('operator connected to instance B', hubB.state === 'Connected')
  const session = await (await fetch(`${PUB}/api/widget/${W}/session`, { method: 'POST', headers: { origin: ORIGIN } })).json()
  const text = `Scale test ${run}`
  const sent = await fetch(`${PUB}/api/widget/${W}/messages`, {
    method: 'POST',
    headers: { origin: ORIGIN, 'content-type': 'application/json', 'x-relayo-visitor': session.visitorToken },
    body: JSON.stringify({ text, clientMessageId: `s-${run}` }),
  })
  const message = await sent.json()
  check('visitor message accepted', sent.status === 200)
  check('operator on B gets the message handled by A', await wait(() => seen.includes(text)), JSON.stringify(seen))

  // 4. Ответ через B: оператор на A и посетитель получают его
  const hubA = operatorHub(A, token)
  const seenA = []
  hubA.on('messageCreated', (e) => seenA.push(e.message.text))
  await hubA.start()
  const widgetHub = new signalR.HubConnectionBuilder()
    .withUrl(`${PUB}/hubs/widget?widgetId=${W}`, { transport: signalR.HttpTransportType.WebSockets, skipNegotiation: true, accessTokenFactory: () => session.visitorToken, headers: { Origin: ORIGIN } })
    .configureLogging(signalR.LogLevel.None).build()
  const visitorSeen = []
  widgetHub.on('message', (m) => visitorSeen.push(m.text))
  await widgetHub.start()
  const tickets = await (await fetch(`${B}/api/tickets`, { headers: { authorization: `Bearer ${token}` } })).json()
  const ticket = tickets.find((t) => t.lastMessagePreview === text)
  const replyText = `Reply via B ${run}`
  const reply = await fetch(`${B}/api/tickets/${ticket.id}/messages`, {
    method: 'POST',
    headers: { authorization: `Bearer ${token}`, 'content-type': 'application/json' },
    body: JSON.stringify({ chatId: message.chatId, text: replyText }),
  })
  check('reply through instance B', reply.status === 200, String(reply.status))
  check('operator on A gets the reply sent through B', await wait(() => seenA.includes(replyText)), JSON.stringify(seenA))
  check('visitor gets the reply sent through B', await wait(() => visitorSeen.includes(replyText)), JSON.stringify(visitorSeen))
  await Promise.all([hubA.stop(), hubB.stop(), widgetHub.stop()])
  await fetch(`${A}/api/tickets/${ticket.id}/close`, { method: 'POST', headers: { authorization: `Bearer ${token}` } })

  // 5. Опрос Telegram держит один экземпляр
  const owner = redis('GET', 'relayo:lease:telegram-polling')
  const ttl = Number(redis('PTTL', 'relayo:lease:telegram-polling'))
  check('one instance holds the Telegram polling lease', owner.length > 0 && ttl > 0 && ttl <= 30000, `${owner} ttl=${ttl}`)

  // 6. Лимит входа общий: 10 попыток в минуту на все экземпляры вместе.
  // Ждём начала новой минуты, чтобы попытки выше не съели часть лимита.
  await new Promise((r) => setTimeout(r, 60_000 - (Date.now() % 60_000) + 200))
  const statuses = []
  for (let i = 0; i < 11; i++) statuses.push((await login(i % 2 ? B : A, 'wrong-password')).status)
  check('login limit is shared: 10 tries pass, the 11th is rejected', statuses.slice(0, 10).every((s) => s === 401) && statuses[10] === 429, statuses.join(','))
  check('rate limit counter lives in Redis', redis('--scan', '--pattern', 'relayo:rl:login:*').length > 0)

  console.log(failed ? `${failed} FAILED` : 'ALL PASSED')
  process.exit(failed ? 1 : 0)
})().catch((e) => { console.error(e); process.exit(1) })
