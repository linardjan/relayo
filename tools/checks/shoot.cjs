// Скриншоты админки в системном Chrome: вход, затем обход страниц.
const fs = require('fs')
const { chromium } = require('playwright-core')

const env = Object.fromEntries(
  fs.readFileSync(require('path').join(__dirname, '../..') + '/.env', 'utf8').split('\n')
    .filter((l) => /^[A-Z_]+=/.test(l))
    .map((l) => { const i = l.indexOf('='); return [l.slice(0, i), l.slice(i + 1).replace(/^"|"$/g, '')] }),
)
const BASE = 'http://localhost:5173'
const OUT = require('path').join(__dirname, 'out')
const pages = process.argv.slice(2)

;(async () => {
  const browser = await chromium.launch({ channel: 'chrome', headless: true })
  const page = await browser.newPage({ viewport: { width: 1440, height: 900 } })
  const errors = []
  page.on('pageerror', (e) => errors.push(e.message))
  page.on('console', (m) => { if (m.type() === 'error' || m.type() === 'warning') errors.push(`${m.type()}: ${m.text()}`) })

  await page.goto(`${BASE}/login`)
  await page.getByLabel(/login/i).first().fill('admin')
  await page.getByLabel(/password/i).fill(env.BOOTSTRAP_ADMIN_PASSWORD)
  await page.getByRole('button', { name: /sign in/i }).click()
  await page.waitForURL((u) => !u.pathname.startsWith('/login'))

  for (const spec of pages) {
    const [path, name, action] = spec.split('|')
    await page.goto(BASE + path)
    await page.waitForLoadState('networkidle')
    if (action === 'collapse') await page.getByLabel('Collapse or expand the menu').click()
    await page.waitForTimeout(400)
    await page.screenshot({ path: `${OUT}/${name}.png`, fullPage: true })
    console.log('shot', name, page.url())
    if (action === 'collapse') await page.getByLabel('Collapse or expand the menu').click()
  }
  if (errors.length) console.log('CONSOLE:\n' + [...new Set(errors)].join('\n'))
  await browser.close()
})().catch((e) => { console.error(e); process.exit(1) })
