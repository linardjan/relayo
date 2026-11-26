// Почтовый канал: API настроек, шифрование паролей, проверка подключения.
// Поднимает поддельные IMAP и SMTP серверы без TLS на 127.0.0.1 (Internal работает на этой же машине),
// в конце удаляет созданный канал.
const fs = require('fs')
const net = require('net')
const { execFileSync } = require('child_process')
const path = require('path')
const env = Object.fromEntries(fs.readFileSync(path.join(__dirname, '../..') + '/.env', 'utf8').split('\n')
  .filter((l) => /^[A-Z_]+=/.test(l)).map((l) => { const i = l.indexOf('='); return [l.slice(0, i), l.slice(i + 1).replace(/^"|"$/g, '')] }))
const ADMIN = 'http://localhost:5100'
const run = Date.now() % 1_000_000
const EMAIL = `support-${run}@example.test`
// Явно фейковый пароль: только для поддельных серверов этой проверки.
const PASSWORD = `fake-mail-password-${run}`
let failed = 0
const check = (name, ok, extra = '') => { console.log(`${ok ? 'PASS' : 'FAIL'} ${name}${extra ? ' — ' + extra : ''}`); if (!ok) failed++ }

/** IMAP: ровно столько, сколько нужно MailKit для входа по LOGIN и EXAMINE INBOX. */
function fakeImap() {
  return net.createServer((socket) => {
    let buffer = ''
    const send = (s) => socket.write(s + '\r\n')
    send('* OK [CAPABILITY IMAP4rev1] fake IMAP ready')
    socket.on('data', (chunk) => {
      buffer += chunk.toString('utf8')
      let i
      while ((i = buffer.indexOf('\r\n')) >= 0) {
        const line = buffer.slice(0, i)
        buffer = buffer.slice(i + 2)
        const [tag, command = ''] = line.split(' ')
        switch (command.toUpperCase()) {
          case 'CAPABILITY': send('* CAPABILITY IMAP4rev1'); send(`${tag} OK done`); break
          case 'LOGIN': send(line.includes(PASSWORD) ? `${tag} OK [CAPABILITY IMAP4rev1] logged in` : `${tag} NO [AUTHENTICATIONFAILED] invalid credentials`); break
          case 'LIST': send('* LIST (\\Noselect) "/" ""'); send('* LIST (\\HasNoChildren) "/" INBOX'); send(`${tag} OK done`); break
          case 'EXAMINE': case 'SELECT':
            send('* FLAGS (\\Seen)'); send('* 3 EXISTS'); send('* 0 RECENT'); send('* OK [UIDVALIDITY 1] ok'); send('* OK [UIDNEXT 4] ok')
            send(`${tag} OK [READ-ONLY] done`); break
          case 'LOGOUT': send('* BYE bye'); send(`${tag} OK done`); socket.end(); break
          default: send(`${tag} OK done`)
        }
      }
    })
    socket.on('error', () => {})
  })
}

/** SMTP: EHLO с AUTH PLAIN, проверка пароля, QUIT. */
function fakeSmtp() {
  return net.createServer((socket) => {
    let buffer = ''
    let awaitingPlain = false
    const send = (s) => socket.write(s + '\r\n')
    const plainOk = (b64) => Buffer.from(b64, 'base64').toString('utf8').split('\0')[2] === PASSWORD
    send('220 fake ESMTP ready')
    socket.on('data', (chunk) => {
      buffer += chunk.toString('utf8')
      let i
      while ((i = buffer.indexOf('\r\n')) >= 0) {
        const line = buffer.slice(0, i)
        buffer = buffer.slice(i + 2)
        if (awaitingPlain) { awaitingPlain = false; send(plainOk(line) ? '235 ok' : '535 bad credentials'); continue }
        const [command, mech, initial] = line.split(' ')
        switch (command.toUpperCase()) {
          case 'EHLO': send('250-fake'); send('250 AUTH PLAIN'); break
          case 'HELO': send('250 fake'); break
          case 'AUTH':
            if (mech.toUpperCase() !== 'PLAIN') send('504 unsupported')
            else if (initial) send(plainOk(initial) ? '235 ok' : '535 bad credentials')
            else { awaitingPlain = true; send('334 ') }
            break
          case 'QUIT': send('221 bye'); socket.end(); break
          default: send('250 ok')
        }
      }
    })
    socket.on('error', () => {})
  })
}

const listen = (server) => new Promise((resolve) => server.listen(0, '127.0.0.1', () => resolve(server.address().port)))
const db = (sql) => execFileSync('node', [path.join(__dirname, 'db.cjs'), sql], { encoding: 'utf8' })

