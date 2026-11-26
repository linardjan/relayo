import { HttpTransportType, HubConnection, HubConnectionBuilder, LogLevel } from '@microsoft/signalr'
import { Api, WidgetApiError, type Message, type Session, type WidgetConfig } from './api'

/** Ссылку обновляем заранее, пока до конца её жизни (10 минут) остаётся больше этого. */
const LINK_REFRESH_BEFORE_MS = 3 * 60_000
const LINK_CHECK_INTERVAL_MS = 60_000
/** Ссылки на файлы временные (S3_PRESIGNED_URL_MINUTES): пока окно открыто, история обновляется заранее. */
const HISTORY_REFRESH_MS = 10 * 60_000

export interface ViewMessage extends Message {
  /** Сообщение отправляется или не отправилось. */
  pending?: 'sending' | 'failed'
  /** Почему не отправилось: код ошибки сервера (attachment_too_large и т. п.). */
  errorCode?: string
}

export interface State {
  config: WidgetConfig | null
  open: boolean
  /** Кнопки чата и мессенджеров свёрнуты в одну. */
  collapsed: boolean
  messages: ViewMessage[]
  unread: number
  connecting: boolean
  error: string | null
}

type Identity =
  | { kind: 'token'; token: string }
  | { kind: 'direct'; clientId: string }

type EventName = 'ready' | 'open' | 'close' | 'message' | 'unreadChanged'

/** Куда открыть: без аргумента — окно чата, иначе мессенджер. */
export type OpenTarget = 'chat' | 'tg' | 'telegram' | 'whatsapp' | 'wa'

type MessengerKind = 'tg' | 'wa'

/**
 * Состояние и поведение виджета. UI подписывается на изменения через subscribe().
 *
 * Посетитель на сервере создаётся лениво: только когда чат открыли или в браузере уже
 * есть токен прошлой переписки. Поэтому identify()/setClientId() на каждой странице сайта
 * не создают лишних записей: идентификация применяется, когда появляется сессия.
 */
export class Controller {
  state: State = { config: null, open: false, collapsed: false, messages: [], unread: 0, connecting: false, error: null }

  private readonly api: Api
  private readonly storageKey: string
  private readonly listeners = new Set<() => void>()
  private readonly handlers = new Map<EventName, Set<(payload?: unknown) => void>>()
  private visitorToken: string | null
  private sessionPromise: Promise<void> | null = null
  private hub: HubConnection | null = null
  private identity: Identity | null = null
  private appliedIdentity: string | null = null
  private product: string | null = null
  private locale: string | null = null
  /** Заранее полученные одноразовые ссылки в мессенджеры. */
  private links: Record<MessengerKind, { url: string; expiresAt: number } | null> = { tg: null, wa: null }
  private linkPromises: Record<MessengerKind, Promise<string> | null> = { tg: null, wa: null }
  private linkTimer: ReturnType<typeof setInterval> | null = null
  private historyTimer: ReturnType<typeof setInterval> | null = null
  /** Файлы неотправленных сообщений: нужны для повторной отправки. */
  private readonly pendingFiles = new Map<string, File[]>()

  constructor(
    private readonly baseUrl: string,
    private readonly widgetId: string,
    /** Настройки из опубликованного файла виджета: тогда запрос /config не нужен. */
    private readonly publishedConfig: WidgetConfig | null = null,
  ) {
    this.api = new Api(baseUrl, widgetId)
    this.storageKey = `relayo:${widgetId}:visitor`
    this.visitorToken = readStorage(this.storageKey)
  }

  async init(): Promise<void> {
    try {
      const config = this.publishedConfig ?? (await this.api.config())
      this.set({ config })
      this.emit('ready')
      // Уже была переписка в этом браузере: подключаемся сразу, чтобы видеть новые ответы.
      if (this.visitorToken) await this.ensureSession()
    } catch (error) {
      console.error('[Relayo] Widget failed to load', error)
    }
  }

