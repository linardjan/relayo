// Проверка LDAP на тестовом Samba AD: настройки, проверка связи, импорт, вход по AD, блокировка.
const fs = require('fs')
const { execFileSync } = require('child_process')
const env = Object.fromEntries(
  fs.readFileSync(require('path').join(__dirname, '../..') + '/.env', 'utf8').split('\n')
    .filter((l) => /^[A-Z_]+=/.test(l))
    .map((l) => { const i = l.indexOf('='); return [l.slice(0, i), l.slice(i + 1).replace(/^"|"$/g, '')] }),
)
const ADMIN = 'http://localhost:5100'
let failed = 0
const check = (name, ok, extra = '') => { console.log(`${ok ? 'PASS' : 'FAIL'} ${name}${extra ? ' — ' + extra : ''}`); if (!ok) failed++ }
const samba = (...args) => execFileSync('docker', ['exec', 'relayo-samba-1', 'samba-tool', ...args], { encoding: 'utf8' })

const base = {
  isEnabled: true, host: 'localhost', port: 636, security: 'Ldaps', verifyCertificate: false, provider: 'ActiveDirectory',
  bindDn: 'CN=relayo-sync,OU=Service Accounts,DC=example,DC=local', bindPassword: 'Oper_Passw0rd',
  usersDn: 'OU=Support,DC=example,DC=local', guidAttribute: 'objectGUID', loginAttribute: 'cn',
  fullNameAttribute: 'displayName', defaultRole: 'Operator', syncIntervalMinutes: 0,
}

async function login(user, password) {
  const r = await fetch(`${ADMIN}/api/auth/login`, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ login: user, password }) })
  return { status: r.status, body: await r.json().catch(() => null) }
}

