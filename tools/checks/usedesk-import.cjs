// Импорт из Usedesk: подключение к тестовой MySQL (docker compose --profile usedesk up -d usedesk-mysql),
// предпросмотр, перенос сотрудников, почты, Telegram, WhatsApp и виджетов. В конце удаляет созданное и подключение.
const fs = require('fs')
const path = require('path')
const { execFileSync } = require('child_process')
const env = Object.fromEntries(fs.readFileSync(path.join(__dirname, '../..') + '/.env', 'utf8').split('\n')
  .filter((l) => /^[A-Z_]+=/.test(l)).map((l) => { const i = l.indexOf('='); return [l.slice(0, i), l.slice(i + 1).replace(/^"|"$/g, '')] }))
const ADMIN = 'http://localhost:5100'
let failed = 0
const check = (name, ok, extra = '') => { console.log(`${ok ? 'PASS' : 'FAIL'} ${name}${extra ? ' — ' + extra : ''}`); if (!ok) failed++ }
const db = (sql) => execFileSync('node', [path.join(__dirname, 'db.cjs'), sql], { encoding: 'utf8' }).trim()

;(async () => {
  const login = await (await fetch(`${ADMIN}/api/auth/login`, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ login: 'admin', password: env.BOOTSTRAP_ADMIN_PASSWORD }) })).json()
  const auth = { authorization: `Bearer ${login.accessToken}`, 'content-type': 'application/json' }
  const call = async (method, p, body) => { const r = await fetch(`${ADMIN}/api/settings${p}`, { method, headers: auth, body: body ? JSON.stringify(body) : undefined }); return { status: r.status, body: await r.json().catch(() => null) } }
  const connection = { host: 'localhost', port: Number(env.TEST_USEDESK_MYSQL_PORT), database: 'usedesk', username: 'root', companyId: 7 }
  const siteId = (await call('GET', '/sites')).body[0].id
  const preview = async () => (await call('GET', '/usedesk/preview')).body
  const links = { users: '/users', email: '/email-channels', telegram: '/telegram-bots', whatsApp: '/whatsapp-channels', widgets: '/widgets' }
  // Убрать перенесённое (и оставшееся от прерванного прогона): виджеты — раньше ботов и номеров.
  const cleanup = async () => {
    const p = await preview()
    if (!p?.users) return
    for (const key of ['widgets', 'email', 'telegram', 'whatsApp', 'users'])
      for (const row of p[key].filter((r) => r.importedId)) await call('DELETE', `${links[key]}/${row.importedId}`)
    // Пользователей AD API не удаляет (их убирает синхронизация) — тестовых убираем из БД. Id — из tools/usedesk-mock.
    db("DELETE FROM users WHERE usedesk_id IN (101, 102, 103) AND source = 'ActiveDirectory'")
  }

  try {
    const bad = await call('PUT', '/usedesk/connection', { ...connection, password: 'wrong-password' })
    check('wrong MySQL password: saved, check failed', bad.status === 200 && bad.body.lastCheckOk === false && /Access denied/i.test(bad.body.lastCheckMessage), bad.body?.lastCheckMessage ?? bad.status)
    const ok = await call('PUT', '/usedesk/connection', { ...connection, password: env.TEST_USEDESK_MYSQL_PASSWORD })
    check('connected, company 7 counted', ok.body?.lastCheckOk === true && /users 5, channels 6, widgets 1/.test(ok.body.lastCheckMessage), ok.body?.lastCheckMessage)
    const got = await call('GET', '/usedesk/connection')
    check('password is not returned', got.body.hasPassword === true && !JSON.stringify(got.body).includes(env.TEST_USEDESK_MYSQL_PASSWORD))
    check('password is encrypted in the DB', !db('SELECT protected_password FROM usedesk_connection').includes(env.TEST_USEDESK_MYSQL_PASSWORD))
    const wrongCompany = await call('PUT', '/usedesk/connection', { ...connection, companyId: 999 })
    check('empty company: check fails', wrongCompany.body?.lastCheckOk === false && /no users and channels/.test(wrongCompany.body.lastCheckMessage), wrongCompany.body?.lastCheckMessage)
    await call('PUT', '/usedesk/connection', connection)

    await cleanup()
    let p = await preview()
    check('preview: only our company', p.users.length === 5 && !p.users.some((u) => u.id === 201) && !p.email.some((c) => c.id === 20), `${p.users.length} users`)
    check('preview: support and bot accounts are not importable', p.users.filter((u) => u.problem).map((u) => u.id).join() === '104,105')
    check('preview: internal mailbox has a problem', /not supported/.test(p.email.find((c) => c.id === 15)?.problem ?? ''))
    check('preview: deleted Pact connection hidden', p.whatsApp.length === 1 && p.whatsApp[0].id === 1)
    check('preview: no token in the response', !JSON.stringify(p).includes('AAAAAAAAAAAAAAAAAAAA') && !JSON.stringify(p).includes('fake-imap-password'))
    check('preview: widget notes', p.widgets[0]?.notes.some((n) => n.includes('VIBER')) && p.widgets[0].notes.some((n) => n.includes('@relayo_fake_bot')), p.widgets[0]?.notes.join(' | '))

    // Сотрудники
    const users = await call('POST', '/usedesk/import', { kind: 'Users', ids: [101, 102, 103, 104] })
    check('users imported, support skipped', users.body?.imported === 3 && users.body.skipped.length === 1 && users.body.skipped[0].id === 104, JSON.stringify(users.body))
    const anna = db("SELECT source, ad_object_guid, alias, is_blocked, login FROM users WHERE usedesk_id = 101")
    check('AD user created by objectGUID with the Usedesk name as alias', /^ActiveDirectory\|3f2504e0-4f89-11d3-9a0c-0305e82c3301\|Анна\|(f|false|0)\|anna\.ivanova@example\.test$/.test(anna), anna)
    const boris = db("SELECT source, is_blocked, password_hash FROM users WHERE usedesk_id = 102")
    check('removed user imported blocked, without a password', /^Local\|(t|true|1)\|$/.test(boris), boris)
    const again = await call('POST', '/usedesk/import', { kind: 'Users', ids: [101] })
    check('second import does not duplicate', again.body?.imported === 0 && /Already imported/.test(again.body.skipped[0]?.reason), JSON.stringify(again.body))

    // Каналы
    const noSite = await call('POST', '/usedesk/import', { kind: 'Email', ids: [10] })
    check('channels need a site', noSite.status === 400, String(noSite.status))
    const mail = await call('POST', '/usedesk/import', { kind: 'Email', ids: [10, 15], siteId })
    check('mailbox imported, internal one skipped', mail.body?.imported === 1 && mail.body.skipped[0]?.id === 15, JSON.stringify(mail.body))
    const mailRow = db("SELECT imap_email, imap_host, imap_port, imap_security, smtp_port, smtp_security, quoting, imap_protected_password FROM email_channels WHERE usedesk_channel_id = 10")
    check('mailbox settings mapped', mailRow.startsWith('support@example.test|imap.example.test|993|Ssl|587|StartTls|SendToClient|') && !mailRow.includes('fake-imap-password'), mailRow)

    const tg = await call('POST', '/usedesk/import', { kind: 'Telegram', ids: [1], siteId })
    // Токен фейковый: Telegram его отклоняет (или недоступен) — бот не создаётся, вебхук не трогается.
    check('fake bot token is rejected', tg.body?.imported === 0 && /Telegram/.test(tg.body.skipped[0]?.reason ?? ''), JSON.stringify(tg.body))

    const wa = await call('POST', '/usedesk/import', { kind: 'WhatsApp', ids: [1], siteId })
    const waRow = db("SELECT mode, phone_number, is_connected FROM whatsapp_channels WHERE usedesk_channel_id = 13")
    check('WhatsApp number created for a QR connection', wa.body?.imported === 1 && /^Gateway\|\|(f|false|0)$/.test(waRow), `${JSON.stringify(wa.body)} ${waRow}`)

    const widgets = await call('POST', '/usedesk/import', { kind: 'Widgets', ids: [1], siteId, identificationMode: 'Direct' })
    check('widget imported', widgets.body?.imported === 1, JSON.stringify(widgets.body))
    p = await preview()
    const widget = (await call('GET', '/widgets')).body.find((w) => w.widget.id === p.widgets[0].importedId)?.widget
    check('widget settings mapped', widget && widget.position === 'Left' && widget.defaultLocale === 'ru' && widget.identificationMode === 'Direct'
      && widget.title === 'Напишите нам' && widget.options.tooltipText === 'Нужна помощь?' && widget.publishedVersion === null
      && widget.options.items.map((i) => i.type).join() === 'Chat,Link', JSON.stringify(widget && { ...widget, options: { items: widget.options.items, tooltipText: widget.options.tooltipText } }))
    check('preview shows what is imported', p.users.filter((u) => u.importedId).length === 3 && p.email.find((c) => c.id === 10)?.importedId
      && p.whatsApp[0].importedId && p.widgets[0].importedId && !p.telegram[0].importedId)
  } finally {
    await cleanup()
    const forget = await call('DELETE', '/usedesk/connection')
    check('connection forgotten', forget.status === 204 && db('SELECT COUNT(*) FROM usedesk_connection') === '0')
    const left = db('SELECT (SELECT COUNT(*) FROM users WHERE usedesk_id IS NOT NULL) + (SELECT COUNT(*) FROM widgets WHERE usedesk_channel_id IS NOT NULL) + (SELECT COUNT(*) FROM email_channels WHERE usedesk_channel_id IS NOT NULL) + (SELECT COUNT(*) FROM whatsapp_channels WHERE usedesk_channel_id IS NOT NULL)')
    check('imported data cleaned up', left === '0', left)
  }
  console.log(failed ? `${failed} FAILED` : 'ALL PASSED')
  process.exit(failed ? 1 : 0)
})()
