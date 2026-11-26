import { Fragment } from 'preact'
import { useEffect, useRef, useState } from 'preact/hooks'
import type { Attachment, LauncherItem, WidgetOptions } from './api'
import type { Controller, ViewMessage } from './controller'
import { textsFor, type Texts } from './texts'

const ChatIcon = ({ dots }: { dots: string }) => (
  <svg width="29" height="29" viewBox="0 0 32 32" aria-hidden="true">
    <path fill="#fff" d="M16 3a12 12 0 0 0-10 19L3 29l8-4A12 12 0 1 0 16 3Z" />
    <g fill={dots}>
      <circle cx="10" cy="15" r="1.5" />
      <circle cx="16" cy="15" r="1.5" />
      <circle cx="22" cy="15" r="1.5" />
    </g>
  </svg>
)
const TelegramIcon = () => (
  <svg width="26" height="26" viewBox="0 0 24 24" fill="#fff" aria-hidden="true">
    <path d="M21.9 4.3c.3-1.2-.9-2.1-2-1.7L2.8 9.2c-1.2.5-1.2 2.2.1 2.6l4.3 1.4 1.7 5.3c.3 1 1.6 1.3 2.3.5l2.4-2.4 4.6 3.4c.9.7 2.2.2 2.5-.9l2.2-13.8zM9.6 14.6l-.5 3.4-1.3-4.3 9.8-6.3-8 7.2z" />
  </svg>
)
const WhatsAppIcon = () => (
  <svg width="26" height="26" viewBox="0 0 24 24" fill="#fff" aria-hidden="true">
    <path d="M12 2a10 10 0 0 0-8.6 15.1L2 22l5-1.3A10 10 0 1 0 12 2zm0 18.2a8.2 8.2 0 0 1-4.2-1.2l-.3-.2-3 .8.8-2.9-.2-.3A8.2 8.2 0 1 1 12 20.2zm4.5-6.1c-.2-.1-1.5-.7-1.7-.8s-.4-.1-.6.1-.7.8-.8 1-.3.2-.5.1a6.7 6.7 0 0 1-3.3-2.9c-.2-.4.2-.4.7-1.3.1-.2 0-.3 0-.4l-.8-1.8c-.2-.5-.4-.4-.6-.4h-.5a1 1 0 0 0-.7.3 3 3 0 0 0-.9 2.2 5.2 5.2 0 0 0 1.1 2.7 11.8 11.8 0 0 0 4.5 4c1.7.7 2.3.8 3.2.6a2.7 2.7 0 0 0 1.8-1.2 2.2 2.2 0 0 0 .1-1.3c0-.1-.2-.2-.5-.3z" />
  </svg>
)
const CrossIcon = () => (
  <svg width="28" height="28" viewBox="0 0 24 24" stroke="#fff" stroke-width="2.5" stroke-linecap="round" aria-hidden="true">
    <path d="M5 5l14 14M19 5L5 19" />
  </svg>
)
const CollapsedIcon = () => (
  <svg width="28" height="28" viewBox="0 0 24 24" fill="none" stroke="#fff" stroke-width="2.2" stroke-linejoin="round" aria-hidden="true">
    <path d="M3 4h18v13H9l-6 4z" />
  </svg>
)
const PersonIcon = () => (
  <svg width="14" height="14" viewBox="0 0 24 24" fill="#fff" aria-hidden="true">
    <circle cx="12" cy="7" r="5" />
    <path d="M2 23v-2q0-6 10-6t10 6v2Z" />
  </svg>
)
const SmileIcon = () => (
  <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.6" aria-hidden="true">
    <circle cx="12" cy="12" r="9" />
    <path d="M8 14q4 5 8 0" />
    <circle cx="9" cy="9" r=".8" fill="currentColor" />
    <circle cx="15" cy="9" r=".8" fill="currentColor" />
  </svg>
)

const ClipIcon = () => (
  <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" aria-hidden="true">
    <path d="M20 11.5 12.4 19a5 5 0 0 1-7.1-7.1l8-8a3.4 3.4 0 0 1 4.8 4.8l-8 8a1.7 1.7 0 0 1-2.4-2.4L15 6.9" />
  </svg>
)
const LinkIcon = () => (
  <svg width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="#fff" stroke-width="2" stroke-linecap="round" aria-hidden="true">
    <path d="M10 14a4 4 0 0 0 5.7 0l3-3a4 4 0 0 0-5.7-5.7l-1 1M14 10a4 4 0 0 0-5.7 0l-3 3a4 4 0 0 0 5.7 5.7l1-1" />
  </svg>
)