;(async () => {
  const imapServer = fakeImap()
  const smtpServer = fakeSmtp()
  const imapPort = await listen(imapServer)
  const smtpPort = await listen(smtpServer)

  const login = await (await fetch(`${ADMIN}/api/auth/login`, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ login: 'admin', password: env.BOOTSTRAP_ADMIN_PASSWORD }) })).json()
  const auth = { authorization: `Bearer ${login.accessToken}`, 'content-type': 'application/json' }
  const call = async (method, p, body) => { const r = await fetch(`${ADMIN}/api/settings${p}`, { method, headers: auth, body: body ? JSON.stringify(body) : undefined }); return { status: r.status, body: await r.json().catch(() => null) } }

  const siteId = (await call('GET', '/sites')).body[0].id
  const server = (port, password, extra = {}) => ({ email: EMAIL, host: '127.0.0.1', port, security: 'None', username: null, password, ...extra })
  const input = (imap, smtp) => ({ name: `Mail ${run}`, siteId, isEnabled: true, forwardCreatesTicket: true, quoting: 'DoNotSend', imap, smtp })
  let id = null
  try {
    const noPassword = await call('POST', '/email-channels', input(server(imapPort, ''), server(smtpPort, PASSWORD)))
    check('new channel without a password → 400', noPassword.status === 400, `${noPassword.status} ${noPassword.body?.title ?? ''}`)
    const badHost = await call('POST', '/email-channels', input(server(imapPort, PASSWORD, { host: 'imaps://x' }), server(smtpPort, PASSWORD)))
    check('host with a protocol → 400', badHost.status === 400, String(badHost.status))

    const created = await call('POST', '/email-channels', input(server(imapPort, PASSWORD, { username: EMAIL.toUpperCase() }), server(smtpPort, PASSWORD)))
    id = created.body?.id
    const c = created.body ?? {}
    check('channel created and checked', created.status === 200 && c.lastCheckOk === true, `${created.status} ${c.lastCheckMessage ?? created.body?.title}`)
    check('IMAP check opened the inbox', /messages in the inbox: 3/.test(c.lastCheckMessage ?? ''), c.lastCheckMessage)
    check('settings saved', c.forwardCreatesTicket === true && c.quoting === 'DoNotSend' && c.imap?.port === imapPort && c.imap?.username === null)
    check('password is not returned', !JSON.stringify(created.body).includes(PASSWORD) && c.imap?.hasPassword === true && !('password' in (c.imap ?? {})))
    const row = db(`SELECT imap_protected_password, smtp_protected_password FROM email_channels WHERE id = '${id}'`)
    check('password is encrypted in the DB', row.length > 20 && !row.includes(PASSWORD))

    const dup = await call('POST', '/email-channels', input(server(imapPort, PASSWORD), server(smtpPort, PASSWORD)))
    check('same mailbox twice → 409', dup.status === 409, String(dup.status))

    const wrong = await call('PUT', `/email-channels/${id}`, input(server(imapPort, 'wrong-password'), server(smtpPort, '')))
    check('wrong IMAP password: saved, check failed', wrong.status === 200 && wrong.body.lastCheckOk === false
      && /IMAP\): wrong username or password/.test(wrong.body.lastCheckMessage) && /SMTP\): connected/.test(wrong.body.lastCheckMessage), wrong.body?.lastCheckMessage)

    await call('PUT', `/email-channels/${id}`, input(server(imapPort, PASSWORD), server(smtpPort, '')))
    const kept = await call('PUT', `/email-channels/${id}`, input(server(imapPort, ''), server(smtpPort, '')))
    check('empty password keeps the saved one', kept.status === 200 && kept.body.lastCheckOk === true, kept.body?.lastCheckMessage)

    // Закрытый порт: сервер слушал и закрылся.
    const closed = net.createServer()
    const closedPort = await listen(closed)
    await new Promise((r) => closed.close(r))
    const refused = await call('PUT', `/email-channels/${id}`, input(server(imapPort, ''), server(closedPort, '')))
    check('closed SMTP port: cannot connect', refused.body?.lastCheckOk === false && /SMTP\): cannot connect to 127\.0\.0\.1/.test(refused.body.lastCheckMessage), refused.body?.lastCheckMessage)
    await call('PUT', `/email-channels/${id}`, input(server(imapPort, ''), server(smtpPort, '')))
    const recheck = await call('POST', `/email-channels/${id}/check`)
    check('check button', recheck.status === 200 && recheck.body.lastCheckOk === true, recheck.body?.lastCheckMessage)

    check('channel in the list', (await call('GET', '/email-channels')).body.some((x) => x.id === id))
  } finally {
    if (id) {
      const del = await call('DELETE', `/email-channels/${id}`)
      check('channel deleted', del.status === 204 && !(await call('GET', '/email-channels')).body.some((x) => x.id === id), String(del.status))
    }
    imapServer.close()
    smtpServer.close()
  }
  console.log(failed ? `${failed} FAILED` : 'ALL PASSED')
  process.exit(failed ? 1 : 0)
})()
