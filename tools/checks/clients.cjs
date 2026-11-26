// Клиенты: создание вручную, карточка (имя, язык, должность, заметка, VIP, спамер), аватар в S3, поиск, удаление.
// Создаёт клиентов с уникальным clientId и в конце удаляет их.
const fs = require('fs')
const path = require('path')
const { execFileSync } = require('child_process')
const env = Object.fromEntries(fs.readFileSync(path.join(__dirname, '../..') + '/.env', 'utf8').split('\n')
  .filter((l) => /^[A-Z_]+=/.test(l)).map((l) => { const i = l.indexOf('='); return [l.slice(0, i), l.slice(i + 1).replace(/^"|"$/g, '')] }))
const ADMIN = 'http://localhost:5100'
const run = Date.now() % 1_000_000
let failed = 0
const check = (name, ok, extra = '') => { console.log(`${ok ? 'PASS' : 'FAIL'} ${name}${extra ? ' — ' + extra : ''}`); if (!ok) failed++ }
const db = (sql) => execFileSync('node', [path.join(__dirname, 'db.cjs'), sql], { encoding: 'utf8' }).trim()
// Картинка 1×1: PNG и GIF.
const png = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==', 'base64')
const gif = Buffer.from('R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7', 'base64')

;(async () => {
  const login = await (await fetch(`${ADMIN}/api/auth/login`, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ login: 'admin', password: env.BOOTSTRAP_ADMIN_PASSWORD }) })).json()
  const token = login.accessToken
  const call = async (method, p, body) => {
    const r = await fetch(`${ADMIN}/api/clients${p}`, { method, headers: { authorization: `Bearer ${token}`, 'content-type': 'application/json' }, body: body ? JSON.stringify(body) : undefined })
    return { status: r.status, body: await r.json().catch(() => null) }
  }
  const upload = async (id, bytes, type, name) => {
    const form = new FormData()
    form.append('file', new Blob([bytes], { type }), name)
    const r = await fetch(`${ADMIN}/api/clients/${id}/avatar`, { method: 'POST', headers: { authorization: `Bearer ${token}` }, body: form })
    return { status: r.status, body: await r.json().catch(() => null) }
  }
  const profile = { name: `Анна ${run}`, locale: 'ru', position: 'Директор', note: 'Звонить после 18:00', isVip: true, isSpammer: false }
  const created = []

  try {
    const sites = (await call('GET', '/sites')).body
    check('sites for staff', Array.isArray(sites) && sites.length > 0 && !('keys' in sites[0]), JSON.stringify(sites?.[0]))
    const siteId = sites[0].id
    const clientId = `chk-${run}`

    const c = await call('POST', '/', { siteId, externalClientId: clientId, profile })
    const id = c.body?.client?.id
    if (id) created.push(id)
    const v = c.body?.client ?? {}
    check('client created', c.status === 200 && v.externalClientId === clientId && v.displayName === profile.name && v.locale === 'ru'
      && v.position === 'Директор' && v.note === profile.note && v.isVip === true && v.isSpammer === false && v.isVerified === false,
      `${c.status} ${JSON.stringify(c.body)}`)
    check('same clientId on the site → 409', (await call('POST', '/', { siteId, externalClientId: clientId, profile })).status === 409)
    check('bad language → 400', (await call('PUT', `/${id}`, { ...profile, locale: 'russian' })).status === 400)

    const updated = await call('PUT', `/${id}`, { name: '', locale: 'EN', position: null, note: ' ', isVip: false, isSpammer: true })
    const u = updated.body?.client ?? {}
    check('profile updated, empty values cleared', updated.status === 200 && u.displayName === null && u.locale === 'en' && u.position === null
      && u.note === null && u.isVip === false && u.isSpammer === true, JSON.stringify(u))
    await call('PUT', `/${id}`, profile)

    // Аватар: закрытый бакет, временная ссылка; при замене старый файл удаляется.
    const a1 = await upload(id, png, 'image/png', 'a.png')
    const url1 = a1.body?.client?.avatarUrl
    const got1 = url1 ? await fetch(url1) : null
    check('avatar uploaded and served by a temporary link', a1.status === 200 && got1?.status === 200 && got1.headers.get('content-type') === 'image/png'
      && /X-Amz-Signature=/.test(url1), `${a1.status} ${got1?.status}`)
    const a2 = await upload(id, gif, 'image/gif', 'b.gif')
    const url2 = a2.body?.client?.avatarUrl
    check('avatar replaced, old file deleted', a2.status === 200 && url2 && url2 !== url1 && (await fetch(url1)).status === 404, `${a2.status}`)
    const bad = await upload(id, Buffer.from('not an image'), 'text/plain', 'x.txt')
    check('not an image → 400', bad.status === 400, String(bad.status))
    const removed = await call('DELETE', `/${id}/avatar`)
    check('avatar removed', removed.status === 200 && removed.body.client.avatarUrl === null && (await fetch(url2)).status === 404)

    const found = await call('GET', `/?search=${encodeURIComponent(`анна ${run}`)}&page=1&pageSize=20`)
    check('search by name (any case)', found.body?.items?.some((x) => x.id === id && x.isVip && x.note === profile.note), `${found.body?.total}`)

    const anonymous = await call('POST', '/', { siteId, externalClientId: null, profile: { ...profile, name: `Без clientId ${run}` } })
    if (anonymous.body?.client?.id) created.push(anonymous.body.client.id)
    check('client without clientId', anonymous.status === 200 && anonymous.body.client.externalClientId === null)

    const withTickets = db('SELECT TOP 1 client_id FROM tickets')
    if (withTickets) {
      const r = await call('DELETE', `/${withTickets}`)
      check('client with tickets cannot be deleted → 409', r.status === 409, String(r.status))
    }
  } finally {
    for (const id of created) {
      const r = await call('DELETE', `/${id}`)
      check(`client deleted (${id.slice(-6)})`, r.status === 204 && (await call('GET', `/${id}`)).status === 404, String(r.status))
    }
  }
  console.log(failed ? `${failed} FAILED` : 'ALL PASSED')
  process.exit(failed ? 1 : 0)
})()
