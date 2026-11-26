// Сессии WhatsApp: по одной на канал Relayo. Ключи сессии лежат в папке <sessionsDir>/<id канала>.
// Подключение — как «Связанные устройства» в WhatsApp: администратор сканирует QR-код телефоном.
import { existsSync } from 'node:fs'
import { mkdir, readFile, readdir, rm } from 'node:fs/promises'
import path from 'node:path'
import type { Boom } from '@hapi/boom'
import makeWASocket, {
  Browsers,
  DisconnectReason,
  type WAMessage,
  type WASocket,
  downloadMediaMessage,
  fetchLatestBaileysVersion,
  isJidBroadcast,
  isJidGroup,
  isJidNewsletter,
  isLidUser,
  isPnUser,
  jidNormalizedUser,
  normalizeMessageContent,
  useMultiFileAuthState,
} from 'baileys'
import type { Logger } from 'pino'
import { ChannelGoneError, type IncomingFile, type IncomingMessage, type InternalClient } from './internal.js'

export type SessionStatus = 'starting' | 'qr' | 'connected' | 'disconnected'

export interface SessionState {
  status: SessionStatus
  /** Строка для QR-кода (админка рисует его сама). */
  qr: string | null
  qrExpiresAt: string | null
  phone: string | null
  name: string | null
  /** Почему сессия отключена: logged_out, qr_timeout, replaced, error. */
  reason: string | null
}

export interface OutgoingMessage {
  to: string
  text?: string
  file?: { fileName: string; contentType: string; content: string; caption?: string }
}

export class SessionNotConnectedError extends Error {}

/** WhatsApp меняет QR примерно раз в 20 секунд. */
const QR_LIFETIME_MS = 20_000
/** Сколько ждать сканирования: потом сессия закрывается, администратор запросит новый QR. */
const QR_WAIT_MS = 3 * 60_000
const RECONNECT_DELAYS_MS = [1_000, 2_000, 5_000, 10_000, 30_000]
const CHANNEL_ID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i

class Session {
  socket: WASocket | null = null
  state: SessionState = { status: 'starting', qr: null, qrExpiresAt: null, phone: null, name: null, reason: null }
  /** Входящие доставляются в Internal по одному, в порядке получения. */
  delivery: Promise<void> = Promise.resolve()
  reconnects = 0
  stopped = false
  /** Когда перестать ждать сканирования QR. */
  qrDeadline: number | null = null

  constructor(readonly id: string) {}
}

export class SessionManager {
  private readonly sessions = new Map<string, Session>()
  private version: [number, number, number] | undefined

  constructor(
    private readonly sessionsDir: string,
    private readonly internal: InternalClient,
    private readonly maxFileBytes: number,
    private readonly log: Logger,
  ) {}

  static isChannelId(id: string): boolean {
    return CHANNEL_ID.test(id)
  }

  /** При запуске поднимает все ранее подключённые номера. Папки без завершённого входа (QR не отсканировали) удаляются. */
  async restore(): Promise<void> {
    await mkdir(this.sessionsDir, { recursive: true, mode: 0o700 })
    for (const entry of await readdir(this.sessionsDir, { withFileTypes: true })) {
      if (!entry.isDirectory() || !SessionManager.isChannelId(entry.name)) continue
      if (await this.isRegistered(entry.name)) await this.start(entry.name)
      else await rm(this.dir(entry.name), { recursive: true, force: true })
    }
  }

  private async isRegistered(id: string): Promise<boolean> {
    const file = path.join(this.dir(id), 'creds.json')
    if (!existsSync(file)) return false
    try {
      return Boolean(JSON.parse(await readFile(file, 'utf8')).me?.id)
    } catch {
      return false
    }
  }

  get(id: string): SessionState {
    return this.sessions.get(id)?.state ?? { status: 'disconnected', qr: null, qrExpiresAt: null, phone: null, name: null, reason: null }
  }

  /** Подключить номер: показать QR или, если вход уже был, просто восстановить соединение. */
  async start(id: string): Promise<SessionState> {
    const existing = this.sessions.get(id)
    if (existing && existing.state.status !== 'disconnected') return existing.state
    const session = new Session(id)
    this.sessions.set(id, session)
    await this.connect(session)
    return session.state
  }