  subscribe(listener: () => void): () => void {
    this.listeners.add(listener)
    return () => this.listeners.delete(listener)
  }

  // ---- Публичные методы Relayo.* ----

  /**
   * Relayo.open() — окно чата; Relayo.open('tg') — Telegram; Relayo.open('whatsapp') — WhatsApp.
   * Telegram открывается с одноразовой ссылкой, и переписка продолжится в том же чате;
   * WhatsApp — чат с номером канала (wa.me/<номер>).
   */
  open(target?: OpenTarget): void {
    const where = typeof target === 'string' ? target.toLowerCase() : 'chat'
    if (where === 'tg' || where === 'telegram') return this.openTelegram()
    if (where === 'whatsapp' || where === 'wa') return this.openWhatsApp()
    if (where !== 'chat') {
      console.warn(`[Relayo] Unknown open target "${String(target)}". Use Relayo.open(), Relayo.open('tg') or Relayo.open('whatsapp').`)
      return
    }
    this.set({ open: true, collapsed: false, unread: 0 })
    this.emit('open')
    this.emit('unreadChanged', 0)
    void this.ensureSession()
  }

  close(): void {
    this.set({ open: false })
    this.emit('close')
  }

  /** Свернуть кнопки в одну (заодно закрывается окно чата) или развернуть обратно. */
  setCollapsed(collapsed: boolean): void {
    if (collapsed && this.state.open) this.close()
    this.set({ collapsed })
  }

  identify(token: string): void {
    if (this.state.config && this.state.config.identificationMode !== 'signed_token') {
      console.warn('[Relayo] identify() is not available: this widget uses direct clientId. Use setClientId().')
      return
    }
    if (typeof token !== 'string' || !token) return
    this.identity = { kind: 'token', token }
    void this.applyIdentity()
  }

  setClientId(clientId: string): void {
    if (this.state.config && this.state.config.identificationMode !== 'direct') {
      console.warn('[Relayo] setClientId() is not available: this widget accepts only signed tokens. Use identify(token).')
      return
    }
    if (clientId === undefined || clientId === null || String(clientId) === '') return
    this.identity = { kind: 'direct', clientId: String(clientId) }
    void this.applyIdentity()
  }

  setProduct(product: string): void {
    if (this.state.config && this.state.config.identificationMode !== 'direct') {
      console.warn('[Relayo] setProduct() is not available: this widget accepts only signed tokens.')
      return
    }
    this.product = product ? String(product) : null
    if (this.identity?.kind === 'direct') void this.applyIdentity()
  }

  setLocale(locale: string): void {
    this.locale = locale ? String(locale) : null
  }

  /** Пользователь вышел с сайта: забываем посетителя, чтобы следующий человек не увидел переписку. */
  logout(): void {
    removeStorage(this.storageKey)
    this.visitorToken = null
    this.identity = null
    this.appliedIdentity = null
    this.sessionPromise = null
    this.links = { tg: null, wa: null }
    this.linkPromises = { tg: null, wa: null }
    this.pendingFiles.clear()
    void this.hub?.stop()
    this.hub = null
    this.set({ messages: [], unread: 0, error: null })
  }

  on(event: EventName, handler: (payload?: unknown) => void): void {
    if (!this.handlers.has(event)) this.handlers.set(event, new Set())
    this.handlers.get(event)!.add(handler)
    if (event === 'ready' && this.state.config) handler()
  }

  /**
   * Открывает бота в Telegram с одноразовой ссылкой: переписка продолжится в том же чате.
   */
  private openTelegram(): void {
    const bot = this.state.config?.telegramBot
    if (!bot) {
      console.warn('[Relayo] Telegram is not connected to this widget.')
      return
    }
    this.openMessenger('tg', `https://t.me/${encodeURIComponent(bot)}`)
  }

