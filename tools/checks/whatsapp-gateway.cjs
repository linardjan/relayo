// Канал WhatsApp в режиме QR без настоящего телефона: вместо шлюза src/Relayo.WhatsApp на его порту
// (WHATSAPP_GATEWAY_URL) поднимается поддельный, а входящие сообщения шлются в Internal так же, как их шлёт шлюз.
// Настоящий шлюз на время проверки нужно остановить: порт занят им.
// Проверяет: QR, подключение, входящие (текст, файл, повтор, неподдерживаемое), ответ оператора, код перехода
// из виджета, отключение, удаление, ключ шлюза. Тестовый виджет возвращается к прежним настройкам.
const http = require('http')
const { chromium } = require('playwright-core')
const { sql, close, env } = require('./db.cjs')

const ADMIN = 'http://localhost:5100'
const PUB = 'http://localhost:5200'
const ORIGIN = 'http://localhost:5300'
const W = env.TEST_WIDGET_TOKEN_MODE_ID
const KEY = env.WHATSAPP_GATEWAY_KEY
const GATEWAY_PORT = Number(new URL(env.WHATSAPP_GATEWAY_URL).port || 80)
const run = Date.now() % 1_000_000
const PHONE = `7999${String(run).padStart(7, '0')}`
const CLIENT = `7998${String(run).padStart(7, '0')}`
const CLIENT2 = `7997${String(run).padStart(7, '0')}`
const PNG = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII=', 'base64')
let failed = 0
const check = (name, ok, extra = '') => { console.log(`${ok ? 'PASS' : 'FAIL'} ${name}${extra ? ' — ' + extra : ''}`); if (!ok) failed++ }
const sleep = (ms) => new Promise((r) => setTimeout(r, ms))

// ---- Поддельный шлюз ----
const calls = []
let session = { status: 'disconnected', qr: null, qrExpiresAt: null, phone: null, name: null, reason: null }
const fake = http.createServer(async (req, res) => {
  let body = ''
  for await (const chunk of req) body += chunk
  const call = { method: req.method, path: req.url, key: req.headers['x-relayo-gateway-key'], body: body ? JSON.parse(body) : null }
  calls.push(call)
  const reply = (status, data) => { res.writeHead(status, { 'content-type': 'application/json' }); res.end(data === undefined ? undefined : JSON.stringify(data)) }
  if (call.key !== KEY) return reply(401, { error: 'unauthorized' })
  if (/\/messages$/.test(req.url)) return session.status === 'connected' ? reply(200, { id: `out-${calls.length}` }) : reply(409, { error: 'not_connected' })
  if (req.method === 'PUT') session = { ...session, status: 'qr', qr: `fake-qr-${run}`, qrExpiresAt: new Date(Date.now() + 20000).toISOString() }
  if (req.method === 'DELETE') { session = { status: 'disconnected', qr: null, qrExpiresAt: null, phone: null, name: null, reason: null }; return reply(204) }
  return reply(200, session)
})
const sentTo = (jid, predicate) => calls.filter((c) => c.method === 'POST' && /\/messages$/.test(c.path) && c.body?.to === jid && predicate(c.body))
const waitFor = async (fn, ms = 8000) => { for (const end = Date.now() + ms; Date.now() < end; await sleep(200)) if (fn()) return true; return false }