const emojis = ['😀', '😊', '👍', '❤️', '👋', '🙏', '😉', '😔', '🎉', '✅']

/** Настройки по умолчанию: для файлов виджета, опубликованных до появления настроек. */
const defaultOptions: WidgetOptions = {
  caption: '',
  allowAttachments: true,
  zIndex: 2147483000,
  hideOnOutsideClick: false,
  visibility: 'Always',
  buttonDelaySeconds: 0,
  buttonColor: null,
  tooltipMode: 'Never',
  tooltipText: '',
  tooltipBackground: '#8fa0ab',
  tooltipColor: '#111111',
  tooltipTextSize: 14,
  sound: 'None',
  operatorName: null,
  items: [{ type: 'Chat', label: 'Online chat' }],
}

export function optionsOf(config: { options?: WidgetOptions | null } | null): WidgetOptions {
  return { ...defaultOptions, ...(config?.options ?? {}) }
}

/**
 * Виджет: справа колонка круглых кнопок (пункты: чат, мессенджеры, ссылки; кнопка свернуть), слева от неё окно чата.
 * Если пункт один — остаётся одна кнопка чата.
 */
export function App({ controller }: { controller: Controller }) {
  const [state, setState] = useState(controller.state)
  useEffect(() => {
    const unsubscribe = controller.subscribe(() => setState(controller.state))
    // Состояние могло измениться до подписки (настройки из файла виджета применяются сразу): берём актуальное.
    setState(controller.state)
    return unsubscribe
  }, [controller])

  const config = state.config
  const options = optionsOf(config)
  // Кнопка появляется через заданное время после открытия страницы (если чат уже открыт — сразу).
  const [delayPassed, setDelayPassed] = useState(false)
  useEffect(() => {
    if (!config) return
    const timer = setTimeout(() => setDelayPassed(true), options.buttonDelaySeconds * 1000)
    return () => clearTimeout(timer)
  }, [config, options.buttonDelaySeconds])

  if (!config || options.visibility === 'Never') return null
  if (!delayPassed && !state.open) return null
  const t = textsFor(config.defaultLocale)
  const items = launcherItems(options, !!config.telegramBot, !!config.whatsApp)
  const chatItem = items.find((i) => i.type === 'Chat')!
  const hasExtra = items.length > 1
  const tooltip = options.tooltipMode !== 'Never' && options.tooltipText && !state.open && (
    <span
      class={`tooltip ${options.tooltipMode === 'Hover' ? 'hover' : ''}`}
      style={{ background: options.tooltipBackground, color: options.tooltipColor, fontSize: `${options.tooltipTextSize}px` }}
    >
      {options.tooltipText}
    </span>
  )
  const chatButton = (
    <div class="item">
      {tooltip}
      <button
        class="round chat"
        aria-label={state.open ? t.close : chatItem.label || t.open}
        title={state.open ? undefined : chatItem.label}
        onClick={() => (state.open ? controller.close() : controller.open())}
      >
        {state.open && !hasExtra ? (
          <CrossIcon />
        ) : config.buttonIconUrl ? (
          <img src={config.buttonIconUrl} alt="" />
        ) : (
          <ChatIcon dots={options.buttonColor ?? config.accentColor} />
        )}
        {!state.open && state.unread > 0 && <span class="badge">{state.unread}</span>}
      </button>
    </div>
  )

  return (
    <div class={config.position}>
      {state.open && <Panel controller={controller} state={state} t={t} options={options} />}
      <div class="launcher">
        {!hasExtra ? (
          chatButton
        ) : (
          <>
            {!state.collapsed && (
              <div class="actions">
                {items.map((item, i) =>
                  item.type === 'Chat' ? (
                    <Fragment key={i}>{chatButton}</Fragment>
                  ) : item.type === 'Telegram' ? (
                    <button key={i} class="round telegram" aria-label={item.label || t.openTelegram} title={item.label || t.openTelegram} onClick={() => controller.open('tg')}>
                      <TelegramIcon />
                    </button>
                  ) : item.type === 'WhatsApp' ? (
                    <button key={i} class="round whatsapp" aria-label={item.label || t.openWhatsApp} title={item.label || t.openWhatsApp} onClick={() => controller.open('whatsapp')}>
                      <WhatsAppIcon />
                    </button>
                  ) : (
                    <a key={i} class="round link" href={item.url ?? '#'} target="_blank" rel="noopener noreferrer" aria-label={item.label} title={item.label}>
                      <LinkIcon />
                    </a>
                  ),
                )}
              </div>
            )}
            <button
              class="round toggle"
              aria-label={state.collapsed ? t.expand : t.collapse}
              onClick={() => controller.setCollapsed(!state.collapsed)}
            >
              {state.collapsed ? <CollapsedIcon /> : <CrossIcon />}
              {state.collapsed && state.unread > 0 && <span class="badge">{state.unread}</span>}
            </button>
          </>
        )}
      </div>
    </div>
  )
}

