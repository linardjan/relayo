// Раздел «Запросы»: поиск и фильтры, тема, статус вручную (закрытый не открывается), назначение
// (оператор — только себе), «Взять запрос». Создаёт свой тикет через виджет и временного оператора; в конце
// возвращает тронутое и удаляет оператора.
const fs = require('fs')
const env = Object.fromEntries(fs.readFileSync(require('path').join(__dirname, '../..') + '/.env', 'utf8').split('\n')
  .filter((l) => /^[A-Z_]+=/.test(l)).map((l) => { const i = l.indexOf('='); return [l.slice(0, i), l.slice(i + 1).replace(/^"|"$/g, '')] }))
const ADMIN = 'http://localhost:5100'
const PUB = 'http://localhost:5200'
const W = env.TEST_WIDGET_TOKEN_MODE_ID
const H = { origin: 'http://localhost:5300', 'content-type': 'application/json' }
const run = Date.now() % 1_000_000
let failed = 0
const check = (name, ok, extra = '') => { console.log(`${ok ? 'PASS' : 'FAIL'} ${name}${extra ? ' — ' + extra : ''}`); if (!ok) failed++ }

const login = async (user, password) => (await (await fetch(`${ADMIN}/api/auth/login`, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ login: user, password }) })).json())
const client = (token) => async (method, path, body) => {
  const r = await fetch(`${ADMIN}${path}`, { method, headers: { authorization: `Bearer ${token}`, 'content-type': 'application/json' }, body: body ? JSON.stringify(body) : undefined })
  return { status: r.status, body: await r.json().catch(() => null) }
}

;(async () => {
  const admin = await login('admin', env.BOOTSTRAP_ADMIN_PASSWORD)
  const call = client(admin.accessToken)
  const adminId = admin.user.id
  const opLogin = `req-op-${run}`
  let operatorId = null
  let taken = null
  let ticketId = null
  const search = async (q) => (await call('GET', `/api/tickets/search?${q}`)).body

  try {
    // Свой тикет: посетитель пишет в виджет
    const visitor = (await (await fetch(`${PUB}/api/widget/${W}/session`, { method: 'POST', headers: H })).json()).visitorToken
    await fetch(`${PUB}/api/widget/${W}/messages`, { method: 'POST', headers: { ...H, 'x-relayo-visitor': visitor }, body: JSON.stringify({ text: `Запрос ${run}`, clientMessageId: 'r' + run }) })
    const found = await search(`search=${encodeURIComponent(`Запрос ${run}`)}`)
    const t = found?.items?.[0]
    ticketId = t?.id
    check('search by last message', found?.total === 1 && t.status === 'New' && t.assigneeId === null, JSON.stringify(found?.total))
    check('search by number', (await search(`search=%23${t.number}`)).items.some((x) => x.id === ticketId))

    const has = async (q) => (await search(`${q}&search=${encodeURIComponent(`Запрос ${run}`)}`)).items.some((x) => x.id === ticketId)
    check('filters that match', await has('status=New&status=InProgress') && await has('assignee=none') && await has('channel=Widget')
      && await has(`createdFrom=${encodeURIComponent(new Date(Date.now() - 3600_000).toISOString())}`))
    check('filters that do not match', !(await has('status=Closed')) && !(await has('assignee=me')) && !(await has('channel=Telegram'))
      && !(await has(`createdFrom=${encodeURIComponent(new Date(Date.now() + 3600_000).toISOString())}`)))

    // Тема
    const subj = await call('PUT', `/api/tickets/${ticketId}/subject`, { subject: `  Оплата ${run} ` })
    check('subject set and trimmed', subj.status === 200 && subj.body.subject === `Оплата ${run}`)
    check('search by subject', (await search(`search=${encodeURIComponent(`Оплата ${run}`)}`)).items.some((x) => x.id === ticketId))
    check('too long subject → 400', (await call('PUT', `/api/tickets/${ticketId}/subject`, { subject: 'x'.repeat(256) })).status === 400)

    // Статус вручную
    const waiting = await call('PUT', `/api/tickets/${ticketId}/status`, { status: 'WaitingClient' })
    check('status set manually', waiting.status === 200 && waiting.body.status === 'WaitingClient')

    // Назначение: администратор — кому угодно; оператор — только себе
    const toAdmin = await call('PUT', `/api/tickets/${ticketId}/assignee`, { userId: adminId })
    check('admin assigns', toAdmin.status === 200 && toAdmin.body.assigneeId === adminId)
    const back = await call('PUT', `/api/tickets/${ticketId}/assignee`, { userId: null })
    check('returned to the queue', back.status === 200 && back.body.assigneeId === null)
    const agents = (await call('GET', '/api/tickets/agents')).body
    check('agents list for assignment', agents.some((a) => a.id === adminId && a.fullName))

    const created = await call('POST', '/api/settings/users', { login: opLogin, fullName: `Оператор ${run}`, role: 'Operator', alias: 'Анна', password: 'Operator_Passw0rd_1' })
    operatorId = created.body?.id
    const op = client((await login(opLogin, 'Operator_Passw0rd_1')).accessToken)
    const forbidden = await op('PUT', `/api/tickets/${ticketId}/assignee`, { userId: adminId })
    check('operator cannot assign to others → 403', forbidden.status === 403, String(forbidden.status))
    const self = await op('PUT', `/api/tickets/${ticketId}/assignee`, { userId: operatorId })
    check('operator assigns to self', self.status === 200 && self.body.assigneeId === operatorId, String(self.status))
    const release = await op('PUT', `/api/tickets/${ticketId}/assignee`, { userId: null })
    check('operator returns own ticket to the queue', release.status === 200 && release.body.assigneeId === null)

    // «Взять запрос»: самый старый свободный
    const next = await op('POST', '/api/tickets/take-next')
    taken = next.body?.id
    const oldestFree = (await search('status=New&status=InProgress&status=WaitingClient&assignee=none&pageSize=100')).items
    check('take-next assigns the oldest free ticket', next.status === 200 && next.body.assigneeId === operatorId
      && oldestFree.every((x) => x.createdAt >= next.body.createdAt), String(next.status))

    // Закрытие и запрет открыть снова
    const closed = await call('PUT', `/api/tickets/${ticketId}/status`, { status: 'Closed' })
    check('closed manually', closed.status === 200 && closed.body.status === 'Closed')
    const reopen = await call('PUT', `/api/tickets/${ticketId}/status`, { status: 'New' })
    check('closed ticket cannot be reopened → 409', reopen.status === 409, String(reopen.status))
    check('closed ticket cannot be assigned → 409', (await call('PUT', `/api/tickets/${ticketId}/assignee`, { userId: adminId })).status === 409)
  } finally {
    if (taken) await call('PUT', `/api/tickets/${taken}/assignee`, { userId: null })
    if (operatorId) {
      const del = await call('DELETE', `/api/settings/users/${operatorId}`)
      check('temporary operator deleted', del.status === 204, String(del.status))
    }
  }
  console.log(failed ? `${failed} FAILED` : 'ALL PASSED')
  process.exit(failed ? 1 : 0)
})()
