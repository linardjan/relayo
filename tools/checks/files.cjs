// Проверка вложений (S3) и настроек канала-виджета на работающем стенде.
//   node tools/checks/files.cjs
const crypto = require('crypto')
const { sql, close, env } = require('./db.cjs')

const PUB = 'http://localhost:5200'
const ADMIN = 'http://localhost:5100'
const ORIGIN = 'http://localhost:5300'
const TOKEN_WIDGET = env.TEST_WIDGET_TOKEN_MODE_ID
const DIRECT_WIDGET = env.TEST_WIDGET_DIRECT_MODE_ID
const run = Date.now() % 1_000_000

let failed = 0
const check = (name, ok, extra = '') => { console.log(`${ok ? 'PASS' : 'FAIL'} ${name}${extra ? ' — ' + extra : ''}`); if (!ok) failed++ }

// Минимальная настоящая картинка PNG 1×1.
const PNG = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII=', 'base64')
const PDF = Buffer.from('%PDF-1.4\n1 0 obj<<>>endobj\ntrailer<<>>\n%%EOF\n')

async function widget(id, method, path, body, visitor) {
  const headers = { origin: ORIGIN }
  if (visitor) headers['x-relayo-visitor'] = visitor
  const isForm = body instanceof FormData
  if (body && !isForm) headers['content-type'] = 'application/json'
  const r = await fetch(`${PUB}/api/widget/${id}${path}`, { method, headers, body: body ? (isForm ? body : JSON.stringify(body)) : undefined })
  return { status: r.status, body: await r.json().catch(() => null) }
}

function form(fields, files) {
  const f = new FormData()
  for (const [k, v] of Object.entries(fields)) f.append(k, v)
  for (const [name, type, bytes] of files) f.append('files', new Blob([bytes], { type }), name)
  return f
}