/** Пункты в порядке из настроек. Telegram — только если бот подключён, WhatsApp — если подключён номер. */
function launcherItems(options: WidgetOptions, hasTelegram: boolean, hasWhatsApp: boolean): LauncherItem[] {
  const items = (options.items ?? []).filter((i) =>
    i.type === 'Telegram' ? hasTelegram : i.type === 'WhatsApp' ? hasWhatsApp : i.type !== 'Link' || !!i.url,
  )
  if (!items.some((i) => i.type === 'Chat')) items.unshift({ type: 'Chat', label: '' })
  if (hasTelegram && !items.some((i) => i.type === 'Telegram')) items.push({ type: 'Telegram', label: '' })
  if (hasWhatsApp && !items.some((i) => i.type === 'WhatsApp')) items.push({ type: 'WhatsApp', label: '' })
  return items
}

function Panel({ controller, state, t, options }: { controller: Controller; state: Controller['state']; t: Texts; options: WidgetOptions }) {
  const config = state.config!
  const [draft, setDraft] = useState('')
  const [files, setFiles] = useState<File[]>([])
  const [fileError, setFileError] = useState<string | null>(null)
  const [dragging, setDragging] = useState(false)
  const [emojiOpen, setEmojiOpen] = useState(false)
  const listRef = useRef<HTMLDivElement>(null)
  const inputRef = useRef<HTMLInputElement>(null)
  const fileRef = useRef<HTMLInputElement>(null)
  const maxBytes = controller.maxAttachmentBytes
  const operatorName = (m: ViewMessage) => options.operatorName || m.authorName || t.operatorFallback

  useEffect(() => {
    listRef.current?.scrollTo({ top: listRef.current.scrollHeight })
  }, [state.messages.length])
  useEffect(() => inputRef.current?.focus(), [])

  const addFiles = (list: FileList | null) => {
    if (!list || maxBytes === 0) return
    const next = [...files, ...Array.from(list)].slice(0, 10)
    const total = next.reduce((sum, f) => sum + f.size, 0)
    if (total > maxBytes) {
      setFileError(t.fileTooLarge.replace('{mb}', String(Math.round(maxBytes / 1024 / 1024))))
      return
    }
    setFileError(null)
    setFiles(next)
  }

  const submit = (e: Event) => {
    e.preventDefault()
    if (!draft.trim() && files.length === 0) return
    void controller.send(draft, files)
    setDraft('')
    setFiles([])
    setFileError(null)
  }

  const dropProps =
    maxBytes > 0
      ? {
          onDragOver: (e: DragEvent) => {
            e.preventDefault()
            setDragging(true)
          },
          onDragLeave: (e: DragEvent) => {
            if (e.currentTarget === e.target) setDragging(false)
          },
          onDrop: (e: DragEvent) => {
            e.preventDefault()
            setDragging(false)
            addFiles(e.dataTransfer?.files ?? null)
          },
        }
      : {}

  return (
    <section class="widget" role="dialog" aria-label={config.title} {...dropProps}>
      <header class="header">
        <span class="title">{config.title}</span>
        <button aria-label={t.close} onClick={() => controller.close()}>
          ×
        </button>
      </header>
      <div class="messages" ref={listRef} aria-live="polite">
        {config.greeting && <Incoming name={options.operatorName || config.title} text={config.greeting} />}
        {state.messages.map((m) => (
          <Message key={m.id} message={m} t={t} name={operatorName(m)} maxMb={Math.round(maxBytes / 1024 / 1024)} onRetry={() => void controller.send(m.text, [], m)} />
        ))}
        {state.connecting && <div class="notice">{t.connecting}</div>}
        {state.error && <div class="error">{t.loadFailed}</div>}
      </div>
      {dragging && <div class="drop">{t.dropFiles}</div>}
      {(files.length > 0 || fileError) && (
        <div class="files">
          {files.map((f, i) => (
            <span class="chip" key={`${f.name}-${i}`}>
              <span>{f.name}</span>
              <button type="button" aria-label={t.removeFile} onClick={() => setFiles(files.filter((_, j) => j !== i))}>
                ×
              </button>
            </span>
          ))}
          {fileError && <span class="file-error">{fileError}</span>}
        </div>
      )}
      <form class="composer" onSubmit={submit}>
        <input
          ref={inputRef}
          type="text"
          placeholder={t.placeholder}
          aria-label={t.placeholder}
          autocomplete="off"
          maxLength={10000}
          value={draft}
          onInput={(e) => setDraft((e.target as HTMLInputElement).value)}
        />
        {maxBytes > 0 && (
          <>
            <button type="button" aria-label={t.attach} title={t.attach} onClick={() => fileRef.current?.click()}>
              <ClipIcon />
            </button>
            <input
              ref={fileRef}
              type="file"
              multiple
              hidden
              onChange={(e) => {
                addFiles((e.target as HTMLInputElement).files)
                ;(e.target as HTMLInputElement).value = ''
              }}
            />
          </>
        )}
        <button type="button" aria-label={t.emoji} onClick={() => setEmojiOpen(!emojiOpen)}>
          <SmileIcon />
        </button>
        {emojiOpen && (
          <div class="emoji">
            {emojis.map((e) => (
              <button
                key={e}
                type="button"
                onClick={() => {
                  setDraft(draft + e)
                  setEmojiOpen(false)
                  inputRef.current?.focus()
                }}
              >
                {e}
              </button>
            ))}
          </div>
        )}
      </form>
      {options.caption && <div class="caption">{options.caption}</div>}
    </section>
  )
}

