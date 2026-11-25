// Стенд deploy/: публикация виджета в S3 и работа виджета на странице сайта.
const { chromium } = require('playwright-core')
const ADMIN = 'http://localhost:6080'
const SITE = 'http://localhost:6300'
let failed = 0
const check = (name, ok, extra = '') => { console.log(`${ok ? 'PASS' : 'FAIL'} ${name}${extra ? ' — ' + extra : ''}`); if (!ok) failed++ }
const sleep = (ms) => new Promise((r) => setTimeout(r, ms))

;(async () => {
  let login
  for (let i = 0; i < 30; i++) {
    const r = await fetch(`${ADMIN}/api/auth/login`, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ login: 'admin', password: 'Deploy_test_Passw0rd' }) }).catch(() => null)
    if (r?.status === 200) { login = await r.json(); break }
    await sleep(2000)
  }
  const auth = { authorization: `Bearer ${login.accessToken}`, 'content-type': 'application/json' }
  const call = async (method, path, body) => { const r = await fetch(`${ADMIN}/api${path}`, { method, headers: auth, body: body ? JSON.stringify(body) : undefined }); return { status: r.status, body: await r.json().catch(() => null) } }

  const site = (await call('POST', '/settings/sites', { code: 'shop4', name: 'Shop 4', allowedOrigins: [SITE] })).body
  const w = (await call('POST', '/settings/widgets', { siteId: site.id, name: 'Shop widget', identificationMode: 'Direct', title: 'Shop support', greeting: 'Hi', accentColor: '#e83e4d', position: 'Right', defaultLocale: 'en', telegramBotId: null })).body
  check('widget not published yet → no embed code', w.widget.publishedVersion === null && !!w.fileUrl)
  const published = await call('POST', `/settings/widgets/${w.widget.id}/publish`)
  check('publish to S3 on the stand', published.status === 200 && published.body.widget.publishedVersion === 1, String(published.status) + ' ' + JSON.stringify(published.body?.title ?? ''))
  const fileUrl = published.body.fileUrl
  const file = await fetch(fileUrl)
  check('widget file readable anonymously', file.status === 200 && /widget_.{10}\.js$/.test(fileUrl), `${file.status} ${fileUrl}`)
  await call('PUT', '/me/alias', { alias: 'Ops' })

  // Публичный сервис обновляет список доменов раз в минуту
  console.log('… waiting for the public service to pick up the new site domain (up to 70 s)')
  for (let i = 0; i < 35; i++) {
    const r = await fetch(`http://localhost:6200/api/widget/${w.widget.id}/session`, { method: 'OPTIONS', headers: { origin: SITE, 'access-control-request-method': 'POST' } })
    if (r.headers.get('access-control-allow-origin') === SITE) break
    await sleep(2000)
  }

  const browser = await chromium.launch({ channel: 'chrome', headless: true })
  const page = await browser.newPage()
  const errors = []
  page.on('pageerror', (e) => errors.push(e.message))
  // Настоящая страница «сайта» на localhost:6300 с кодом вставки из админки.
  const server = require('http').createServer((req, res) => {
    res.writeHead(200, { 'content-type': 'text/html' })
    res.end(`<!doctype html><html><body><h1>Shop</h1>${published.body.embedCode}</body></html>`)
  }).listen(6300)
  await page.goto(`${SITE}/`)
  await page.locator('.round.chat').waitFor({ timeout: 15000 })
  await page.locator('.round.chat').click()
  check('widget title from the published file', (await page.locator('.header .title').textContent()) === 'Shop support')
  await page.locator('.composer input').fill('Hello from the stand')
  await page.locator('.composer input').press('Enter')
  await sleep(2000)
  const tickets = (await call('GET', '/tickets')).body
  check('message reached the operator', tickets.some((t) => t.lastMessagePreview === 'Hello from the stand'))
  check('no page errors', errors.length === 0, errors.join('; '))
  await browser.close()
  server.close()
  console.log(failed ? `${failed} FAILED` : 'ALL PASSED')
  process.exit(failed ? 1 : 0)
})().catch((e) => { console.error(e); process.exit(1) })
