// Внутренние заметки, страница сайта у чата, группа тикета и поля переноса из Usedesk в API тикета.
const fs = require('fs')
const env = Object.fromEntries(fs.readFileSync(require('path').join(__dirname, '../..') + '/.env', 'utf8').split('\n')
  .filter((l) => /^[A-Z_]+=/.test(l)).map((l) => { const i = l.indexOf('='); return [l.slice(0, i), l.slice(i + 1).replace(/^"|"$/g, '')] }))
const ADMIN = 'http://localhost:5100'
const PUB = 'http://localhost:5200'
const W = env.TEST_WIDGET_TOKEN_MODE_ID
const H = { origin: 'http://localhost:5300', 'content-type': 'application/json' }
let failed = 0
const check = (name, ok, extra = '') => { console.log(`${ok ? 'PASS' : 'FAIL'} ${name}${extra ? ' — ' + extra : ''}`); if (!ok) failed++ }

;(async () => {
  const login = await (await fetch(`${ADMIN}/api/auth/login`, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ login: 'admin', password: env.BOOTSTRAP_ADMIN_PASSWORD }) })).json()
  const auth = { authorization: `Bearer ${login.accessToken}`, 'content-type': 'application/json' }
  const call = async (method, path, body) => {
    const r = await fetch(`${ADMIN}${path}`, { method, headers: auth, body: body ? JSON.stringify(body) : undefined })
    return { status: r.status, body: await r.json().catch(() => null) }
  }
  const visitor = async () => (await (await fetch(`${PUB}/api/widget/${W}/session`, { method: 'POST', headers: H })).json()).visitorToken
  const say = (token, text, pageUrl) => fetch(`${PUB}/api/widget/${W}/messages`, { method: 'POST', headers: { ...H, 'x-relayo-visitor': token }, body: JSON.stringify({ text, clientMessageId: 'n' + Math.random(), pageUrl }) })
  const history = async (token) => (await (await fetch(`${PUB}/api/widget/${W}/messages`, { headers: { ...H, 'x-relayo-visitor': token } })).json())
  const ticketOf = async (text) => (await call('GET', '/api/tickets')).body.find((t) => t.lastMessagePreview === text)
  const run = Date.now() % 100000

  // Страница сайта: запоминается первая, якорь отбрасывается
  const v = await visitor()
  const text = `Notes check ${run}`
  await say(v, text, 'http://localhost:5300/catalog/item?id=7#reviews')
  await say(v, `${text} again`, 'http://localhost:5300/other')
  let ticket = (await call('GET', '/api/tickets')).body.find((t) => t.lastMessagePreview === `${text} again`)
  let details = (await call('GET', `/api/tickets/${ticket.id}`)).body
  check('page url of the first message, without fragment', details.chats[0].pageUrl === 'http://localhost:5300/catalog/item?id=7', details.chats[0].pageUrl)
  check('new ticket is from Relayo, no subject or group', details.ticket.source === 'Relayo' && details.ticket.subject === null
    && details.ticket.sourceChannel === null && details.ticket.groupId === null)

  const v2 = await visitor()
  await say(v2, `Bad page ${run}`, 'javascript:alert(1)')
  const bad = await ticketOf(`Bad page ${run}`)
  check('non-http page url ignored', (await call('GET', `/api/tickets/${bad.id}`)).body.chats[0].pageUrl === null)

  // Заметка: видят сотрудники, не видит клиент, тикет не меняется
  await call('PUT', '/api/me/alias', { alias: 'Anna' })
  const before = (await call('GET', `/api/tickets/${ticket.id}`)).body.ticket
  const noteText = `Internal note ${run}`
  const note = await call('POST', `/api/tickets/${ticket.id}/notes`, { chatId: details.chats[0].id, text: noteText })
  check('note created', note.status === 200 && note.body.visibility === 'Internal' && note.body.authorName, `${note.status}`)
  details = (await call('GET', `/api/tickets/${ticket.id}`)).body
  check('staff see the note', details.chats[0].messages.some((m) => m.text === noteText && m.visibility === 'Internal'))
  check('client messages are public', details.chats[0].messages.filter((m) => m.author === 'Client').every((m) => m.visibility === 'Public'))
  check('note does not change status or preview', details.ticket.status === before.status && details.ticket.lastMessagePreview === before.lastMessagePreview,
    `${details.ticket.status} | ${details.ticket.lastMessagePreview}`)
  check('widget history hides the note', !(await history(v)).some((m) => m.text === noteText))
  check('empty note rejected', (await call('POST', `/api/tickets/${ticket.id}/notes`, { chatId: details.chats[0].id, text: '  ' })).status === 400)
  check('note to a chat of another ticket rejected', (await call('POST', `/api/tickets/${bad.id}/notes`, { chatId: details.chats[0].id, text: 'x' })).status === 404)

  // Ответ после заметки по-прежнему уходит клиенту
  const reply = `Public reply ${run}`
  await call('POST', `/api/tickets/${ticket.id}/messages`, { chatId: details.chats[0].id, text: reply })
  check('reply after note reaches the widget', (await history(v)).some((m) => m.text === reply))

  // Группа тикета
  const group = (await call('POST', '/api/settings/groups', { name: `Queue ${run}` })).body
  check('groups listed for ticket form', (await call('GET', '/api/tickets/groups')).body.some((g) => g.id === group.id && g.name === `Queue ${run}`))
  let r = await call('PUT', `/api/tickets/${ticket.id}/group`, { groupId: group.id })
  check('group set', r.status === 200 && r.body.groupId === group.id && r.body.groupName === `Queue ${run}`)
  check('unknown group rejected', (await call('PUT', `/api/tickets/${ticket.id}/group`, { groupId: '00000000-0000-0000-0000-000000000001' })).status === 404)
  r = await call('PUT', `/api/tickets/${ticket.id}/group`, { groupId: null })
  check('group cleared', r.status === 200 && r.body.groupId === null)
  await call('PUT', `/api/tickets/${ticket.id}/group`, { groupId: group.id })
  await call('DELETE', `/api/settings/groups/${group.id}`)
  check('deleted group leaves the ticket', (await call('GET', `/api/tickets/${ticket.id}`)).body.ticket.groupId === null)

  // Уборка: тикеты проверки закрываются
  await call('POST', `/api/tickets/${ticket.id}/close`)
  await call('POST', `/api/tickets/${bad.id}/close`)
  console.log(failed ? `${failed} FAILED` : 'ALL PASSED')
  process.exit(failed ? 1 : 0)
})().catch((e) => { console.error(e); process.exit(1) })