function Message({ message: m, t, name, maxMb, onRetry }: { message: ViewMessage; t: Texts; name: string; maxMb: number; onRetry: () => void }) {
  if (m.author === 'system') return <div class="system">{m.text}</div>
  if (m.author === 'operator') return <Incoming name={name} text={m.text} at={m.createdAt} attachments={m.attachments} t={t} />

  const reason =
    m.errorCode === 'attachment_too_large' ? t.fileTooLarge.replace('{mb}', String(maxMb))
    : m.errorCode === 'attachment_type_not_allowed' ? t.typeNotAllowed
    : m.errorCode?.startsWith('attachment') ? t.fileRejected
    : null
  return (
    <div class="out">
      <div class="bubble">
        <Attachments items={m.attachments} t={t} />
        {m.text}
        <time>{m.pending === 'sending' ? '…' : formatTime(m.createdAt)}</time>
      </div>
      {m.pending === 'failed' &&
        (reason ? (
          <span class="failed">{reason}</span>
        ) : (
          <span class="failed" onClick={onRetry}>
            {t.sendFailed}
          </span>
        ))}
    </div>
  )
}

function Incoming({ name, text, at, attachments, t }: { name: string; text: string; at?: string; attachments?: Attachment[] | null; t?: Texts }) {
  return (
    <div class="in">
      <div class="avatar">
        <PersonIcon />
      </div>
      <div class="bubble">
        <strong>{name}</strong>
        {t && <Attachments items={attachments} t={t} />}
        {text}
        {at && <time>{formatTime(at)}</time>}
      </div>
    </div>
  )
}

/** Вложения: картинки — превью, остальное — ссылка на скачивание с именем и размером. */
function Attachments({ items, t }: { items?: Attachment[] | null; t: Texts }) {
  if (!items?.length) return null
  return (
    <div class="atts">
      {items.map((a) =>
        a.url && a.contentType.startsWith('image/') ? (
          <a key={a.id} class="att-image" href={a.url} target="_blank" rel="noopener noreferrer">
            <img src={a.url} alt={a.fileName} loading="lazy" />
          </a>
        ) : (
          <a key={a.id} class="att" href={a.url ?? undefined} target="_blank" rel="noopener noreferrer" download={a.fileName}>
            <ClipIcon />
            <span class="att-name">{a.fileName || t.file}</span>
            <span class="att-size">{formatSize(a.size)}</span>
          </a>
        ),
      )}
    </div>
  )
}

function formatSize(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 * 1024) return `${Math.round(bytes / 1024)} KB`
  return `${(bytes / 1024 / 1024).toFixed(1)} MB`
}

function formatTime(iso: string): string {
  return new Date(iso).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })
}
