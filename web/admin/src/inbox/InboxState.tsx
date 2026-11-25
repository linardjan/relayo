import { useQuery } from '@tanstack/react-query'
import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import { useLocation } from 'react-router'
import { ticketsApi, type TicketSummary } from '../api/tickets'
import { useAuth } from '../auth/AuthContext'
import { useOperatorHub } from './useOperatorHub'

const BASE_TITLE = 'Relayo'
const MAX_SEEN = 1000

interface InboxState {
  /** Тикеты с сообщением клиента, которое оператор ещё не видел. */
  isUnread: (ticket: TicketSummary) => boolean
  /** Сколько непросмотренных в моих и неназначенных — это число в заголовке вкладки. */
  unreadCount: number
  markSeen: (ticketId: string, lastMessageAt: string) => void
  muted: boolean
  setMuted: (muted: boolean) => void
}

const Context = createContext<InboxState | null>(null)

export function useInboxState(): InboxState {
  const state = useContext(Context)
  if (!state) throw new Error('useInboxState must be used inside InboxStateProvider')
  return state
}

/**
 * Новые обращения во всей админке: счётчик во вкладке браузера «(2) Chats · Relayo», звук и подсветка
 * в списке, пока оператор не открыл тикет. Что просмотрено, хранится в браузере оператора.
 */
export function InboxStateProvider({ children }: { children: ReactNode }) {
  const { user } = useAuth()
  const location = useLocation()
  const openTicketId = /^\/tickets\/([^/]+)/.exec(location.pathname)?.[1]
  const tickets = useQuery({ queryKey: ['tickets', false], queryFn: () => ticketsApi.list(false), enabled: !!user })

  const seenKey = `relayo:seen:${user?.id}`
  const [seen, setSeen] = useState<Record<string, string> | null>(() => read(seenKey))
  const [muted, setMutedState] = useState(() => localStorage.getItem('relayo:sound-muted') === '1')

  // Первый запуск: всё, что уже есть, считаем просмотренным, иначе «новыми» оказались бы все старые тикеты.
  useEffect(() => {
    if (seen === null && tickets.data) {
      const baseline = Object.fromEntries(tickets.data.map((t) => [t.id, t.lastMessageAt]))
      write(seenKey, baseline)
      setSeen(baseline)
    }
  }, [seen, tickets.data, seenKey])

  const markSeen = useCallback(
    (ticketId: string, lastMessageAt: string) =>
      setSeen((old) => {
        if (old?.[ticketId] && old[ticketId]! >= lastMessageAt) return old
        const next = { ...(old ?? {}), [ticketId]: lastMessageAt }
        write(seenKey, next)
        return next
      }),
    [seenKey],
  )

  const isUnread = useCallback(
    (t: TicketSummary) =>
      // Клиент ждёт ответа (последним написал он) и оператор этого ещё не видел.
      (t.status === 'New' || t.status === 'InProgress') &&
      t.id !== openTicketId &&
      seen !== null &&
      !(seen[t.id] && seen[t.id]! >= t.lastMessageAt),
    [seen, openTicketId],
  )

  const relevant = useCallback((t: TicketSummary) => t.assigneeId === null || t.assigneeId === user?.id, [user?.id])
  const unreadCount = useMemo(
    () => (tickets.data ?? []).filter((t) => relevant(t) && isUnread(t)).length,
    [tickets.data, relevant, isUnread],
  )

  useEffect(() => {
    document.title = unreadCount > 0 ? `(${unreadCount}) Chats · ${BASE_TITLE}` : BASE_TITLE
  }, [unreadCount])
  useEffect(() => () => void (document.title = BASE_TITLE), [])

  // Звук: новое сообщение клиента в моём или неназначенном тикете, который сейчас не открыт.
  const sound = useRef({ muted, openTicketId, relevant })
  sound.current = { muted, openTicketId, relevant }
  useOperatorHub((previous, next) => {
    const fromClient = (next.status === 'New' || next.status === 'InProgress') && (!previous || next.lastMessageAt > previous.lastMessageAt)
    const { muted, openTicketId, relevant } = sound.current
    if (fromClient && !muted && next.id !== openTicketId && relevant(next)) playChime()
  })

  const setMuted = useCallback((value: boolean) => {
    localStorage.setItem('relayo:sound-muted', value ? '1' : '0')
    setMutedState(value)
    if (!value) playChime()
  }, [])

  return (
    <Context.Provider value={{ isUnread, unreadCount, markSeen, muted, setMuted }}>{children}</Context.Provider>
  )
}

function read(key: string): Record<string, string> | null {
  try {
    const raw = localStorage.getItem(key)
    return raw ? (JSON.parse(raw) as Record<string, string>) : null
  } catch {
    return null
  }
}

function write(key: string, value: Record<string, string>) {
  // Храним только последние записи: иначе список рос бы бесконечно.
  const entries = Object.entries(value)
  const trimmed = entries.length > MAX_SEEN ? Object.fromEntries(entries.sort((a, b) => b[1].localeCompare(a[1])).slice(0, MAX_SEEN)) : value
  try {
    localStorage.setItem(key, JSON.stringify(trimmed))
  } catch {
    // нет места в хранилище: подсветка просто не запомнится
  }
}

let audio: AudioContext | null = null

// Браузер разрешает звук только после действия пользователя: готовим его при первом клике.
if (typeof window !== 'undefined') {
  window.addEventListener('pointerdown', () => {
    audio ??= new AudioContext()
    void audio.resume()
  }, { once: true })
}

/** Короткий двухтоновый сигнал без файлов. */
function playChime() {
  if (!audio || audio.state !== 'running') return
  const now = audio.currentTime
  for (const [freq, start] of [[880, 0], [1320, 0.12]] as const) {
    const osc = audio.createOscillator()
    const gain = audio.createGain()
    osc.type = 'sine'
    osc.frequency.value = freq
    gain.gain.setValueAtTime(0.0001, now + start)
    gain.gain.exponentialRampToValueAtTime(0.2, now + start + 0.02)
    gain.gain.exponentialRampToValueAtTime(0.0001, now + start + 0.25)
    osc.connect(gain).connect(audio.destination)
    osc.start(now + start)
    osc.stop(now + start + 0.3)
  }
}
