// Публикация виджета в S3: версии, откат, код вставки, загрузка на сайте без /config.
const fs = require('fs')
const { chromium } = require('playwright-core')
const env = Object.fromEntries(fs.readFileSync(require('path').join(__dirname, '../..') + '/.env', 'utf8').split('\n')
  .filter((l) => /^[A-Z_]+=/.test(l)).map((l) => { const i = l.indexOf('='); return [l.slice(0, i), l.slice(i + 1).replace(/^"|"$/g, '')] }))
const ADMIN = 'http://localhost:5100'
const FILES = 'http://localhost:9000/relayo-widgets'
const W = env.TEST_WIDGET_TOKEN_MODE_ID
let failed = 0
const check = (name, ok, extra = '') => { console.log(`${ok ? 'PASS' : 'FAIL'} ${name}${extra ? ' — ' + extra : ''}`); if (!ok) failed++ }
const file = async (name) => {
  const r = await fetch(`${FILES}/${name}`, { cache: 'no-store' })
  const text = await r.text()
  // Настройки — JS-строка в атрибуте data-relayo-config: разбираем её как JSON-строку, затем как объект.
  const m = /data-relayo-config', (".*")\);/.exec(text)
  return { status: r.status, config: m ? JSON.parse(JSON.parse(m[1])) : null }
}

;(async () => {
  const login = await (await fetch(`${ADMIN}/api/auth/login`, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ login: 'admin', password: env.BOOTSTRAP_ADMIN_PASSWORD }) })).json()
  const auth = { authorization: `Bearer ${login.accessToken}`, 'content-type': 'application/json' }
  const call = async (method, path, body) => { const r = await fetch(`${ADMIN}/api/settings${path}`, { method, headers: auth, body: body ? JSON.stringify(body) : undefined }); return { status: r.status, body: await r.json().catch(() => null) } }
  const row = async () => (await call('GET', '/widgets')).body.find((r) => r.widget.id === W)

  let r = await row()
  check('test widget published at startup', r.widget.publishedVersion >= 1 && !r.widget.hasUnpublishedChanges, `v${r.widget.publishedVersion}`)
  check('embed code is one line with the S3 file', r.embedCode === `<script async src="${FILES}/widget_${r.widget.publicKey}.js"></script>`, r.embedCode)
  const start = r.widget.publishedVersion
  const { id, siteId, siteCode, createdAt, publicKey, publishedVersion, publishedAt, updatedAt, hasUnpublishedChanges, ...input } = r.widget

  // Правка — пока не опубликована, на сайтах старая версия
  await call('PUT', `/widgets/${W}`, { ...input, title: 'Help desk' })
  r = await row()
  check('saved change marks widget unpublished', r.widget.hasUnpublishedChanges)
  check('site file still has old title', (await file(`widget_${publicKey}.js`)).config?.title === 'Support')
  // Сохранение без изменений не делает виджет «неопубликованным»
  r = (await call('POST', `/widgets/${W}/publish`)).body
  check('publish → new version', r.widget.publishedVersion === start + 1 && !r.widget.hasUnpublishedChanges, `v${r.widget.publishedVersion}`)
  check('latest file has new title', (await file(`widget_${publicKey}.js`)).config?.title === 'Help desk')
  check('versioned file written', (await file(`widget_${publicKey}.v${start + 1}.js`)).status === 200)
  check('previous version file untouched', (await file(`widget_${publicKey}.v${start}.js`)).config?.title === 'Support')
  await call('PUT', `/widgets/${W}`, { ...input, title: 'Help desk' })
  check('saving the same values keeps it published', !(await row()).widget.hasUnpublishedChanges)

  // Откат
  r = (await call('POST', `/widgets/${W}/versions/${start}/rollback`)).body
  check('rollback publishes a new version', r.widget.publishedVersion === start + 2 && r.widget.title === 'Support', `v${r.widget.publishedVersion} ${r.widget.title}`)
  check('latest file rolled back', (await file(`widget_${publicKey}.js`)).config?.title === 'Support')
  const versions = (await call('GET', `/widgets/${W}/versions`)).body
  check('history has rollback note', versions[0].note === `Rollback to v${start}` && versions[0].publishedBy === 'Administrator', JSON.stringify(versions[0]))

  // Права
  const op = await (await fetch(`${ADMIN}/api/auth/login`, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ login: 'ivanov', password: 'Oper_Passw0rd' }) })).json()
  check('operator cannot publish', (await fetch(`${ADMIN}/api/settings/widgets/${W}/publish`, { method: 'POST', headers: { authorization: `Bearer ${op.accessToken}` } })).status === 403)

  // Сайт: виджет из файла S3, без запроса настроек
  const browser = await chromium.launch({ channel: 'chrome', headless: true })
  const page = await browser.newPage({ viewport: { width: 1280, height: 800 } })
  const requests = []
  page.on('request', (q) => requests.push(q.url()))
  const errors = []
  page.on('pageerror', (e) => errors.push(e.message))
  await page.goto('http://localhost:5300/')
  await page.locator('.round.chat').waitFor({ timeout: 15000 })
  check('site loads widget file from S3', requests.some((u) => u === `${FILES}/widget_${publicKey}.js`))
  check('core.js loaded from public service', requests.some((u) => u.endsWith('/widget/core.js')))
  check('no /config request (settings come from the file)', !requests.some((u) => u.includes('/config')))
  await page.locator('.round.chat').click()
  check('title from published settings', (await page.locator('.header .title').textContent()) === 'Support')
  await page.locator('.composer input').fill('Hello via S3 widget')
  await page.locator('.composer input').press('Enter')
  await page.waitForTimeout(1500)
  check('message sent from S3-loaded widget', (await page.locator('.out .bubble', { hasText: 'Hello via S3 widget' }).count()) === 1)
  check('no page errors', errors.length === 0, errors.join('; '))
  await browser.close()

  console.log(failed ? `${failed} FAILED` : 'ALL PASSED')
  process.exit(failed ? 1 : 0)
})().catch((e) => { console.error(e); process.exit(1) })