;(async () => {
  const { body: { accessToken } } = await login('admin', env.BOOTSTRAP_ADMIN_PASSWORD)
  const auth = { authorization: `Bearer ${accessToken}`, 'content-type': 'application/json' }
  const call = async (method, path, body) => {
    const r = await fetch(`${ADMIN}/api/settings${path}`, { method, headers: auth, body: body ? JSON.stringify(body) : undefined })
    return { status: r.status, body: await r.json().catch(() => null) }
  }

  // Проверка подключения: ошибки понятны администратору
  let r = await call('PUT', '/ldap', { ...base, bindPassword: 'wrong-password' })
  check('wrong service password is reported', r.status === 200 && r.body.lastCheckOk === false && /password/i.test(r.body.lastCheckMessage), r.body?.lastCheckMessage)
  r = await call('PUT', '/ldap', { ...base, verifyCertificate: true })
  check('untrusted certificate is reported', r.body.lastCheckOk === false && /certificate/i.test(r.body.lastCheckMessage), r.body.lastCheckMessage)
  r = await call('PUT', '/ldap', { ...base, port: 6399 })
  check('wrong port is reported', r.body.lastCheckOk === false && /connect|answer/i.test(r.body.lastCheckMessage), r.body.lastCheckMessage)
  r = await call('PUT', '/ldap', { ...base, usersDn: 'OU=Nope,DC=example,DC=local' })
  check('missing folder is reported', r.body.lastCheckOk === false && /not found/i.test(r.body.lastCheckMessage), r.body.lastCheckMessage)
  r = await call('PUT', '/ldap', { ...base, host: 'ldap://localhost' })
  check('host with scheme rejected', r.status === 400)

  r = await call('PUT', '/ldap', base)
  check('valid settings connect', r.body.lastCheckOk === true && /Users in the folder: 3/.test(r.body.lastCheckMessage), r.body.lastCheckMessage)
  check('password not returned', !JSON.stringify(r.body).includes('Oper_Passw0rd') && r.body.hasBindPassword === true)
  r = await call('PUT', '/ldap', { ...base, bindPassword: '' })
  check('empty password keeps the saved one', r.body.lastCheckOk === true, r.body.lastCheckMessage)

  // Импорт
  r = await call('POST', '/ldap/sync')
  check('import ok', r.body.lastSyncOk === true, r.body.lastSyncMessage)
  const users = (await call('GET', '/users')).body
  const ivanov = users.find((u) => u.login === 'ivanov')
  check('Support users imported with full names', ['ivanov', 'petrova', 'sidorov'].every((l) => users.some((u) => u.login === l && u.source === 'ActiveDirectory')) && ivanov?.fullName === 'Ivan Ivanov', ivanov?.fullName)
  check('Sales user not imported', !users.some((u) => u.login === 'smirnov'))
  check('service account not imported', !users.some((u) => u.login === 'relayo-sync'))
  check('default role applied', ivanov?.role === 'Operator')
  r = await call('POST', '/ldap/sync')
  check('second import changes nothing', /Created: 0, updated: 0, blocked: 0/.test(r.body.lastSyncMessage), r.body.lastSyncMessage)

  // Вход через AD
  check('AD login with right password', (await login('ivanov', 'Oper_Passw0rd')).status === 200)
  check('AD login is case-insensitive', (await login('IVANOV', 'Oper_Passw0rd')).status === 200)
  check('AD login with wrong password', (await login('ivanov', 'nope')).status === 401)
  check('not imported AD user cannot sign in', (await login('smirnov', 'Oper_Passw0rd')).status === 401)
  check('DN injection in login rejected', (await login('ivanov,OU=Support', 'Oper_Passw0rd')).status === 401)

  // Отключённый в AD пользователь блокируется при синхронизации и разблокируется после включения
  samba('user', 'disable', 'sidorov')
  r = await call('POST', '/ldap/sync')
  let sid = (await call('GET', '/users')).body.find((u) => u.login === 'sidorov')
  check('disabled in AD → blocked', sid.isBlocked && sid.blockedByDirectory, r.body.lastSyncMessage)
  check('blocked AD user cannot sign in', (await login('sidorov', 'Oper_Passw0rd')).status === 401)
  samba('user', 'enable', 'sidorov')
  r = await call('POST', '/ldap/sync')
  sid = (await call('GET', '/users')).body.find((u) => u.login === 'sidorov')
  check('enabled again → unblocked', !sid.isBlocked, r.body.lastSyncMessage)

  // Блокировку администратором синхронизация не снимает
  const pet = (await call('GET', '/users')).body.find((u) => u.login === 'petrova')
  await call('PUT', `/users/${pet.id}`, { fullName: pet.fullName, alias: null, role: pet.role, isBlocked: true })
  await call('POST', '/ldap/sync')
  check('manual block survives sync', (await call('GET', '/users')).body.find((u) => u.login === 'petrova').isBlocked)
  await call('PUT', `/users/${pet.id}`, { fullName: pet.fullName, alias: null, role: pet.role, isBlocked: false })

  // Выключенный AD: вход по AD недоступен
  await call('PUT', '/ldap', { ...base, isEnabled: false, bindPassword: '' })
  check('AD sign-in off when disabled', (await login('ivanov', 'Oper_Passw0rd')).status === 401)
  await call('PUT', '/ldap', { ...base, bindPassword: '' })

  // Права: оператор не видит настройки
  const op = await login('ivanov', 'Oper_Passw0rd')
  const forbidden = await fetch(`${ADMIN}/api/settings/ldap`, { headers: { authorization: `Bearer ${op.body.accessToken}` } })
  check('operator cannot read LDAP settings', forbidden.status === 403)

  const counts = await call('GET', '/channels/counts')
  check('channel counts endpoint', counts.status === 200 && typeof counts.body.widgets === 'object', JSON.stringify(counts.body))

  console.log(failed ? `${failed} FAILED` : 'ALL PASSED')
  process.exit(failed ? 1 : 0)
})().catch((e) => { console.error(e); process.exit(1) })
