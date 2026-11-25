// Агенты: профиль, группы, статус, удаление, пароль в общей форме, данные из AD.
const fs = require('fs')
const { execFileSync } = require('child_process')
const env = Object.fromEntries(fs.readFileSync(require('path').join(__dirname, '../..') + '/.env', 'utf8').split('\n')
  .filter((l) => /^[A-Z_]+=/.test(l)).map((l) => { const i = l.indexOf('='); return [l.slice(0, i), l.slice(i + 1).replace(/^"|"$/g, '')] }))
const ADMIN = 'http://localhost:5100'
let failed = 0
const check = (name, ok, extra = '') => { console.log(`${ok ? 'PASS' : 'FAIL'} ${name}${extra ? ' — ' + extra : ''}`); if (!ok) failed++ }
const login = async (l, p) => { const r = await fetch(`${ADMIN}/api/auth/login`, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ login: l, password: p }) }); return { status: r.status, body: await r.json().catch(() => null) } }

;(async () => {
  const admin = (await login('admin', env.BOOTSTRAP_ADMIN_PASSWORD)).body
  const auth = { authorization: `Bearer ${admin.accessToken}`, 'content-type': 'application/json' }
  const call = async (method, path, body) => { const r = await fetch(`${ADMIN}/api/settings${path}`, { method, headers: auth, body: body ? JSON.stringify(body) : undefined }); return { status: r.status, body: await r.json().catch(() => null) } }
  const run = Date.now() % 100000

  // Группы
  const g1 = (await call('POST', '/groups', { name: `Payments ${run}` })).body
  const g2 = (await call('POST', '/groups', { name: `VIP ${run}` })).body
  check('groups created', !!g1.id && !!g2.id)
  check('duplicate group name rejected', (await call('POST', '/groups', { name: `VIP ${run}` })).status === 409)

  // Новый агент с профилем и группами
  const login1 = `agent${run}`
  let r = await call('POST', '/users', { login: login1, fullName: 'Agent One', role: 'Operator', alias: 'Kate', password: 'Agent_Passw0rd',
    profile: { email: `agent${run}@example.com`, position: 'First line', phone: '+7 900 000-00-00', locale: 'en', groupIds: [g1.id, g2.id], defaultGroupId: g2.id } })
  check('agent created with profile', r.status === 200 && r.body.email === `agent${run}@example.com` && r.body.position === 'First line', String(r.status))
  check('groups and default group saved', r.body.groupIds.length === 2 && r.body.defaultGroupId === g2.id)
  check('new agent is offline', r.body.status === 'Offline')
  const id = r.body.id
  check('bad email rejected', (await call('POST', '/users', { login: `x${run}`, fullName: 'X', role: 'Operator', password: 'Agent_Passw0rd', profile: { email: 'not-an-email' } })).status === 400)

  // Статус
  r = await call('PUT', `/users/${id}/status`, { status: 'Online' })
  check('status online', r.body.status === 'Online')

  // Изменение: пароль в той же форме, группа по умолчанию следует за составом групп
  r = await call('PUT', `/users/${id}`, { fullName: 'Agent One', alias: 'Kate', role: 'Operator', isBlocked: false, password: 'New_Passw0rd_1',
    profile: { email: `agent${run}@example.com`, position: 'Second line', phone: null, locale: 'en', groupIds: [g1.id], defaultGroupId: g2.id } })
  check('update with password', r.status === 200 && r.body.position === 'Second line')
  check('default group falls back to a member group', r.body.defaultGroupId === g1.id)
  check('new password works', (await login(login1, 'New_Passw0rd_1')).status === 200)
  check('old password rejected', (await login(login1, 'Agent_Passw0rd')).status === 401)
  check('short password rejected', (await call('PUT', `/users/${id}`, { fullName: 'Agent One', alias: 'Kate', role: 'Operator', isBlocked: false, password: 'short' })).status === 400)

  // Группы: счётчики и удаление
  let groups = (await call('GET', '/groups')).body
  check('member counts', groups.find((g) => g.id === g1.id).memberCount === 1 && groups.find((g) => g.id === g2.id).memberCount === 0)
  check('delete group', (await call('DELETE', `/groups/${g1.id}`)).status === 204)
  const users = (await call('GET', '/users')).body
  const a = users.find((u) => u.id === id)
  check('agent left deleted group', a.groupIds.length === 0 && a.defaultGroupId === null)

  // Удаление агента
  check('agent without history deleted', (await call('DELETE', `/users/${id}`)).status === 204)
  check('cannot delete yourself', (await call('DELETE', `/users/${admin.user.id}`)).status === 409)
  const withHistory = users.find((u) => u.login === 'admin')
  check('admin has history → only block', withHistory && (await call('DELETE', `/users/${withHistory.id}`)).status === 409)
  const adUser = users.find((u) => u.source === 'ActiveDirectory')
  check('AD user cannot be deleted', adUser && (await call('DELETE', `/users/${adUser.id}`)).status === 409)

  // AD: почта, телефон и должность приходят из AD
  execFileSync('docker', ['exec', '-i', 'relayo-samba-1', 'ldbmodify', '-H', '/usr/local/samba/private/sam.ldb'], {
    input: 'dn: CN=petrova,OU=Support,DC=example,DC=local\nchangetype: modify\nreplace: telephoneNumber\ntelephoneNumber: +7 495 111-22-33\n-\nreplace: title\ntitle: Senior agent\n',
  })
  const sync = (await call('POST', '/ldap/sync')).body
  const petrova = (await call('GET', '/users')).body.find((u) => u.login === 'petrova')
  check('AD sync fills email', petrova?.email === 'petrova@example.local', `${petrova?.email} | ${sync.lastSyncMessage}`)
  check('AD sync fills phone and position', petrova?.phone === '+7 495 111-22-33' && petrova?.position === 'Senior agent', `${petrova?.phone} | ${petrova?.position}`)
  // Попытка поменять почту AD-пользователю игнорируется
  await call('PUT', `/users/${petrova.id}`, { fullName: 'Hacked', alias: null, role: petrova.role, isBlocked: false, profile: { email: 'x@y.z', locale: 'en', groupIds: [] } })
  const p2 = (await call('GET', '/users')).body.find((u) => u.login === 'petrova')
  check('AD fields not editable', p2.email === 'petrova@example.local' && p2.fullName === 'Maria Petrova')

  await call('DELETE', `/groups/${g2.id}`)
  console.log(failed ? `${failed} FAILED` : 'ALL PASSED')
  process.exit(failed ? 1 : 0)
})().catch((e) => { console.error(e); process.exit(1) })