  /**
   * Открывает в WhatsApp чат с номером канала. Если номер подключён к Relayo по QR, в поле ввода подставляется
   * код перехода: по нему первое сообщение попадёт в эту же переписку.
   */
  private openWhatsApp(): void {
    const config = this.state.config
    if (!config?.whatsApp) {
      console.warn('[Relayo] WhatsApp is not connected to this widget.')
      return
    }
    const plain = `https://wa.me/${encodeURIComponent(config.whatsApp)}`
    if (!config.whatsAppCode) return openInNewTab(plain)
    this.openMessenger('wa', plain)
  }

  /**
   * Переход в мессенджер по одноразовой ссылке. Ссылка запрашивается заранее, потому что браузеры
   * (особенно Safari на iOS) блокируют переход, если перед ним по клику шёл сетевой запрос.
   * Без ссылки человек всё равно попадёт в мессенджер (fallback), только переписка начнётся заново.
   */
  private openMessenger(kind: MessengerKind, fallback: string): void {
    const ready = this.links[kind]
    if (ready && ready.expiresAt - Date.now() > 30_000) {
      openInNewTab(ready.url)
      // Ссылка одноразовая: сразу готовим следующую.
      this.links[kind] = null
      void this.refreshLink(kind).catch(() => undefined)
      return
    }

    // Ссылки ещё нет (виджет не открывали): окно открываем сразу, пока браузер считает это действием
    // пользователя, а адрес подставляем, когда ссылка будет готова.
    const tab = window.open('', '_blank')
    if (tab) tab.opener = null
    const go = (url: string) => (tab ? (tab.location.href = url) : (window.location.href = url))
    this.ensureSession()
      .then(() => this.refreshLink(kind))
      .then((url) => {
        this.links[kind] = null
        go(url)
        void this.refreshLink(kind).catch(() => undefined)
      })
      .catch((error) => {
        console.warn(`[Relayo] Could not create a ${kind === 'tg' ? 'Telegram' : 'WhatsApp'} link, opening without it`, error)
        go(fallback)
      })
  }

  // ---- Переписка ----

  /** Можно ли прикладывать файлы и сколько всего байт в одном сообщении. */
  get maxAttachmentBytes(): number {
    const config = this.state.config
    if (!config || config.options?.allowAttachments === false) return 0
    // В файлах виджета, опубликованных до появления вложений, лимита нет: берём обычный, сервер всё равно проверит.
    return config.maxAttachmentBytes ?? 20 * 1024 * 1024
  }

  async send(text: string, files: File[] = [], retryOf?: ViewMessage): Promise<void> {
    text = text.trim()
    const clientMessageId = retryOf?.clientMessageId ?? randomId()
    if (retryOf) files = this.pendingFiles.get(clientMessageId) ?? []
    if (!text && files.length === 0) return
    const optimistic: ViewMessage = retryOf
      ? { ...retryOf, pending: 'sending', errorCode: undefined }
      : {
          id: `local-${clientMessageId}`,
          chatId: '',
          author: 'client',
          authorName: null,
          text,
          createdAt: new Date().toISOString(),
          clientMessageId,
          attachments: files.map((f, i) => ({ id: `local-${i}`, fileName: f.name, contentType: f.type, size: f.size, url: null })),
          pending: 'sending',
        }
    if (files.length > 0) this.pendingFiles.set(clientMessageId, files)
    this.upsertLocal(optimistic)

    try {
      await this.ensureSession()
      const saved = await this.api.send(this.visitorToken!, text, clientMessageId, files)
      this.pendingFiles.delete(clientMessageId)
      this.replaceLocal(clientMessageId, saved)
    } catch (error) {
      console.error('[Relayo] Message was not sent', error)
      this.upsertLocal({ ...optimistic, pending: 'failed', errorCode: error instanceof WidgetApiError ? error.code : undefined })
    }
  }

  private ensureSession(): Promise<void> {
    this.sessionPromise ??= this.startSession().catch((error) => {
      this.sessionPromise = null
      this.set({ connecting: false, error: 'load' })
      throw error
    })
    return this.sessionPromise
  }