  /** Отключить номер: выйти из WhatsApp (устройство пропадёт из «Связанных устройств») и стереть ключи. */
  async logout(id: string): Promise<void> {
    const session = this.sessions.get(id)
    this.sessions.delete(id)
    if (session) {
      session.stopped = true
      try {
        if (session.state.status === 'connected') await session.socket?.logout()
        else await session.socket?.end(undefined)
      } catch (error) {
        this.log.warn({ channel: id, err: (error as Error).message }, 'Logout failed, removing the session anyway')
      }
    }
    await rm(this.dir(id), { recursive: true, force: true })
  }

  async send(id: string, message: OutgoingMessage): Promise<string | null> {
    const session = this.sessions.get(id)
    if (!session?.socket || session.state.status !== 'connected') throw new SessionNotConnectedError('WhatsApp is not connected')
    const socket = session.socket
    if (message.file) {
      const content = Buffer.from(message.file.content, 'base64')
      const { fileName, contentType, caption } = message.file
      const sent = contentType.startsWith('image/') && contentType !== 'image/svg+xml'
        ? await socket.sendMessage(message.to, { image: content, mimetype: contentType, caption })
        : contentType.startsWith('video/')
          ? await socket.sendMessage(message.to, { video: content, mimetype: contentType, caption })
          : await socket.sendMessage(message.to, { document: content, mimetype: contentType, fileName, caption })
      return sent?.key.id ?? null
    }
    const sent = await socket.sendMessage(message.to, { text: message.text ?? '' })
    return sent?.key.id ?? null
  }

  async stopAll(): Promise<void> {
    for (const session of this.sessions.values()) {
      session.stopped = true
      await session.socket?.end(undefined).catch(() => undefined)
    }
  }

  private dir(id: string): string {
    return path.join(this.sessionsDir, id)
  }

  private async connect(session: Session): Promise<void> {
    const { state, saveCreds } = await useMultiFileAuthState(this.dir(session.id))
    // Версию протокола берём актуальную: со старой WhatsApp может не пустить. Без сети — встроенная.
    this.version ??= await fetchLatestBaileysVersion()
      .then((v) => v.version)
      .catch(() => undefined)

    const socket = makeWASocket({
      auth: state,
      version: this.version,
      browser: Browsers.appropriate('Relayo'),
      logger: this.log.child({ channel: session.id, module: 'baileys' }, { level: 'warn' }) as never,
      markOnlineOnConnect: false,
      // Полную историю не просим. То, что WhatsApp всё же пришлёт при входе, приходит не как «notify»
      // и в Relayo не попадает (см. messages.upsert): операторы видят только новые сообщения.
      syncFullHistory: false,
    })
    session.socket = socket
    session.state = { ...session.state, status: 'starting', reason: null }

    socket.ev.on('creds.update', saveCreds)

    socket.ev.on('connection.update', (update) => {
      if (session.stopped || session.socket !== socket) return
      if (update.qr) {
        session.qrDeadline ??= Date.now() + QR_WAIT_MS
        if (Date.now() > session.qrDeadline) {
          this.log.info({ channel: session.id }, 'QR code was not scanned in time')
          session.stopped = true
          void socket.end(undefined).catch(() => undefined).then(() => this.stopSession(session, 'qr_timeout'))
          return
        }
        session.state = {
          ...session.state,
          status: 'qr',
          qr: update.qr,
          qrExpiresAt: new Date(Date.now() + QR_LIFETIME_MS).toISOString(),
        }
      }
      if (update.connection === 'open') {
        const phone = socket.user?.id ? jidNormalizedUser(socket.user.id).split('@')[0] : null
        session.reconnects = 0
        session.qrDeadline = null
        session.state = { status: 'connected', qr: null, qrExpiresAt: null, phone, name: socket.user?.name ?? null, reason: null }
        this.log.info({ channel: session.id }, 'WhatsApp connected')
        this.report(session, { status: 'connected', phone, name: session.state.name })
      }
      if (update.connection === 'close') void this.onClose(session, socket, update.lastDisconnect?.error)
    })

    socket.ev.on('messages.upsert', ({ messages, type }) => {
      if (type !== 'notify') return
      for (const message of messages) this.enqueue(session, socket, message)
    })
  }

