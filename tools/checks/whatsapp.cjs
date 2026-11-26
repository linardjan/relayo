// Канал WhatsApp: API настроек, пункт WhatsApp в виджете, переход на wa.me/<номер> с тестового сайта.
// Меняет тестовый виджет (token-mode) и в конце возвращает его прежние настройки и публикует их.
const fs = require('fs')
const { chromium } = require('playwright-core')
const env = Object.fromEntries(fs.readFileSync(require('path').join(__dirname, '../..') + '/.env', 'utf8').split('\n')
  .filter((l) => /^[A-Z_]+=/.test(l)).map((l) => { const i = l.indexOf('='); return [l.slice(0, i), l.slice(i + 1).replace(/^"|"$/g, '')] }))
const ADMIN = 'http://localhost:5100'
const ORIGIN = 'http://localhost:5300'
const FILES = 'http://localhost:9000/relayo-widgets'
const W = env.TEST_WIDGET_TOKEN_MODE_ID
const run = Date.now() % 1_000_000
// Явно тестовый номер: 7 999 ... + номер прогона.
const PHONE = `7999${String(run).padStart(7, '0')}`
let failed = 0
const check = (name, ok, extra = '') => { console.log(`${ok ? 'PASS' : 'FAIL'} ${name}${extra ? ' — ' + extra : ''}`); if (!ok) failed++ }