  private async startSession(): Promise<void> {
    this.set({ connecting: true, error: null })
    const session = await this.api.session(this.visitorToken)
    this.acceptSession(session)
    await this.applyIdentity(true)
    await this.connectHub()
    this.set({ connecting: false })
    this.startLinkRefresh()
    this.historyTimer ??= setInterval(() => {
      if (this.state.open && document.visibilityState === 'visible') void this.refreshHistory()
    }, HISTORY_REFRESH_MS)
  }

  /** Мессенджеры, в которые виджет переходит по одноразовой ссылке. */
  private linkKinds(): MessengerKind[] {
    const config = this.state.config
    return [...(config?.telegramBot ? (['tg'] as const) : []), ...(config?.whatsApp && config.whatsAppCode ? (['wa'] as const) : [])]
  }

  /** Держит наготове свежие ссылки в мессенджеры, пока вкладка видна. */
  private startLinkRefresh(): void {
    const kinds = this.linkKinds()
    if (kinds.length === 0 || this.linkTimer) return
    for (const kind of kinds) void this.refreshLink(kind).catch(() => undefined)
    this.linkTimer = setInterval(() => {
      if (document.visibilityState !== 'visible' || !this.visitorToken) return
      for (const kind of kinds) {
        const link = this.links[kind]
        if (!link || link.expiresAt - Date.now() < LINK_REFRESH_BEFORE_MS) void this.refreshLink(kind).catch(() => undefined)
      }
    }, LINK_CHECK_INTERVAL_MS)
  }

  private refreshLink(kind: MessengerKind): Promise<string> {
    if (!this.visitorToken) return Promise.reject(new Error('No visitor session'))
    this.linkPromises[kind] ??= (kind === 'tg' ? this.api.telegramLink(this.visitorToken) : this.api.whatsAppLink(this.visitorToken))
      .then((link) => {
        this.links[kind] = { url: link.url, expiresAt: Date.parse(link.expiresAt) }
        return link.url
      })
      .finally(() => {
        this.linkPromises[kind] = null
      })
    return this.linkPromises[kind]
  }

  /** Применяет identify()/setClientId(), если есть сессия; иначе это сделает startSession(). */
  private async applyIdentity(fromSession = false): Promise<void> {
    const identity = this.identity
    if (!identity || !this.visitorToken || (!fromSession && !this.sessionPromise)) return
    const key = identity.kind === 'token' ? `t:${identity.token}` : `d:${identity.clientId}:${this.product ?? ''}`
    if (key === this.appliedIdentity) return
    this.appliedIdentity = key

    try {
      const session =
        identity.kind === 'token'
          ? await this.api.identify(this.visitorToken, identity.token)
          : await this.api.identifyDirect(this.visitorToken, identity.clientId, this.product, this.locale)
      this.acceptSession(session)
      // Сервер мог выдать нового посетителя, а подтверждённая сессия попадает в группу клиента:
      // в обоих случаях realtime нужно переподключить.
      if (this.hub) {
        await this.hub.stop()
        this.hub = null
        await this.connectHub()
      }
    } catch (error) {
      this.appliedIdentity = null
      if (error instanceof WidgetApiError && (error.status === 401 || error.status === 403)) {
        console.warn(`[Relayo] Identification rejected: ${error.code ?? error.message}`)
      } else {
        console.error('[Relayo] Identification failed', error)
      }
    }
  }

  private acceptSession(session: Session): void {
    this.visitorToken = session.visitorToken
    writeStorage(this.storageKey, session.visitorToken)
    const pending = this.state.messages.filter((m) => m.pending)
    this.set({ messages: mergeMessages(session.messages, pending) })
  }

  private async connectHub(): Promise<void> {
    if (this.hub || !this.visitorToken) return
    const hub = new HubConnectionBuilder()
      .withUrl(`${this.baseUrl}/hubs/widget?widgetId=${encodeURIComponent(this.widgetId)}`, {
        // Только WebSocket и без negotiate: не нужны sticky sessions и cookie.
        transport: HttpTransportType.WebSockets,
        skipNegotiation: true,
        accessTokenFactory: () => this.visitorToken ?? '',
      })
      .withAutomaticReconnect()
      .configureLogging(LogLevel.Warning)
      .build()

    hub.on('message', (message: Message) => this.onIncoming(message))
    // После обрыва связи подтягиваем то, что пришло, пока были отключены.
    hub.onreconnected(() => void this.refreshHistory())
    this.hub = hub
    try {
      await hub.start()
    } catch (error) {
      console.warn('[Relayo] Realtime connection failed, messages will appear after reload', error)
    }
  }

