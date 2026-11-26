// Шлюз WhatsApp для Relayo. Слушает только внутреннюю сеть: его вызывает Relayo.Internal с общим ключом.
//
//   GET    /health                    — жив ли процесс
//   GET    /sessions/:id              — состояние подключения канала (и QR, пока его ждут)
//   PUT    /sessions/:id              — подключить: начать вход по QR или восстановить соединение
//   DELETE /sessions/:id              — отключить: выйти из WhatsApp и стереть ключи
//   POST   /sessions/:id/messages     — отправить текст или файл
//
// Входящие сообщения шлюз сам передаёт в Internal (POST /api/whatsapp-gateway/channels/:id/messages).
// Один экземпляр на все номера: сессия WhatsApp живёт в одном процессе.
import { timingSafeEqual } from 'node:crypto'
import { createServer, type IncomingMessage, type ServerResponse } from 'node:http'
import { pino } from 'pino'
import { loadConfig } from './config.js'
import { InternalClient } from './internal.js'
import { type OutgoingMessage, SessionManager, SessionNotConnectedError } from './sessions.js'

const config = loadConfig()
const log = pino({ level: config.logLevel, base: { service: 'relayo-whatsapp' } })
const sessions = new SessionManager(config.sessionsDir, new InternalClient(config.internalUrl, config.key, log), config.maxFileBytes, log)
const keyBytes = Buffer.from(config.key)
// Файл в base64 занимает на треть больше, плюс JSON вокруг.
const maxBodyBytes = Math.ceil(config.maxFileBytes * 1.4) + 64 * 1024

function authorized(request: IncomingMessage): boolean {
  const header = request.headers['x-relayo-gateway-key']
  const given = Buffer.from(typeof header === 'string' ? header : '')
  return given.length === keyBytes.length && timingSafeEqual(given, keyBytes)
}

function send(response: ServerResponse, status: number, body?: unknown): void {
  response.writeHead(status, { 'content-type': 'application/json; charset=utf-8', 'cache-control': 'no-store' })
  response.end(body === undefined ? undefined : JSON.stringify(body))
}

async function readJson<T>(request: IncomingMessage): Promise<T> {
  const chunks: Buffer[] = []
  let size = 0
  for await (const chunk of request) {
    size += (chunk as Buffer).length
    if (size > maxBodyBytes) throw Object.assign(new Error('Request is too large'), { status: 413 })
    chunks.push(chunk as Buffer)
  }
  try {
    return JSON.parse(Buffer.concat(chunks).toString('utf8')) as T
  } catch {
    throw Object.assign(new Error('Body must be JSON'), { status: 400 })
  }
}

function validOutgoing(body: OutgoingMessage): boolean {
  if (typeof body?.to !== 'string' || !/^[0-9]+@(s\.whatsapp\.net|lid)$/.test(body.to)) return false
  if (body.file) return typeof body.file.content === 'string' && typeof body.file.fileName === 'string' && typeof body.file.contentType === 'string'
  return typeof body.text === 'string' && body.text.length > 0
}

const server = createServer(async (request, response) => {
  const url = new URL(request.url ?? '/', 'http://gateway')
  try {
    if (url.pathname === '/health') return send(response, 200, { status: 'ok' })
    if (!authorized(request)) return send(response, 401, { error: 'unauthorized' })

    const match = /^\/sessions\/([^/]+)(\/messages)?$/.exec(url.pathname)
    if (!match || !SessionManager.isChannelId(match[1])) return send(response, 404, { error: 'not_found' })
    const id = match[1].toLowerCase()

    if (!match[2]) {
      if (request.method === 'GET') return send(response, 200, sessions.get(id))
      if (request.method === 'PUT') return send(response, 200, await sessions.start(id))
      if (request.method === 'DELETE') {
        await sessions.logout(id)
        return send(response, 204)
      }
      return send(response, 405, { error: 'method_not_allowed' })
    }

    if (request.method !== 'POST') return send(response, 405, { error: 'method_not_allowed' })
    const body = await readJson<OutgoingMessage>(request)
    if (!validOutgoing(body)) return send(response, 400, { error: 'invalid_message' })
    return send(response, 200, { id: await sessions.send(id, body) })
  } catch (error) {
    if (error instanceof SessionNotConnectedError) return send(response, 409, { error: 'not_connected' })
    const status = (error as { status?: number }).status
    if (status) return send(response, status, { error: (error as Error).message })
    log.error({ err: error, path: url.pathname }, 'Request failed')
    return send(response, 500, { error: 'internal_error' })
  }
})

await sessions.restore()
server.listen(config.port, () => log.info({ port: config.port }, 'WhatsApp gateway is listening'))

for (const signal of ['SIGINT', 'SIGTERM'] as const) {
  process.on(signal, () => {
    log.info('Stopping')
    server.close()
    void sessions.stopAll().finally(() => process.exit(0))
  })
}