;(async () => {
  const login = await (await fetch(`${ADMIN}/api/auth/login`, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ login: 'admin', password: env.BOOTSTRAP_ADMIN_PASSWORD }) })).json()
  const auth = { authorization: `Bearer ${login.accessToken}`, 'content-type': 'application/json' }
  const call = async (method, path, body) => { const r = await fetch(`${ADMIN}/api/settings${path}`, { method, headers: auth, body: body ? JSON.stringify(body) : undefined }); return { status: r.status, body: await r.json().catch(() => null) } }
  // Настройки в опубликованном файле: так их получают сайты. /config публичного сервиса кэшируется на 30 с.
  const file = async (key) => {
    const text = await (await fetch(`${FILES}/widget_${key}.js`, { cache: 'no-store' })).text()
    const m = /data-relayo-config', (".*")\);/.exec(text)
    return m ? JSON.parse(JSON.parse(m[1])) : null
  }
  const row = async () => (await call('GET', '/widgets')).body.find((r) => r.widget.id === W)

  const original = (await row()).widget
  const { id, siteId, siteCode, createdAt, publicKey, publishedVersion, publishedAt, updatedAt, hasUnpublishedChanges, ...input } = original
  let channelId = null
  try {
    // Канал: номер приводится к цифрам, повтор и мусор отклоняются
    const bad = await call('POST', '/whatsapp-channels', { name: `WA ${run}`, phoneNumber: '12-34', siteId, isEnabled: true })
    check('bad phone → 400', bad.status === 400, `${bad.status} ${bad.body?.detail ?? ''}`)
    const created = await call('POST', '/whatsapp-channels', { name: `WA ${run}`, phoneNumber: `+7 (999) ${PHONE.slice(4, 7)}-${PHONE.slice(7)}`, siteId, isEnabled: true })
    channelId = created.body?.id
    check('channel created, phone normalized', created.status === 200 && created.body.phoneNumber === PHONE, `${created.status} ${created.body?.phoneNumber}`)
    const dup = await call('POST', '/whatsapp-channels', { name: `WA dup ${run}`, phoneNumber: PHONE, siteId, isEnabled: true })
    check('same phone twice → 409', dup.status === 409, String(dup.status))
    check('channel in list', (await call('GET', '/whatsapp-channels')).body.some((c) => c.id === channelId))

    // Пункт WhatsApp в виджете
    const items = [...input.options.items.filter((i) => i.type !== 'WhatsApp'), { type: 'WhatsApp', label: '' }]
    const saved = await call('PUT', `/widgets/${W}`, { ...input, whatsAppChannelId: channelId, options: { ...input.options, items } })
    check('widget saved with WhatsApp item', saved.status === 200 && saved.body.widget.whatsAppChannelId === channelId
      && saved.body.widget.options.items.some((i) => i.type === 'WhatsApp' && i.label === 'WhatsApp'), String(saved.status))
    const counted = (await call('GET', '/whatsapp-channels')).body.find((x) => x.id === channelId)
    check('channel counts the widget', counted.widgetCount === 1, String(counted.widgetCount))

    // Выключенный канал — номера в опубликованном виджете нет
    await call('PUT', `/whatsapp-channels/${channelId}`, { name: `WA ${run}`, phoneNumber: PHONE, siteId, isEnabled: false })
    await call('POST', `/widgets/${W}/publish`)
    check('disabled channel: no phone in the published file', (await file(publicKey)).whatsApp == null)
    await call('PUT', `/whatsapp-channels/${channelId}`, { name: `WA ${run}`, phoneNumber: PHONE, siteId, isEnabled: true })

    // Переход с тестового сайта: опубликованный файл, кнопка и Relayo.open('whatsapp')
    const published = await call('POST', `/widgets/${W}/publish`)
    const pub = await file(publicKey)
    check('published file has the phone and the item', published.status === 200 && pub.whatsApp === PHONE
      && pub.options.items.some((i) => i.type === 'WhatsApp'), String(published.status))
    const browser = await chromium.launch({ channel: 'chrome', headless: true })
    const context = await browser.newContext({ viewport: { width: 1280, height: 800 } })
    // В WhatsApp на самом деле не ходим: адрес проверяем, ответ подставляем.
    await context.route('https://wa.me/**', (r) => r.fulfill({ status: 200, contentType: 'text/html', body: 'wa' }))
    await context.route('https://api.whatsapp.com/**', (r) => r.fulfill({ status: 200, contentType: 'text/html', body: 'wa' }))
    const page = await context.newPage()
    const errors = []
    page.on('pageerror', (e) => errors.push(e.message))
    await page.goto(`${ORIGIN}/?r=${run}`)
    await page.locator('.round.whatsapp').waitFor({ timeout: 15000 })
    check('WhatsApp button on the site', true)
    const [popup] = await Promise.all([context.waitForEvent('page', { timeout: 10000 }), page.locator('.round.whatsapp').click()])
    await popup.waitForLoadState().catch(() => {})
    check('button opens wa.me/<phone>', popup.url() === `https://wa.me/${PHONE}`, popup.url())
    await popup.close()
    const [popup2] = await Promise.all([context.waitForEvent('page', { timeout: 10000 }), page.evaluate(() => window.Relayo.open('whatsapp'))])
    await popup2.waitForLoadState().catch(() => {})
    check("Relayo.open('whatsapp') opens wa.me/<phone>", popup2.url() === `https://wa.me/${PHONE}`, popup2.url())
    check('no page errors', errors.length === 0, errors.join('; '))
    await browser.close()

    // Удаление канала убирает пункт из виджета
    const del = await call('DELETE', `/whatsapp-channels/${channelId}`)
    check('channel deleted', del.status === 204, String(del.status))
    channelId = null
    const after = (await row()).widget
    check('widget lost the channel and the item', after.whatsAppChannelId === null && !after.options.items.some((i) => i.type === 'WhatsApp'))
    await call('POST', `/widgets/${W}/publish`)
    const last = await file(publicKey)
    check('published file has no phone and no item', last.whatsApp == null && !last.options.items.some((i) => i.type === 'WhatsApp'))
  } finally {
    if (channelId) await call('DELETE', `/whatsapp-channels/${channelId}`)
    // Прежние настройки виджета — и на сайте тоже.
    await call('PUT', `/widgets/${W}`, input)
    if (original.publishedVersion) await call('POST', `/widgets/${W}/publish`)
  }

  console.log(failed ? `${failed} FAILED` : 'ALL PASSED')
  process.exit(failed ? 1 : 0)
})().catch((e) => { console.error(e); process.exit(1) })