  private async onClose(session: Session, socket: WASocket, error: Error | undefined): Promise<void> {
    const code = (error as Boom | undefined)?.output?.statusCode
    // Вход по QR завершён, когда у ключей есть «me» (флаг registered ставится только при входе по коду сопряжения).
    const registered = Boolean(socket.authState.creds.me?.id)
    session.socket = null

    if (code === DisconnectReason.loggedOut) {
      // Устройство удалили в телефоне («Связанные устройства → Выйти»): нужен новый QR.
      this.log.warn({ channel: session.id }, 'WhatsApp logged out')
      await this.stopSession(session, 'logged_out')
      this.report(session, { status: 'logged_out', phone: session.state.phone, name: session.state.name })
      return
    }
    if (code === DisconnectReason.connectionReplaced) {
      this.log.error({ channel: session.id }, 'Another gateway instance uses this session: stopping here')
      session.stopped = true
      session.state = { ...session.state, status: 'disconnected', qr: null, qrExpiresAt: null, reason: 'replaced' }
      return
    }
    if (!registered && code !== DisconnectReason.restartRequired) {
      // QR так и не отсканировали: WhatsApp закрыл соединение. Администратор нажмёт «Connect» снова.
      await this.stopSession(session, 'qr_timeout')
      return
    }
    if (session.stopped) return

    // Обрыв сети, перезапуск после входа (515) и т. п.: переподключаемся.
    const delay = code === DisconnectReason.restartRequired ? 0 : RECONNECT_DELAYS_MS[Math.min(session.reconnects, RECONNECT_DELAYS_MS.length - 1)]
    session.reconnects++
    session.state = { ...session.state, status: 'starting', qr: null, qrExpiresAt: null }
    this.log.info({ channel: session.id, code, delay }, 'WhatsApp connection closed, reconnecting')
    setTimeout(() => {
      if (!session.stopped && this.sessions.get(session.id) === session)
        void this.connect(session).catch((err) => this.log.error({ channel: session.id, err }, 'Reconnect failed'))
    }, delay)
  }

  private async stopSession(session: Session, reason: string): Promise<void> {
    session.stopped = true
    session.state = { ...session.state, status: 'disconnected', qr: null, qrExpiresAt: null, reason }
    await rm(this.dir(session.id), { recursive: true, force: true })
  }

  /** Канал удалён в Relayo, пока шлюз был отключён: выходим из WhatsApp. */
  private onChannelGone(session: Session): void {
    this.log.warn({ channel: session.id }, 'Channel no longer exists in Relayo: logging out')
    void this.logout(session.id)
  }

  private report(session: Session, update: Parameters<InternalClient['postStatus']>[1]): void {
    this.internal.postStatus(session.id, update).catch((error) => {
      if (error instanceof ChannelGoneError) this.onChannelGone(session)
    })
  }

  private enqueue(session: Session, socket: WASocket, message: WAMessage): void {
    session.delivery = session.delivery.then(async () => {
      try {
        const incoming = await this.toIncoming(socket, message)
        if (incoming) await this.internal.postMessage(session.id, incoming)
      } catch (error) {
        if (error instanceof ChannelGoneError) this.onChannelGone(session)
        else this.log.error({ channel: session.id, err: error }, 'Could not process an incoming message')
      }
    })
  }

