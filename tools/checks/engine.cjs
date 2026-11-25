// Движок правил: события, условия, действия, лимиты, таймер, журнал.
const fs = require('fs')
const env = Object.fromEntries(fs.readFileSync(require('path').join(__dirname, '../..') + '/.env', 'utf8').split('\n')
  .filter((l) => /^[A-Z_]+=/.test(l)).map((l) => { const i = l.indexOf('='); return [l.slice(0, i), l.slice(i + 1).replace(/^"|"$/g, '')] }))
const ADMIN = 'http://localhost:5100'
const PUB = 'http://localhost:5200'
const W = env.TEST_WIDGET_TOKEN_MODE_ID
const H = { origin: 'http://localhost:5300', 'content-type': 'application/json' }
let failed = 0
const check = (name, ok, extra = '') => { console.log(`${ok ? 'PASS' : 'FAIL'} ${name}${extra ? ' — ' + extra : ''}`); if (!ok) failed++ }
const sleep = (ms) => new Promise((r) => setTimeout(r, ms))

;(async () => {
  const login = await (await fetch(`${ADMIN}/api/auth/login`, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ login: 'admin', password: env.BOOTSTRAP_ADMIN_PASSWORD }) })).json()
  const auth = { authorization: `Bearer ${login.accessToken}`, 'content-type': 'application/json' }
  const adminId = login.user.id
  const call = async (method, path, body) => {
    const r = await fetch(`${ADMIN}${path}`, { method, headers: auth, body: body ? JSON.stringify(body) : undefined })
    return { status: r.status, body: await r.json().catch(() => null) }
  }
  const rule = (name, def, patch = {}) => call('POST', '/api/automation/rules', {
    name, description: null, isEnabled: true, isManual: false, useWordForms: false, ignoreHtml: true, stopProcessing: false,
    definition: { whenAll: [], whenAny: [], whatAll: [], whatAny: [], actions: [], ...def }, ...patch,
  })
  const visitor = async () => (await (await fetch(`${PUB}/api/widget/${W}/session`, { method: 'POST', headers: H })).json()).visitorToken
  const say = (token, text) => fetch(`${PUB}/api/widget/${W}/messages`, { method: 'POST', headers: { ...H, 'x-relayo-visitor': token }, body: JSON.stringify({ text, clientMessageId: 'e' + Math.random() }) })
  const history = async (token) => (await (await fetch(`${PUB}/api/widget/${W}/messages`, { headers: { ...H, 'x-relayo-visitor': token } })).json())
  const ticketOf = async (text) => (await call('GET', '/api/tickets')).body.find((t) => t.lastMessagePreview === text)

  await call('PUT', '/api/me/alias', { alias: 'Anna' })
  for (const r of (await call('GET', '/api/automation/rules')).body) await call('DELETE', `/api/automation/rules/${r.id}`)

  // 1. Автоответ на новый тикет со словоформами и переменными
  await rule('Deposit auto-reply', {
    whenAll: [{ type: 'ticket_created' }],
    whatAll: [{ field: 'message_text', operator: 'contains', value: 'депозит' }, { field: 'channel', operator: 'eq', value: `widget:${W}` }],
    actions: [{ type: 'send_message', value: 'Ticket #{ticket.number}: we are checking your deposit.' }],
  }, { useWordForms: true })
  // 2. Назначение на администратора на любом сообщении клиента из виджета
  await rule('Assign widget chats', { whenAll: [{ type: 'client_message' }], whatAll: [{ field: 'channel_type', operator: 'eq', value: 'Widget' }, { field: 'assignee', operator: 'eq', value: 'none' }], actions: [{ type: 'assign', value: adminId }] })
  // 3. Выключенное правило не срабатывает
  await rule('Disabled rule', { whenAll: [{ type: 'client_message' }], actions: [{ type: 'send_message', value: 'SHOULD NOT APPEAR' }] }, { isEnabled: false })

  const v1 = await visitor()
  const text1 = `Где мои депозиты? ${Date.now() % 100000}`
  await say(v1, text1)
  await sleep(2500)
  let h = await history(v1)
  const auto = h.find((m) => m.text.includes('we are checking your deposit'))
  check('word forms: «депозиты» matches «депозит»', !!auto)
  const t1 = await ticketOf(text1)
  check('variables rendered', !!auto && auto.text.startsWith(`Ticket #${t1?.number}:`), auto?.text)
  check('auto-reply shown as support, no staff name', auto?.author === 'operator' && auto?.authorName === null)
  check('assign rule applied', t1?.assigneeId === adminId, t1?.assigneeName)
  check('disabled rule ignored', !h.some((m) => m.text === 'SHOULD NOT APPEAR'))
  check('preview keeps client message, not auto-reply', t1?.lastMessagePreview === text1)
  const details = (await call('GET', `/api/tickets/${t1.id}`)).body
  const autoInAdmin = details.chats.flatMap((c) => c.messages).find((m) => m.automated)
  check('admin sees auto-reply marked automated', autoInAdmin?.authorName === 'Auto-reply')

  // 4. Слово не в той форме без словоформ — не срабатывает
  const v2 = await visitor()
  await rule('Exact refund', { whenAll: [{ type: 'ticket_created' }], whatAll: [{ field: 'message_text', operator: 'contains', value: 'refund' }], actions: [{ type: 'send_message', value: 'Refund reply' }] })
  await say(v2, 'I want refunds please')
  await sleep(2000)
  check('substring match without word forms', (await history(v2)).some((m) => m.text === 'Refund reply'))

  // 5. Лимит автоответов: 3 в час на тикет
  for (const r of (await call('GET', '/api/automation/rules')).body) await call('DELETE', `/api/automation/rules/${r.id}`)
  await rule('Echo', { whenAll: [{ type: 'client_message' }], actions: [{ type: 'send_message', value: 'Echo' }] })
  const v3 = await visitor()
  for (let i = 0; i < 5; i++) { await say(v3, `spam ${i}`); await sleep(300) }
  await sleep(2500)
  const echoes = (await history(v3)).filter((m) => m.text === 'Echo').length
  check('auto-reply limit 3 per hour', echoes === 3, String(echoes))

  // 6. Защита от зацикливания и stop processing: правило меняет статус, правило на status_changed не срабатывает от него
  for (const r of (await call('GET', '/api/automation/rules')).body) await call('DELETE', `/api/automation/rules/${r.id}`)
  await rule('Set waiting', { whenAll: [{ type: 'client_message' }], actions: [{ type: 'set_status', value: 'WaitingClient' }] }, { stopProcessing: true })
  await rule('After stop', { whenAll: [{ type: 'client_message' }], actions: [{ type: 'send_message', value: 'AFTER STOP' }] })
  await rule('On status change', { whenAll: [{ type: 'status_changed' }], actions: [{ type: 'send_message', value: 'STATUS LOOP' }] })
  const v4 = await visitor()
  const text4 = `loop ${Date.now() % 100000}`
  await say(v4, text4)
  await sleep(2500)
  h = await history(v4)
  const t4 = await ticketOf(text4)
  check('set_status applied', t4?.status === 'WaitingClient', t4?.status)
  check('stop processing respected', !h.some((m) => m.text === 'AFTER STOP'))
  check('rule actions do not trigger rules', !h.some((m) => m.text === 'STATUS LOOP'))
  // ...а действие оператора (закрытие) — триггерит
  await call('POST', `/api/tickets/${t4.id}/close`)
  await sleep(2000)
  check('operator close triggers status_changed rule', (await history(v4)).some((m) => m.text === 'STATUS LOOP'))

  // 7. Ответ оператора со словом «bye» закрывает тикет
  for (const r of (await call('GET', '/api/automation/rules')).body) await call('DELETE', `/api/automation/rules/${r.id}`)
  await rule('Close on bye', { whenAll: [{ type: 'operator_replied' }], whatAll: [{ field: 'message_text', operator: 'contains', value: 'bye' }], actions: [{ type: 'close_ticket' }] })
  const v5 = await visitor()
  const text5 = `close me ${Date.now() % 100000}`
  await say(v5, text5)
  await sleep(1000)
  const t5 = await ticketOf(text5)
  const chat5 = (await call('GET', `/api/tickets/${t5.id}`)).body.chats[0].id
  await call('POST', `/api/tickets/${t5.id}/messages`, { chatId: chat5, text: 'Thanks, bye!' })
  await sleep(2000)
  check('operator_replied rule closes ticket', (await call('GET', `/api/tickets/${t5.id}`)).body.ticket.status === 'Closed')

  // 8. Таймер: оператор не ответил 1 минуту
  for (const r of (await call('GET', '/api/automation/rules')).body) await call('DELETE', `/api/automation/rules/${r.id}`)
  await rule('No reply 1 min', { whenAll: [{ type: 'no_operator_reply', minutes: 1 }], actions: [{ type: 'send_message', value: 'Sorry for the wait' }] })
  const v6 = await visitor()
  await say(v6, 'timer first')
  await sleep(2000)
  await say(v6, 'timer second')
  const v7 = await visitor()
  const text7 = `answered ${Date.now() % 100000}`
  await say(v7, text7)
  await sleep(1500)
  const t7 = await ticketOf(text7)
  const chat7 = (await call('GET', `/api/tickets/${t7.id}`)).body.chats[0].id
  await call('POST', `/api/tickets/${t7.id}/messages`, { chatId: chat7, text: 'Here I am' })
  console.log('… waiting 75 s for the timer')
  await sleep(75_000)
  const waits = (await history(v6)).filter((m) => m.text === 'Sorry for the wait').length
  check('timer fires once for two unanswered messages', waits === 1, String(waits))
  check('timer skipped when operator answered', !(await history(v7)).some((m) => m.text === 'Sorry for the wait'))

  // 9. Журнал
  const log = (await call('GET', '/api/automation/rules/log?days=1')).body
  check('execution log written', log.some((e) => e.ruleName === 'Deposit auto-reply' && e.applied) && log.some((e) => e.trigger.startsWith('no_operator_reply')))
  check('limit recorded in log', log.some((e) => e.ruleName === 'Echo' && !e.applied && /limit/.test(e.details)))

  for (const r of (await call('GET', '/api/automation/rules')).body) await call('DELETE', `/api/automation/rules/${r.id}`)
  console.log(failed ? `${failed} FAILED` : 'ALL PASSED')
  process.exit(failed ? 1 : 0)
})().catch((e) => { console.error(e); process.exit(1) })
