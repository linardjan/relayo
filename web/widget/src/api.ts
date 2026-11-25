// HTTP API публичного сервиса для виджета. Токен посетителя передаётся в заголовке X-Relayo-Visitor.

export interface WidgetConfig {
  widgetId: string
  title: string
  greeting: string
  accentColor: string
  position: 'right' | 'left'
  identificationMode: 'signed_token' | 'direct'
  defaultLocale: string
  historyDays: number
  /** Имя Telegram-бота без @, если он подключён к виджету. */
  telegramBot: string | null
  /** Внешний вид и поведение (страница канала в админке). У старых опубликованных файлов их нет. */
  options?: WidgetOptions | null
  /** Своя картинка кнопки чата. */
  buttonIconUrl?: string | null
  /** Суммарный размер файлов одного сообщения; 0 — файлы прикладывать нельзя. */
  maxAttachmentBytes?: number
}

export interface LauncherItem {
  type: 'Chat' | 'Telegram' | 'Link'
  label: string
  url?: string | null
}

export interface WidgetOptions {
  caption: string
  allowAttachments: boolean
  zIndex: number
  hideOnOutsideClick: boolean
  visibility: 'Always' | 'Never'
  buttonDelaySeconds: number
  buttonColor: string | null
  tooltipMode: 'Never' | 'Always' | 'Hover'
  tooltipText: string
  tooltipBackground: string
  tooltipColor: string
  tooltipTextSize: number
  sound: 'None' | 'Beep'
  operatorName: string | null
  items: LauncherItem[]
}

export interface Attachment {
  id: string
  fileName: string
  contentType: string
  size: number
  /** Временная ссылка; обновляется вместе с историей. */
  url: string | null
}

export interface MessengerLink {
  url: string
  expiresAt: string
}

export interface Message {
  id: string
  chatId: string
  author: 'client' | 'operator' | 'system'
  authorName: string | null
  text: string
  createdAt: string
  clientMessageId: string | null
  attachments?: Attachment[] | null
}

export interface Session {
  visitorToken: string
  verified: boolean
  messages: Message[]
}

export class WidgetApiError extends Error {
  readonly status: number
  readonly code: string | undefined

  constructor(status: number, code: string | undefined, message: string) {
    super(message)
    this.status = status
    this.code = code
  }
}

export class Api {
  private readonly base: string

  constructor(baseUrl: string, widgetId: string) {
    this.base = `${baseUrl}/api/widget/${encodeURIComponent(widgetId)}`
  }

  config(): Promise<WidgetConfig> {
    return this.request('GET', '/config')
  }

  session(visitorToken: string | null): Promise<Session> {
    return this.request('POST', '/session', undefined, visitorToken)
  }

  identify(visitorToken: string, token: string): Promise<Session> {
    return this.request('POST', '/identify', { token }, visitorToken)
  }

  identifyDirect(visitorToken: string, clientId: string, product: string | null, locale: string | null): Promise<Session> {
    return this.request('POST', '/identify-direct', { clientId, product, locale }, visitorToken)
  }

  telegramLink(visitorToken: string): Promise<MessengerLink> {
    return this.request('POST', '/telegram-link', undefined, visitorToken)
  }

  send(visitorToken: string, text: string, clientMessageId: string, files: File[] = []): Promise<Message> {
    if (files.length === 0) return this.request('POST', '/messages', { text, clientMessageId }, visitorToken)
    const form = new FormData()
    form.append('text', text)
    form.append('clientMessageId', clientMessageId)
    for (const file of files) form.append('files', file, file.name)
    return this.request('POST', '/messages', form, visitorToken)
  }

  private async request<T>(method: string, path: string, body?: unknown, visitorToken?: string | null): Promise<T> {
    const headers: Record<string, string> = {}
    const isForm = body instanceof FormData
    // Для FormData заголовок с границей частей браузер ставит сам.
    if (body !== undefined && !isForm) headers['content-type'] = 'application/json'
    if (visitorToken) headers['x-relayo-visitor'] = visitorToken
    const response = await fetch(this.base + path, {
      method,
      headers,
      body: body === undefined ? undefined : isForm ? body : JSON.stringify(body),
      credentials: 'omit',
    })
    if (!response.ok) {
      let code: string | undefined
      let title = response.statusText
      try {
        const problem = await response.json()
        code = problem.code
        title = problem.title ?? title
      } catch {
        // тело не JSON
      }
      throw new WidgetApiError(response.status, code, title)
    }
    return (await response.json()) as T
  }
}