  private async refreshHistory(): Promise<void> {
    if (!this.visitorToken) return
    try {
      this.acceptSession(await this.api.session(this.visitorToken))
    } catch {
      // повторим при следующем переподключении
    }
  }

  private onIncoming(message: Message): void {
    const known = this.state.messages.some(
      (m) => m.id === message.id || (message.clientMessageId && m.clientMessageId === message.clientMessageId),
    )
    if (known) {
      if (message.clientMessageId) this.replaceLocal(message.clientMessageId, message)
      return
    }
    this.set({ messages: [...this.state.messages, message] })
    this.emit('message', message)
    if (message.author === 'operator' && this.state.config?.options?.sound === 'Beep') beep()
    if (!this.state.open && message.author !== 'client') {
      this.set({ unread: this.state.unread + 1 })
      this.emit('unreadChanged', this.state.unread)
    }
  }

  private upsertLocal(message: ViewMessage): void {
    const others = this.state.messages.filter((m) => m.clientMessageId !== message.clientMessageId || !m.pending)
    this.set({ messages: [...others, message] })
  }

  private replaceLocal(clientMessageId: string, saved: Message): void {
    const without = this.state.messages.filter(
      (m) => m.clientMessageId !== clientMessageId && m.id !== saved.id,
    )
    this.set({ messages: [...without, saved].sort(byTime) })
  }

  private set(patch: Partial<State>): void {
    this.state = { ...this.state, ...patch }
    this.listeners.forEach((l) => l())
  }

  private emit(event: EventName, payload?: unknown): void {
    this.handlers.get(event)?.forEach((h) => {
      try {
        h(payload)
      } catch (error) {
        console.error(`[Relayo] Error in "${event}" handler`, error)
      }
    })
  }
}

/** Короткий сигнал о новом сообщении, без загрузки звуковых файлов. */
function beep(): void {
  try {
    const audio = new AudioContext()
    const oscillator = audio.createOscillator()
    const gain = audio.createGain()
    oscillator.connect(gain)
    gain.connect(audio.destination)
    gain.gain.value = 0.08
    oscillator.frequency.value = 660
    oscillator.start()
    oscillator.stop(audio.currentTime + 0.18)
    oscillator.onended = () => void audio.close()
  } catch {
    // браузер не дал проиграть звук без действия пользователя
  }
}

function mergeMessages(server: Message[], pending: ViewMessage[]): ViewMessage[] {
  const ids = new Set(server.map((m) => m.clientMessageId).filter(Boolean))
  return [...server, ...pending.filter((m) => !ids.has(m.clientMessageId))].sort(byTime)
}

function byTime(a: Message, b: Message): number {
  return a.createdAt.localeCompare(b.createdAt)
}

function openInNewTab(url: string): void {
  const tab = window.open(url, '_blank')
  if (tab) tab.opener = null
  else window.location.href = url
}

function randomId(): string {
  return crypto.randomUUID ? crypto.randomUUID() : `${Date.now()}-${Math.random().toString(36).slice(2)}`
}

// localStorage может быть недоступен (приватный режим, запрет cookie): тогда работаем без сохранения.
function readStorage(key: string): string | null {
  try {
    return localStorage.getItem(key)
  } catch {
    return null
  }
}

function writeStorage(key: string, value: string): void {
  try {
    localStorage.setItem(key, value)
  } catch {
    // без сохранения
  }
}

function removeStorage(key: string): void {
  try {
    localStorage.removeItem(key)
  } catch {
    // нечего удалять
  }
}