;(async () => {
  const login = await fetch(`${ADMIN}/api/auth/login`, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ login: 'admin', password: env.BOOTSTRAP_ADMIN_PASSWORD }) })
  const { accessToken, user } = await login.json()
  const auth = { authorization: `Bearer ${accessToken}` }
  const admin = async (method, path, body) => {
    const isForm = body instanceof FormData
    const r = await fetch(`${ADMIN}/api${path}`, {
      method,
      headers: { ...auth, ...(body && !isForm ? { 'content-type': 'application/json' } : {}) },
      body: body ? (isForm ? body : JSON.stringify(body)) : undefined,
    })
    return { status: r.status, body: await r.json().catch(() => null) }
  }
  if (!user.alias) await admin('PUT', '/me/alias', { alias: 'Anna' })

  // 1. Настройки виджета приходят в /config
  const config = await widget(TOKEN_WIDGET, 'GET', '/config')
  check('config has options and attachment limit', config.body?.options?.items?.[0]?.type === 'Chat' && config.body.maxAttachmentBytes > 0,
    `${config.body?.maxAttachmentBytes}`)

  // 2. Посетитель отправляет текст и картинку
  const session = await widget(TOKEN_WIDGET, 'POST', '/session')
  const visitor = session.body.visitorToken
  const sent = await widget(TOKEN_WIDGET, 'POST', '/messages', form({ text: `Look ${run}`, clientMessageId: `f-${run}` }, [['shot.png', 'image/png', PNG]]), visitor)
  const att = sent.body?.attachments?.[0]
  check('visitor message with file saved', sent.status === 200 && att?.fileName === 'shot.png' && att.contentType === 'image/png' && att.size === PNG.length, `${sent.status} ${JSON.stringify(sent.body?.attachments)}`)
  check('download link points to S3_PUBLIC_URL', !!att?.url && att.url.startsWith(env.S3_PUBLIC_URL) && att.url.includes('X-Amz-Signature'), att?.url?.slice(0, 80))
  const download = att?.url ? await fetch(att.url) : null
  const bytes = download ? Buffer.from(await download.arrayBuffer()) : Buffer.alloc(0)
  check('file is downloadable by the link', download?.status === 200 && bytes.equals(PNG) && download.headers.get('content-type') === 'image/png', `${download?.status} ${download?.headers.get('content-type')}`)
  const unsigned = att?.url ? await fetch(att.url.split('?')[0]) : null
  check('attachments bucket is not public', unsigned && unsigned.status !== 200, String(unsigned?.status))

  // Повтор того же сообщения не создаёт дубль и не грузит файл заново
  const again = await widget(TOKEN_WIDGET, 'POST', '/messages', form({ text: `Look ${run}`, clientMessageId: `f-${run}` }, [['shot.png', 'image/png', PNG]]), visitor)
  check('retry returns the same message', again.status === 200 && again.body.id === sent.body.id && again.body.attachments?.[0]?.id === att?.id)
  const count = await sql(`select count(*) from attachments where chat_id = '${sent.body.chatId}'`)
  check('no duplicate attachment', count === '1', count)

  // Только файл, без текста
  const onlyFile = await widget(TOKEN_WIDGET, 'POST', '/messages', form({ clientMessageId: `g-${run}` }, [['doc.pdf', '', PDF]]), visitor)
  check('file without text, type by extension', onlyFile.status === 200 && onlyFile.body.text === '' && onlyFile.body.attachments?.[0]?.contentType === 'application/pdf', JSON.stringify(onlyFile.body?.attachments ?? onlyFile.body))

  // 3. Отказы
  const fake = await widget(TOKEN_WIDGET, 'POST', '/messages', form({ text: 'x' }, [['evil.png', 'image/png', Buffer.from('<html><script>alert(1)</script></html>')]]), visitor)
  check('HTML disguised as PNG rejected', fake.status === 400 && fake.body?.code === 'attachment_type_not_allowed', `${fake.status} ${fake.body?.code}`)
  const exe = await widget(TOKEN_WIDGET, 'POST', '/messages', form({ text: 'x' }, [['run.exe', 'application/x-msdownload', Buffer.from('MZ')]]), visitor)
  check('disallowed type rejected', exe.status === 400 && exe.body?.code === 'attachment_type_not_allowed', `${exe.status} ${exe.body?.code}`)
  const big = Buffer.alloc(config.body.maxAttachmentBytes + 10, 0x41)
  const tooBig = await widget(TOKEN_WIDGET, 'POST', '/messages', form({ text: 'x' }, [['big.txt', 'text/plain', big]]), visitor)
  check('too large rejected', tooBig.status === 413 && tooBig.body?.code === 'attachment_too_large', `${tooBig.status} ${tooBig.body?.code}`)

  // 4. Оператор отвечает с файлом
  const ticketId = await sql(`select ticket_id from chats where id = '${sent.body.chatId}'`)
  const reply = await admin('POST', `/tickets/${ticketId}/messages`, form({ chatId: sent.body.chatId, text: `Here you go ${run}` }, [['answer.pdf', 'application/pdf', PDF]]))
  check('operator reply with file', reply.status === 200 && reply.body.attachments?.[0]?.fileName === 'answer.pdf', `${reply.status} ${JSON.stringify(reply.body)}`.slice(0, 200))
  const onlyOperatorFile = await admin('POST', `/tickets/${ticketId}/messages`, form({ chatId: sent.body.chatId }, [['pic.png', 'image/png', PNG]]))
  check('operator file without text', onlyOperatorFile.status === 200 && onlyOperatorFile.body.text === '')
  const details = await admin('GET', `/tickets/${ticketId}`)
  const all = details.body.chats.flatMap((c) => c.messages)
  check('admin sees client and operator files', all.some((m) => m.attachments?.[0]?.fileName === 'shot.png') && all.some((m) => m.attachments?.[0]?.fileName === 'answer.pdf'))
  check('ticket preview shows the file name', details.body.ticket.lastMessagePreview === '📎 pic.png', details.body.ticket.lastMessagePreview)
  const history = await widget(TOKEN_WIDGET, 'GET', '/messages', undefined, visitor)
  const fromOperator = history.body.find((m) => m.text === `Here you go ${run}`)
  check('visitor sees operator file', fromOperator?.attachments?.[0]?.fileName === 'answer.pdf' && !!fromOperator.attachments[0].url)
  const pdf = await fetch(fromOperator.attachments[0].url)
  check('non-image downloads as attachment', /attachment; filename\*=UTF-8''answer\.pdf/.test(pdf.headers.get('content-disposition') ?? ''), pdf.headers.get('content-disposition'))

  // 5. Вложения выключены у виджета
  const widgets = (await admin('GET', '/settings/widgets')).body
  const direct = widgets.find((w) => w.widget.id === DIRECT_WIDGET).widget
  const { id: _i, siteId: _s, siteCode: _c, createdAt: _d, ...input } = direct
  const off = await admin('PUT', `/settings/widgets/${DIRECT_WIDGET}`, { ...input, options: { ...input.options, allowAttachments: false, caption: `Policy ${run}`, items: [{ type: 'Chat', label: 'Chat' }, { type: 'Link', label: 'Site', url: 'https://example.com' }] } })
  check('widget options saved', off.status === 200 && off.body.widget.options.caption === `Policy ${run}` && off.body.widget.options.items.length === 2 && off.body.widget.hasUnpublishedChanges, `${off.status} ${JSON.stringify(off.body).slice(0, 160)}`)
  const badLink = await admin('PUT', `/settings/widgets/${DIRECT_WIDGET}`, { ...input, options: { ...input.options, items: [{ type: 'Link', label: 'x', url: 'javascript:alert(1)' }] } })
  check('unsafe link item rejected', badLink.status === 400, String(badLink.status))
  // Настройки в публичном сервисе кэшируются до 30 секунд: ждём их по /config, файлы при этом не шлём —
  // загрузки ограничены PUBLIC_RATE_LIMIT_UPLOADS_PER_MINUTE.
  const directSession = await widget(DIRECT_WIDGET, 'POST', '/session')
  for (let i = 0; i < 35 && (await widget(DIRECT_WIDGET, 'GET', '/config')).body?.maxAttachmentBytes !== 0; i++)
    await new Promise((r) => setTimeout(r, 1000))
  const disabled = await widget(DIRECT_WIDGET, 'POST', '/messages', form({ text: 'x' }, [['a.png', 'image/png', PNG]]), directSession.body.visitorToken)
  check('attachments off → 409', disabled.status === 409 && disabled.body?.code === 'attachments_disabled', `${disabled.status} ${disabled.body?.code}`)
  await admin('PUT', `/settings/widgets/${DIRECT_WIDGET}`, input)

  // 6. Картинка кнопки и удаление виджета — на отдельном виджете
  const created = await admin('POST', '/settings/widgets', { siteId: direct.siteId, name: `Files ${run}`, identificationMode: 'Direct', title: 'Help', greeting: '', accentColor: '#112233', position: 'Left', defaultLocale: 'en', telegramBotId: null })
  const temp = created.body?.widget
  check('widget created with default options', created.status === 200 && temp.options.allowAttachments === true && temp.options.items[0].type === 'Chat')
  const icon = await admin('POST', `/settings/widgets/${temp.id}/button-icon`, (() => { const f = new FormData(); f.append('file', new Blob([PNG], { type: 'image/png' }), 'icon.png'); return f })())
  const iconUrl = icon.body?.buttonIconUrl
  check('button icon uploaded', icon.status === 200 && /widget_[A-Za-z0-9]{10}_icon_[A-Za-z0-9]{10}\.png$/.test(iconUrl ?? ''), `${icon.status} ${iconUrl}`)
  const iconFile = iconUrl ? await fetch(iconUrl) : null
  check('button icon is public', iconFile?.status === 200, String(iconFile?.status))
  const badIcon = await admin('POST', `/settings/widgets/${temp.id}/button-icon`, (() => { const f = new FormData(); f.append('file', new Blob([PDF], { type: 'application/pdf' }), 'x.pdf'); return f })())
  check('non-image icon rejected', badIcon.status === 400, String(badIcon.status))
  const published = await admin('POST', `/settings/widgets/${temp.id}/publish`)
  const file = await (await fetch(published.body.fileUrl)).text()
  check('published file carries options and icon', file.includes('buttonIconUrl') && file.includes('maxAttachmentBytes') && file.includes('items'))
  const removed = await admin('DELETE', `/settings/widgets/${temp.id}`)
  check('widget deleted', removed.status === 204)
  const gone = await fetch(published.body.fileUrl)
  check('published file removed from S3', gone.status === 404 || gone.status === 403, String(gone.status))
  check('deleted widget not listed', !(await admin('GET', '/settings/widgets')).body.some((w) => w.widget.id === temp.id))

  await close()
  console.log(failed ? `${failed} FAILED` : 'ALL PASSED')
  process.exit(failed ? 1 : 0)
})().catch(async (e) => { console.error(e); await close(); process.exit(1) })
