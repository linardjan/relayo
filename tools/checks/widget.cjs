// Виджет на тестовом сайте: внешний вид, отправка, свёртывание, Relayo.open('tg').
const { chromium } = require('playwright-core')
const OUT = require('path').join(__dirname, 'out')

;(async () => {
  const browser = await chromium.launch({ channel: 'chrome', headless: true })
  const context = await browser.newContext({ viewport: { width: 1280, height: 800 } })
  const page = await context.newPage()
  const errors = []
  page.on('pageerror', (e) => errors.push(e.message))
  page.on('console', (m) => { if (m.type() === 'error') errors.push(m.text()) })

  await page.goto('http://localhost:5300/')
  await page.locator('.round.chat').waitFor({ timeout: 15000 })
  await page.waitForTimeout(500)
  await page.screenshot({ path: `${OUT}/w-closed.png` })

  await page.locator('.round.chat').click()
  await page.locator('.composer input').fill('здрасьте 👋')
  await page.locator('.composer input').press('Enter')
  await page.waitForTimeout(1500)
  await page.screenshot({ path: `${OUT}/w-open.png` })
  console.log('ready event fired:', await page.evaluate(() => typeof window.Relayo.open === 'function'))

  // Relayo.open('tg') открывает t.me с одноразовой ссылкой
  const [popup] = await Promise.all([
    context.waitForEvent('page', { timeout: 10000 }),
    page.evaluate(() => window.Relayo.open('tg')),
  ])
  await popup.waitForURL(/t\.me/, { timeout: 10000 }).catch(() => {})
  console.log('telegram url:', popup.url().replace(/start=.*/, 'start=…'))
  await popup.close()

  // Кнопка Telegram в колонке
  const [popup2] = await Promise.all([context.waitForEvent('page', { timeout: 10000 }), page.locator('.round.telegram').click()])
  await popup2.waitForURL(/t\.me/, { timeout: 10000 }).catch(() => {})
  console.log('telegram button url:', popup2.url().replace(/start=.*/, 'start=…'))
  await popup2.close()

  await page.locator('.round.toggle').click()
  await page.waitForTimeout(300)
  await page.screenshot({ path: `${OUT}/w-collapsed.png` })

  console.log('errors:', errors.length ? errors.join('\n') : 'none')
  await browser.close()
})().catch((e) => { console.error(e); process.exit(1) })
