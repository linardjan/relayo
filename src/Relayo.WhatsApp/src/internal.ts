// Вызовы внутреннего сервиса: входящие сообщения и смена состояния подключения.
// Если Internal недоступен (перезапуск, сеть), вызов повторяется с паузами — сообщение не теряется,
// пока жив процесс шлюза. Повторно доставленное сообщение Internal не дублирует: у него тот же id.
import type { Logger } from 'pino'

export interface IncomingFile {
  fileName: string
  contentType: string
  /** Содержимое в base64. */
  content: string
}

export interface IncomingMessage {
  /** Id сообщения в WhatsApp: по нему Internal отбрасывает повторы. */
  messageId: string
  /** Куда отвечать: JID чата (номер@s.whatsapp.net или id@lid). */
  chatJid: string
  /** Номер отправителя без «+», если WhatsApp его сообщил. */
  phone: string | null
  /** Имя из профиля WhatsApp. */
  pushName: string | null
  text: string
  files: IncomingFile[]
  /** Что прислал клиент, если это не текст и не файл (геопозиция, контакт, опрос…) или файл не удалось скачать. */
  unsupported: string | null
  sentAt: string
}

export interface SessionStatusUpdate {
  status: 'connected' | 'logged_out'
  phone: string | null
  name: string | null
}

/** Ответ Internal «канала нет»: сессию надо закрыть. */
export class ChannelGoneError extends Error {}

const RETRY_DELAYS_MS = [1_000, 2_000, 5_000, 10_000, 30_000, 60_000]
const MAX_ATTEMPTS = 200 // ~3 часа при паузе в минуту

export class InternalClient {
  constructor(
    private readonly baseUrl: string,
    private readonly key: string,
    private readonly log: Logger,
  ) {}

  postMessage(channelId: string, message: IncomingMessage): Promise<void> {
    return this.withRetry(`message ${message.messageId}`, () =>
      this.post(`/api/whatsapp-gateway/channels/${channelId}/messages`, message),
    )
  }

  postStatus(channelId: string, update: SessionStatusUpdate): Promise<void> {
    return this.withRetry(`status ${update.status}`, () => this.post(`/api/whatsapp-gateway/channels/${channelId}/status`, update))
  }

  private async post(path: string, body: unknown): Promise<void> {
    const response = await fetch(this.baseUrl + path, {
      method: 'POST',
      headers: { 'content-type': 'application/json', 'x-relayo-gateway-key': this.key },
      body: JSON.stringify(body),
      signal: AbortSignal.timeout(60_000),
    })
    if (response.status === 404) throw new ChannelGoneError('Channel not found in Relayo')
    if (!response.ok) {
      const error = new Error(`Internal responded ${response.status}`) as Error & { permanent?: boolean }
      // 400 — сообщение не примут и при повторе.
      error.permanent = response.status === 400 || response.status === 413
      throw error
    }
  }

  private async withRetry(what: string, call: () => Promise<void>): Promise<void> {
    for (let attempt = 0; ; attempt++) {
      try {
        await call()
        return
      } catch (error) {
        if (error instanceof ChannelGoneError) throw error
        const permanent = (error as { permanent?: boolean }).permanent
        if (permanent || attempt + 1 >= MAX_ATTEMPTS) {
          this.log.error({ err: error }, `Internal did not accept ${what}: giving up`)
          return
        }
        const delay = RETRY_DELAYS_MS[Math.min(attempt, RETRY_DELAYS_MS.length - 1)]
        this.log.warn({ err: (error as Error).message }, `Could not deliver ${what} to Internal, retrying in ${delay / 1000}s`)
        await new Promise((resolve) => setTimeout(resolve, delay))
      }
    }
  }
}
