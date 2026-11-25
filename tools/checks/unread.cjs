// Новые обращения: счётчик во вкладке, подсветка, бейджи, сброс при открытии.
const fs = require('fs')
const { chromium } = require('playwright-core')
const env = Object.fromEntries(fs.readFileSync(require('path').join(__dirname, '../..') + '/.env', 'utf8').split('\n')
  .filter((l) => /^[A-Z_]+=/.test(l)).map((l) => { const i = l.indexOf('='); return [l.slice(0, i), l.slice(i + 1).replace(/^"|"$/g, '')] }))
const WIDGET = env.TEST_WIDGET_TOKEN_MODE_ID
let failed = 0
const check = (name, ok, extra = '') => { console.log(`${ok ? 'PASS' : 'FAIL'} ${name}${extra ? ' — ' + extra : ''}`); if (!ok) failed++ }

async function visitorSays(text) {
  const h = { origin: 'http://localhost:5300', 'content-type': 'application/json' }
  const s = await (await fetch(`http://localhost:5200/api/widget/${WIDGET}/session`, { method: 'POST', headers: h })).json()
  await fetch(`http://localhost:5200/api/widget/${WIDGET}/messages`, { method: 'POST', headers: { ...h, 'x-relayo-visitor': s.visitorToken }, body: JSON.stringify({ text, clientMessageId: String(Date.now()) }) })
}

;(async () => {
  const browser = await chromium.launch({ channel: 'chrome', headless: true })
  const page = await browser.newPage({ viewport: { width: 1440, height: 900 } })
  const errors = []
  page.on('pageerror', (e) => errors.push(e.message))
  await page.goto('http://localhost:5173/login')
  await page.getByLabel(/login/i).first().fill('admin')
  await page.getByLabel(/password/i).fill(env.BOOTSTRAP_ADMIN_PASSWORD)
  await page.getByRole('button', { name: /sign in/i }).click()
  await page.waitForURL((u) => !u.pathname.startsWith('/login'))
  await page.waitForLoadState('networkidle')
  await page.waitForTimeout(1500)
  check('no counter at start (baseline)', (await page.title()) === 'Relayo', await page.title())

  const run = Date.now() % 100000
  await visitorSays(`Unread test ${run}`)
  await page.waitForFunction(() => document.title.startsWith('(1)'), null, { timeout: 8000 }).catch(() => {})
  check('tab title shows (1)', (await page.title()) === '(1) Chats · Relayo', await page.title())
  const row = page.locator('.inbox-row.unread', { hasText: `Unread test ${run}` })
  check('new ticket row highlighted', (await row.count()) === 1)
  check('Unassigned group shows "1 new"', (await page.locator('.inbox-group-new', { hasText: '1 new' }).count()) === 1)
  check('menu Chats badge shows 1', (await page.locator('.menu-badge').textContent()) === '1')
  await page.screenshot({ path: __dirname + '/out/unread-inbox.png' })

  // Вне Inbox счётчик тоже работает
  await page.goto('http://localhost:5173/clients')
  await page.waitForLoadState('networkidle')
  await visitorSays(`Unread test 2 ${run}`)
  await page.waitForFunction(() => document.title.startsWith('(2)'), null, { timeout: 8000 }).catch(() => {})
  check('counter updates on another page', (await page.title()) === '(2) Chats · Relayo', await page.title())

  // Открыли тикет — он просмотрен
  await page.goto('http://localhost:5173/')
  await page.waitForLoadState('networkidle')
  await page.locator('.inbox-row', { hasText: `Unread test ${run}` }).first().click()
  await page.waitForTimeout(1000)
  check('opened ticket is no longer counted', (await page.title()) === '(1) Chats · Relayo', await page.title())
  check('opened ticket not highlighted', (await page.locator('.inbox-row.unread', { hasText: `Unread test ${run}` }).count()) === 0)

  // После перезагрузки просмотренное помнится
  await page.reload()
  await page.waitForLoadState('networkidle')
  await page.waitForTimeout(1000)
  check('seen state survives reload', (await page.title()).startsWith('(1)') || (await page.title()) === 'Relayo', await page.title())

  check('no page errors', errors.length === 0, errors.join('; '))
  console.log(failed ? `${failed} FAILED` : 'ALL PASSED')
  await browser.close()
  process.exit(failed ? 1 : 0)
})().catch((e) => { console.error(e); process.exit(1) })
