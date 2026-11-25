// API правил: создание, проверки, история, порядок, удаление, права.
const fs = require('fs')
const env = Object.fromEntries(
  fs.readFileSync(require('path').join(__dirname, '../..') + '/.env', 'utf8').split('\n')
    .filter((l) => /^[A-Z_]+=/.test(l))
    .map((l) => { const i = l.indexOf('='); return [l.slice(0, i), l.slice(i + 1).replace(/^"|"$/g, '')] }),
)
const ADMIN = 'http://localhost:5100'
let failed = 0
const check = (name, ok, extra = '') => { console.log(`${ok ? 'PASS' : 'FAIL'} ${name}${extra ? ' — ' + extra : ''}`); if (!ok) failed++ }

const rule = (name, patch = {}) => ({
  name, description: null, isEnabled: true, isManual: false, useWordForms: false, ignoreHtml: true, stopProcessing: false,
  definition: { whenAll: [{ type: 'client_message' }], whenAny: [], whatAll: [], whatAny: [], actions: [{ type: 'send_message', value: 'Hi!' }] },
  ...patch,
})

;(async () => {
  const login = await (await fetch(`${ADMIN}/api/auth/login`, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ login: 'admin', password: env.BOOTSTRAP_ADMIN_PASSWORD }) })).json()
  const auth = { authorization: `Bearer ${login.accessToken}`, 'content-type': 'application/json' }
  const call = async (method, path, body) => {
    const r = await fetch(`${ADMIN}/api/automation/rules${path}`, { method, headers: auth, body: body ? JSON.stringify(body) : undefined })
    return { status: r.status, body: await r.json().catch(() => null) }
  }
  const sites = await (await fetch(`${ADMIN}/api/settings/sites`, { headers: auth })).json()
  const bots = await (await fetch(`${ADMIN}/api/settings/telegram-bots`, { headers: auth })).json()

  // Проверки
  check('name required', (await call('POST', '', rule(''))).status === 400)
  check('action required', (await call('POST', '', rule('x', { definition: { ...rule('x').definition, actions: [] } }))).status === 400)
  check('event required unless manual', (await call('POST', '', rule('x', { definition: { ...rule('x').definition, whenAll: [] } }))).status === 400)
  check('manual rule without event ok', (await call('POST', '', rule('Manual cleanup', { isManual: true, definition: { ...rule('x').definition, whenAll: [] } }))).status === 200)
  check('unknown event rejected', (await call('POST', '', rule('x', { definition: { ...rule('x').definition, whenAll: [{ type: 'nope' }] } }))).status === 400)
  check('timer needs minutes', (await call('POST', '', rule('x', { definition: { ...rule('x').definition, whenAll: [{ type: 'no_operator_reply' }] } }))).status === 400)
  check('bad operator rejected', (await call('POST', '', rule('x', { definition: { ...rule('x').definition, whatAll: [{ field: 'site', operator: 'contains', value: sites[0].id }] } }))).status === 400)
  check('unknown site rejected', (await call('POST', '', rule('x', { definition: { ...rule('x').definition, whatAll: [{ field: 'site', operator: 'eq', value: crypto.randomUUID() }] } }))).status === 400)
  check('bad regex rejected', (await call('POST', '', rule('x', { definition: { ...rule('x').definition, whatAll: [{ field: 'message_text', operator: 'regex', value: '([' }] } }))).status === 400)
  check('empty message rejected', (await call('POST', '', rule('x', { definition: { ...rule('x').definition, actions: [{ type: 'send_message', value: ' ' }] } }))).status === 400)

  // Полное правило
  const full = rule('Auto-reply at night', {
    description: 'Answer Telegram clients at night',
    definition: {
      whenAll: [{ type: 'out_of_hours_message' }],
      whenAny: [{ type: 'no_operator_reply', minutes: 15 }],
      whatAll: [
        { field: 'channel', operator: 'eq', value: `telegram:${bots[0].id}` },
        { field: 'site', operator: 'eq', value: sites[0].id },
        { field: 'message_text', operator: 'regex', value: '(оплат|refund)' },
      ],
      whatAny: [{ field: 'assignee', operator: 'eq', value: 'none' }, { field: 'client_verified', operator: 'eq', value: 'true' }],
      actions: [{ type: 'send_message', value: 'We will answer in the morning, {client.name}.' }, { type: 'set_status', value: 'WaitingClient' }, { type: 'unassign' }],
    },
  })
  let r = await call('POST', '', full)
  check('full rule created', r.status === 200 && r.body.definition.whatAll.length === 3, r.status + ' ' + JSON.stringify(r.body?.title ?? ''))
  const id = r.body.id
  check('created revision', r.body.revisions.length === 1 && r.body.revisions[0].summary === 'Created' && r.body.revisions[0].userName === 'Administrator')
  check('timer minutes kept', r.body.definition.whenAny[0].minutes === 15)

  r = await call('PUT', `/${id}`, { ...full, name: 'Night auto-reply', isEnabled: false, definition: { ...full.definition, actions: [full.definition.actions[0]] } })
  check('update recorded in history', r.body.revisions.length === 2 && /Name: .*Night auto-reply.*Disabled.*Do/.test(r.body.revisions[0].summary), r.body.revisions[0].summary)
  r = await call('PUT', `/${id}`, { ...full, name: 'Night auto-reply', isEnabled: false, definition: { ...full.definition, actions: [full.definition.actions[0]] } })
  check('no-op save adds no history', r.body.revisions.length === 2)

  // Порядок
  const a = (await call('POST', '', rule('Rule A'))).body.id
  const b = (await call('POST', '', rule('Rule B'))).body.id
  let list = (await call('GET', '')).body
  check('new rules go to the end', list.at(-1).id === b && list.at(-2).id === a)
  list = (await call('PUT', '/order', [b, a, id])).body
  check('reorder', list[0].id === b && list[1].id === a && list[2].id === id, list.map((x) => x.name).join(', '))

  // Удаление
  check('delete', (await call('DELETE', `/${a}`)).status === 204)
  check('deleted is gone', (await call('GET', `/${a}`)).status === 404)

  // Права
  const op = await (await fetch(`${ADMIN}/api/auth/login`, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ login: 'ivanov', password: 'Oper_Passw0rd' }) })).json()
  const forbidden = await fetch(`${ADMIN}/api/automation/rules`, { headers: { authorization: `Bearer ${op.accessToken}` } })
  check('operator cannot see rules', forbidden.status === 403)

  console.log(failed ? `${failed} FAILED` : 'ALL PASSED')
  process.exit(failed ? 1 : 0)
})().catch((e) => { console.error(e); process.exit(1) })
