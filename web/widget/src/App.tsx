import { useEffect, useRef, useState } from 'preact/hooks'
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

const emojis = ['😀', '😊', '👍', '❤️', '👋', '🙏', '😉', '😔', '🎉', '✅']

/**
 * Виджет: справа колонка круглых кнопок (чат, мессенджеры, свернуть), слева от неё окно чата.
 * Если мессенджеров нет, остаётся одна кнопка чата.
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
  if (!config) return null
  const t = textsFor(config.defaultLocale)
  const hasMessengers = !!config.telegramBot
  const chatButton = (
    <button
      class="round chat"
      aria-label={state.open ? t.close : t.open}
      onClick={() => (state.open ? controller.close() : controller.open())}
    >
      {state.open && !hasMessengers ? <CrossIcon /> : <ChatIcon dots={config.accentColor} />}
      {!state.open && state.unread > 0 && <span class="badge">{state.unread}</span>}
    </button>
  )

  return (
    <div class={config.position}>
      {state.open && <Panel controller={controller} state={state} t={t} />}
      <div class="launcher">
        {!hasMessengers ? (
          chatButton
        ) : (
          <>
            {!state.collapsed && (
              <div class="actions">
                {chatButton}
                {config.telegramBot && (
                  <button class="round telegram" aria-label={t.openTelegram} title={t.openTelegram} onClick={() => controller.open('tg')}>
                    <TelegramIcon />
                  </button>
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

function Panel({ controller, state, t }: { controller: Controller; state: Controller['state']; t: Texts }) {
  const config = state.config!
  const [draft, setDraft] = useState('')
  const [emojiOpen, setEmojiOpen] = useState(false)
  const listRef = useRef<HTMLDivElement>(null)
  const inputRef = useRef<HTMLInputElement>(null)

  useEffect(() => {
    listRef.current?.scrollTo({ top: listRef.current.scrollHeight })
  }, [state.messages.length])
  useEffect(() => inputRef.current?.focus(), [])

  const submit = (e: Event) => {
    e.preventDefault()
    if (!draft.trim()) return
    void controller.send(draft)
    setDraft('')
  }

  return (
    <section class="widget" role="dialog" aria-label={config.title}>
      <header class="header">
        <span class="title">{config.title}</span>
        <button aria-label={t.close} onClick={() => controller.close()}>
          ×
        </button>
      </header>
      <div class="messages" ref={listRef} aria-live="polite">
        {config.greeting && <Incoming name={config.title} text={config.greeting} />}
        {state.messages.map((m) => (
          <Message key={m.id} message={m} t={t} onRetry={() => void controller.send(m.text, m)} />
        ))}
        {state.connecting && <div class="notice">{t.connecting}</div>}
        {state.error && <div class="error">{t.loadFailed}</div>}
      </div>
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
    </section>
  )
}

function Message({ message: m, t, onRetry }: { message: ViewMessage; t: Texts; onRetry: () => void }) {
  if (m.author === 'system') return <div class="system">{m.text}</div>
  if (m.author === 'operator') return <Incoming name={m.authorName ?? t.operatorFallback} text={m.text} at={m.createdAt} />

  return (
    <div class="out">
      <div class="bubble">
        {m.text}
        <time>{m.pending === 'sending' ? '…' : formatTime(m.createdAt)}</time>
      </div>
      {m.pending === 'failed' && (
        <span class="failed" onClick={onRetry}>
          {t.sendFailed}
        </span>
      )}
    </div>
  )
}

function Incoming({ name, text, at }: { name: string; text: string; at?: string }) {
  return (
    <div class="in">
      <div class="avatar">
        <PersonIcon />
      </div>
      <div class="bubble">
        <strong>{name}</strong>
        {text}
        {at && <time>{formatTime(at)}</time>}
      </div>
    </div>
  )
}

function formatTime(iso: string): string {
  return new Date(iso).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })
}