;(async () => {
  await new Promise((resolve, reject) => fake.once('error', reject).listen(GATEWAY_PORT, resolve)).catch((e) => {
    console.error(`Port ${GATEWAY_PORT} is busy: stop the real WhatsApp gateway first (${e.code})`)
    process.exit(2)
  })

  const login = await (await fetch(`${ADMIN}/api/auth/login`, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ login: 'admin', password: env.BOOTSTRAP_ADMIN_PASSWORD }) })).json()
  const auth = { authorization: `Bearer ${login.accessToken}`, 'content-type': 'application/json' }
  const api = async (method, path, body) => { const r = await fetch(`${ADMIN}/api${path}`, { method, headers: auth, body: body ? JSON.stringify(body) : undefined }); return { status: r.status, body: await r.json().catch(() => null) } }
  const gw = async (path, body, key = KEY) => (await fetch(`${ADMIN}/api/whatsapp-gateway/channels/${path}`, { method: 'POST', headers: { 'content-type': 'application/json', 'x-relayo-gateway-key': key }, body: JSON.stringify(body) })).status
  const incoming = (chatJid, messageId, extra) => ({ messageId, chatJid, phone: chatJid.split('@')[0], pushName: `Ivan ${run}`, text: '', files: [], unsupported: null, ...extra })
  const me = (await api('GET', '/me')).body
  if (!me?.alias) await api('PUT', '/me/alias', { alias: 'Anna' })
  const alias = me?.alias || 'Anna'

  const original = (await api('GET', '/settings/widgets')).body.find((r) => r.widget.id === W).widget
  const { id: _i, siteId, siteCode: _s, createdAt: _c, publicKey: _p, publishedVersion: _pv, publishedAt: _pa, updatedAt: _u, hasUnpublishedChanges: _h, ...widgetInput } = original
  let channelId = null
  try {
    // Канал в режиме QR: номера пока нет
    const created = await api('POST', '/settings/whatsapp-channels', { name: `WA QR ${run}`, mode: 'Gateway', siteId, isEnabled: true })
    channelId = created.body?.id
    check('QR channel created without a phone', created.status === 200 && created.body.mode === 'Gateway' && created.body.phoneNumber === null, String(created.status))

    // QR
    const started = await api('POST', `/settings/whatsapp-channels/${channelId}/connect`)
    check('connect asks the gateway for a QR', started.body?.status === 'qr' && started.body.qr === `fake-qr-${run}`, JSON.stringify(started.body))
    check('gateway called with the key', calls.some((c) => c.method === 'PUT' && c.path === `/sessions/${channelId}` && c.key === KEY))
    const state = await api('GET', `/settings/whatsapp-channels/${channelId}/connection`)
    check('connection state shows the QR', state.body?.configured === true && state.body.qr === `fake-qr-${run}`)

    // Ключ шлюза
    check('callback without the key → 401', (await gw(`${channelId}/status`, { status: 'connected' }, 'wrong-key-wrong-key-wrong-key-wrong')) === 401)
    check('callback for unknown channel → 404', (await gw(`00000000-0000-4000-8000-000000000000/status`, { status: 'connected', phone: PHONE })) === 404)

    // Подключение: номер приходит из WhatsApp
    session = { status: 'connected', qr: null, qrExpiresAt: null, phone: PHONE, name: 'Support', reason: null }
    check('status callback accepted', (await gw(`${channelId}/status`, { status: 'connected', phone: PHONE, name: 'Support' })) === 204)
    let channel = (await api('GET', '/settings/whatsapp-channels')).body.find((c) => c.id === channelId)
    check('channel connected, phone from WhatsApp', channel.isConnected && channel.phoneNumber === PHONE && channel.accountName === 'Support', JSON.stringify(channel))
    const switchBack = await api('PUT', `/settings/whatsapp-channels/${channelId}`, { name: channel.name, mode: 'LinkOnly', phoneNumber: PHONE, siteId, isEnabled: true })
    check('connected number cannot switch to link-only', switchBack.status === 400, String(switchBack.status))

    // Входящее: новый клиент и тикет
    const jid = `${CLIENT}@s.whatsapp.net`
    check('message accepted', (await gw(`${channelId}/messages`, incoming(jid, `m1-${run}`, { text: `Hello from WhatsApp ${run}` }))) === 204)
    const ticketId = await sql(`select top 1 c.ticket_id from messages m join chats c on c.id = m.chat_id where m.text = 'Hello from WhatsApp ${run}'`)
    check('ticket opened with a WhatsApp message', /^[0-9a-f-]{36}$/i.test(ticketId), ticketId)
    const identity = await sql(`select i.display_name, i.external_id from client_identities i where i.type = 'WhatsApp' and i.external_id like '%${CLIENT}%'`)
    check('client identity has name and phone', identity.startsWith(`Ivan ${run} (+${CLIENT})|`) && identity.endsWith(`:${jid}`), identity)

    // Повтор того же сообщения не дублирует
    await gw(`${channelId}/messages`, incoming(jid, `m1-${run}`, { text: `Hello from WhatsApp ${run}` }))
    check('repeated message is not duplicated', (await sql(`select count(*) from messages where text = 'Hello from WhatsApp ${run}'`)) === '1')

    // Файл и неподдерживаемое
    await gw(`${channelId}/messages`, incoming(jid, `m2-${run}`, { text: `Photo ${run}`, files: [{ fileName: 'photo.png', contentType: 'image/png', content: PNG.toString('base64') }] }))
    check('file stored as attachment', (await sql(`select count(*) from attachments a join messages m on m.id = a.message_id where m.text = 'Photo ${run}'`)) === '1')
    await gw(`${channelId}/messages`, incoming(jid, `m3-${run}`, { unsupported: 'location' }))
    check('unsupported content leaves a note', (await sql(`select count(*) from messages m join chats c on c.id = m.chat_id where c.ticket_id = '${ticketId}' and m.text like '%sent a location%'`)) === '1')

    // Ответ оператора уходит в WhatsApp с псевдонимом
    const details = (await api('GET', `/tickets/${ticketId}`)).body
    const chat = details.chats[details.chats.length - 1]
    check('chat replies to WhatsApp', chat.replyChannel === 'WhatsApp', chat.replyChannel)
    const reply = await api('POST', `/tickets/${ticketId}/messages`, { chatId: chat.id, text: `Answer ${run}` })
    check('operator reply saved', reply.status === 200, String(reply.status))
    check('reply sent to WhatsApp with alias', await waitFor(() => sentTo(jid, (b) => b.text === `*${alias}*\nAnswer ${run}`).length === 1),
      JSON.stringify(calls.filter((c) => c.method === 'POST').map((c) => c.body?.text)))

    // Переход из виджета с кодом: переписка продолжается в том же тикете
    const items = [...widgetInput.options.items.filter((i) => i.type !== 'WhatsApp'), { type: 'WhatsApp', label: '' }]
    await api('PUT', `/settings/widgets/${W}`, { ...widgetInput, whatsAppChannelId: channelId, options: { ...widgetInput.options, items } })
    const visitor = (await (await fetch(`${PUB}/api/widget/${W}/session`, { method: 'POST', headers: { origin: ORIGIN } })).json()).visitorToken
    const widgetCall = (path, body) => fetch(`${PUB}/api/widget/${W}${path}`, { method: 'POST', headers: { origin: ORIGIN, 'content-type': 'application/json', 'x-relayo-visitor': visitor }, body: body ? JSON.stringify(body) : undefined })
    const sent = await (await widgetCall('/messages', { text: `From the site ${run}`, clientMessageId: `wa-site-${run}` })).json()
    const link = await (await widgetCall('/whatsapp-link')).json()
    const code = /My%20code%3A%20([A-Z0-9]{8})$/.exec(link.url ?? '')?.[1]
    check('widget gets wa.me link with a code', link.url?.startsWith(`https://wa.me/${PHONE}?text=`) && !!code, link.url)
    // На сайте: опубликованный виджет, кнопка WhatsApp открывает ссылку с кодом
    await api('POST', `/settings/widgets/${W}/publish`)
    const browser = await chromium.launch({ channel: 'chrome', headless: true })
    try {
      const context = await browser.newContext()
      await context.route('https://wa.me/**', (r) => r.fulfill({ status: 200, contentType: 'text/html', body: 'wa' }))
      await context.route('https://api.whatsapp.com/**', (r) => r.fulfill({ status: 200, contentType: 'text/html', body: 'wa' }))
      const page = await context.newPage()
      await page.goto(`${ORIGIN}/?r=${run}`)
      await page.locator('.round.whatsapp').waitFor({ timeout: 15000 })
      const [popup] = await Promise.all([context.waitForEvent('page', { timeout: 10000 }), page.locator('.round.whatsapp').click()])
      await popup.waitForURL(/wa\.me/, { timeout: 10000 }).catch(() => {})
      check('site button opens wa.me with a code', new RegExp(`^https://wa\\.me/${PHONE}\\?text=My%20code%3A%20[A-Z0-9]{8}$`).test(popup.url()), popup.url())
    } finally {
      await browser.close()
    }

    const jid2 = `${CLIENT2}@s.whatsapp.net`
    await gw(`${channelId}/messages`, incoming(jid2, `m4-${run}`, { text: `My code: ${code}\nContinuing here ${run}` }))
    const visitorTicket = await sql(`select ticket_id from chats where id = '${sent.chatId}'`)
    const continued = await sql(`select count(*) from messages m join chats c on c.id = m.chat_id where c.ticket_id = '${visitorTicket}' and m.text = 'Continuing here ${run}'`)
    check('message with code lands in the visitor ticket, code cut out', continued === '1', continued)
    check('system note about WhatsApp in the chat', (await sql(`select count(*) from messages where chat_id = '${sent.chatId}' and text = 'The client continued the chat in WhatsApp'`)) === '1')
    check('client told the chat is connected', await waitFor(() => sentTo(jid2, (b) => b.text?.startsWith("You're connected")).length === 1))
    await gw(`${channelId}/messages`, incoming(`7996${String(run).padStart(7, '0')}@s.whatsapp.net`, `m5-${run}`, { text: `My code: ${code}` }))
    check('used code → expired notice', await waitFor(() => sentTo(`7996${String(run).padStart(7, '0')}@s.whatsapp.net`, (b) => b.text?.startsWith('This code has expired')).length === 1))

    // Отключение
    const disconnected = await api('POST', `/settings/whatsapp-channels/${channelId}/disconnect`)
    check('disconnect logs out in the gateway', disconnected.status === 200 && !disconnected.body.isConnected
      && calls.some((c) => c.method === 'DELETE' && c.path === `/sessions/${channelId}`), String(disconnected.status))
    check('phone stays after disconnect', disconnected.body?.phoneNumber === PHONE)

    // Удаление
    const deleted = await api('DELETE', `/settings/whatsapp-channels/${channelId}`)
    check('channel deleted', deleted.status === 204, String(deleted.status))
    channelId = null
    check('messages for a deleted channel → 404 (gateway logs out)', (await gw(`${created.body.id}/messages`, incoming(jid, `m6-${run}`, { text: 'late' }))) === 404)
  } finally {
    if (channelId) await api('DELETE', `/settings/whatsapp-channels/${channelId}`)
    await api('PUT', `/settings/widgets/${W}`, widgetInput)
    if (original.publishedVersion) await api('POST', `/settings/widgets/${W}/publish`)
    fake.close()
    await close?.()
  }

  console.log(failed ? `${failed} FAILED` : 'ALL PASSED')
  process.exit(failed ? 1 : 0)
})().catch((e) => { console.error(e); process.exit(1) })
