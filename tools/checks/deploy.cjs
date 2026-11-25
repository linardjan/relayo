// Проверка боевого стенда (deploy/docker-compose.yml): админка через nginx, публичный сервис, шина, лимиты.
const ADMIN = 'http://localhost:6080'
const PUB = 'http://localhost:6200'
const PASSWORD = 'Deploy_test_Passw0rd'
const ORIGIN = 'https://shop.example.com'
let failed = 0
const check = (name, ok, extra = '') => { console.log(`${ok ? 'PASS' : 'FAIL'} ${name}${extra ? ' — ' + extra : ''}`); if (!ok) failed++ }
const sleep = (ms) => new Promise((r) => setTimeout(r, ms))

;(async () => {
  // Вход через nginx: access-токен и refresh-cookie
  const login = await fetch(`${ADMIN}/api/auth/login`, { method: 'POST', headers: { 'content-type': 'application/json', 'x-forwarded-for': '203.0.113.1' }, body: JSON.stringify({ login: 'admin', password: PASSWORD }) })
  const body = await login.json()
  const cookie = login.headers.get('set-cookie') ?? ''
  check('login via nginx', login.status === 200 && !!body.accessToken)
  check('refresh cookie is Secure, HttpOnly, SameSite=Strict', /secure/i.test(cookie) && /httponly/i.test(cookie) && /samesite=strict/i.test(cookie), cookie.replace(/=[^;]+;/, '=…;').slice(0, 90))
  const refresh = await fetch(`${ADMIN}/api/auth/refresh`, { method: 'POST', headers: { cookie: cookie.split(';')[0] } })
  check('refresh via nginx', refresh.status === 200)
  const auth = { authorization: `Bearer ${body.accessToken}`, 'content-type': 'application/json' }

  // Сайт и виджет
  const site = await (await fetch(`${ADMIN}/api/settings/sites`, { method: 'POST', headers: auth, body: JSON.stringify({ code: 'shop', name: 'Shop', allowedOrigins: [ORIGIN] }) })).json()
  const w = await (await fetch(`${ADMIN}/api/settings/widgets`, { method: 'POST', headers: auth, body: JSON.stringify({ siteId: site.id, name: 'Shop widget', identificationMode: 'Direct', title: 'Support', greeting: 'Hi', accentColor: '#1677ff', position: 'Right', defaultLocale: 'en', telegramBotId: null }) })).json()
  check('site and widget created', !!w.widget?.id)
  check('embed code is one line with PUBLIC_BASE_URL', w.embedCode === `<script async src="http://localhost:6200/widget/core.js" data-relayo-widget="${w.widget.id}"></script>`, w.embedCode)

  // Публичный сервис: конфиг через gRPC, сообщение, тикет у оператора
  const wid = w.widget.id
  const h = { origin: ORIGIN, 'content-type': 'application/json' }
  let config
  for (let i = 0; i < 20; i++) { config = await fetch(`${PUB}/api/widget/${wid}/config`, { headers: h }); if (config.status === 200) break; await sleep(1000) }
  check('widget config via gRPC', config.status === 200)
  check('foreign origin rejected', (await fetch(`${PUB}/api/widget/${wid}/config`, { headers: { origin: 'https://evil.example' } })).status === 403)
  const session = await (await fetch(`${PUB}/api/widget/${wid}/session`, { method: 'POST', headers: h })).json()
  const sent = await fetch(`${PUB}/api/widget/${wid}/messages`, { method: 'POST', headers: { ...h, 'x-relayo-visitor': session.visitorToken }, body: JSON.stringify({ text: 'Hello from prod stand', clientMessageId: 'p1' }) })
  check('visitor message sent', sent.status === 200)
  const tickets = await (await fetch(`${ADMIN}/api/tickets`, { headers: auth })).json()
  const ticket = tickets.find((t) => t.lastMessagePreview === 'Hello from prod stand')
  check('ticket visible in admin', !!ticket)

  // Ответ оператора приходит в историю виджета
  await fetch(`${ADMIN}/api/me/alias`, { method: 'PUT', headers: auth, body: JSON.stringify({ alias: 'Ops' }) })
  const details = await (await fetch(`${ADMIN}/api/tickets/${ticket.id}`, { headers: auth })).json()
  const reply = await fetch(`${ADMIN}/api/tickets/${ticket.id}/messages`, { method: 'POST', headers: auth, body: JSON.stringify({ chatId: details.chats[0].id, text: 'Reply from prod' }) })
  check('operator reply', reply.status === 200)
  const history = await (await fetch(`${PUB}/api/widget/${wid}/messages`, { headers: { ...h, 'x-relayo-visitor': session.visitorToken } })).json()
  check('reply visible to visitor with alias only', history.some((m) => m.text === 'Reply from prod' && m.authorName === 'Ops'))

  // Лимит входа считается по клиенту из X-Forwarded-For, а не по адресу nginx
  const bad = async (ip) => (await fetch(`${ADMIN}/api/auth/login`, { method: 'POST', headers: { 'content-type': 'application/json', 'x-forwarded-for': ip }, body: JSON.stringify({ login: 'admin', password: 'wrong' }) })).status
  const codes = []
  for (let i = 0; i < 11; i++) codes.push(await bad('198.51.100.7'))
  check('11th login from one client is limited', codes.at(-1) === 429, codes.join(','))
  check('another client is not affected', (await bad('198.51.100.8')) === 401)

  // Админка без кэша, сборка с кэшем
  const index = await fetch(`${ADMIN}/`)
  check('index.html not cached', index.headers.get('cache-control') === 'no-cache')
  check('security headers', index.headers.get('x-frame-options') === 'DENY' && index.headers.get('x-content-type-options') === 'nosniff')

  console.log(failed ? `${failed} FAILED` : 'ALL PASSED')
  process.exit(failed ? 1 : 0)
})().catch((e) => { console.error(e); process.exit(1) })