  /** Сообщение клиента в том виде, в каком его ждёт Internal. null — не для Relayo (свои, группы, рассылки, служебные). */
  private async toIncoming(socket: WASocket, message: WAMessage): Promise<IncomingMessage | null> {
    const key = message.key
    const jid = key.remoteJid
    if (!jid || key.fromMe || !key.id) return null
    // Группы, рассылки, статусы и каналы операторам поддержки не нужны.
    if (isJidGroup(jid) || isJidBroadcast(jid) || isJidNewsletter(jid)) return null

    const content = normalizeMessageContent(message.message)
    if (!content || content.protocolMessage || content.reactionMessage || content.senderKeyDistributionMessage && Object.keys(content).length === 1)
      return null

    // Один человек должен быть одним клиентом: по возможности адресуем его номером, а не скрытым id (LID).
    let chatJid = jidNormalizedUser(jid)
    let phone: string | null = null
    if (isPnUser(chatJid)) phone = chatJid.split('@')[0]
    else if (isLidUser(chatJid)) {
      const alt = key.remoteJidAlt && isPnUser(key.remoteJidAlt) ? key.remoteJidAlt : await socket.signalRepository.lidMapping.getPNForLID(chatJid).catch(() => null)
      if (alt) {
        chatJid = jidNormalizedUser(alt)
        phone = chatJid.split('@')[0]
      }
    }

    const text =
      content.conversation ??
      content.extendedTextMessage?.text ??
      content.imageMessage?.caption ??
      content.videoMessage?.caption ??
      content.documentMessage?.caption ??
      content.documentWithCaptionMessage?.message?.documentMessage?.caption ??
      ''

    const files: IncomingFile[] = []
    let unsupported: string | null = null
    const media =
      (content.imageMessage && { kind: 'image', mimetype: content.imageMessage.mimetype, size: content.imageMessage.fileLength, name: null }) ||
      (content.videoMessage && { kind: 'video', mimetype: content.videoMessage.mimetype, size: content.videoMessage.fileLength, name: null }) ||
      (content.audioMessage && { kind: content.audioMessage.ptt ? 'voice message' : 'audio', mimetype: content.audioMessage.mimetype, size: content.audioMessage.fileLength, name: null }) ||
      (content.stickerMessage && { kind: 'sticker', mimetype: content.stickerMessage.mimetype, size: content.stickerMessage.fileLength, name: null }) ||
      (content.documentMessage && { kind: 'document', mimetype: content.documentMessage.mimetype, size: content.documentMessage.fileLength, name: content.documentMessage.fileName }) ||
      (content.documentWithCaptionMessage?.message?.documentMessage && {
        kind: 'document',
        mimetype: content.documentWithCaptionMessage.message.documentMessage.mimetype,
        size: content.documentWithCaptionMessage.message.documentMessage.fileLength,
        name: content.documentWithCaptionMessage.message.documentMessage.fileName,
      }) ||
      null

    if (media) {
      const contentType = (media.mimetype ?? 'application/octet-stream').split(';')[0].trim().toLowerCase()
      if (Number(media.size ?? 0) > this.maxFileBytes) unsupported = `${media.kind} (too large)`
      else {
        try {
          const buffer = await downloadMediaMessage(message, 'buffer', {}, { logger: socket.logger, reuploadRequest: socket.updateMediaMessage })
          files.push({ fileName: media.name || defaultFileName(media.kind, contentType), contentType, content: buffer.toString('base64') })
        } catch (error) {
          this.log.warn({ err: (error as Error).message, messageId: key.id }, 'Could not download a WhatsApp file')
          unsupported = `${media.kind} (could not be downloaded)`
        }
      }
    } else if (!text) {
      // Геопозиция, контакт, опрос и т. п.: оператор увидит, что клиент что-то прислал.
      unsupported = describe(content)
      if (!unsupported) return null
    }

    return {
      messageId: key.id,
      chatJid,
      phone,
      pushName: message.pushName ?? null,
      text,
      files,
      unsupported,
      sentAt: new Date(Number(message.messageTimestamp ?? Date.now() / 1000) * 1000).toISOString(),
    }
  }
}

function defaultFileName(kind: string, contentType: string): string {
  const extension = contentType.split('/')[1]?.split('+')[0].replace('jpeg', 'jpg').replace('mpeg', 'mp3') || 'bin'
  return `${kind.replace(/\s+/g, '-')}.${extension}`
}

function describe(content: NonNullable<ReturnType<typeof normalizeMessageContent>>): string | null {
  if (content.locationMessage || content.liveLocationMessage) return 'location'
  if (content.contactMessage || content.contactsArrayMessage) return 'contact'
  if (content.pollCreationMessage || content.pollCreationMessageV2 || content.pollCreationMessageV3) return 'poll'
  if (content.eventMessage) return 'event'
  return null
}
