import { Avatar, Tooltip } from 'antd'
import { MessageOutlined, SendOutlined, WhatsAppOutlined } from '@ant-design/icons'
import { useEffect, useState } from 'react'
import { useTranslation } from 'react-i18next'
import type { Channel } from '../api/tickets'

const palette = ['#4a90d9', '#7cb342', '#e5a33d', '#5c6bc0', '#8e5bd0', '#d9604a', '#26a69a', '#ec407a']

/** Инициалы: первые буквы двух первых слов («Ivan Testov» → «IT»). */
export function initials(label: string): string {
  const words = label.replace(/[@()]/g, ' ').split(/\s+/).filter(Boolean)
  return (words.slice(0, 2).map((w) => w[0]).join('') || '?').toUpperCase()
}

/** Цвет аватара постоянный для одного и того же имени: оператор быстрее узнаёт клиента в списке. */
function colorFor(seed: string): string {
  let hash = 0
  for (let i = 0; i < seed.length; i++) hash = (hash * 31 + seed.charCodeAt(i)) | 0
  return palette[Math.abs(hash) % palette.length]!
}

/** Аватар с инициалами; если есть картинка (аватар клиента), показывается она. */
export function PersonAvatar({ name, seed, size = 36, src }: { name: string; seed?: string; size?: number; src?: string | null }) {
  return (
    <Avatar size={size} src={src ?? undefined} style={{ background: src ? undefined : colorFor(seed ?? name), flexShrink: 0, fontSize: size * 0.4 }}>
      {initials(name)}
    </Avatar>
  )
}

const channelIcon: Record<Channel, { icon: React.ReactNode; color: string }> = {
  Widget: { icon: <MessageOutlined />, color: '#e5a33d' },
  Telegram: { icon: <SendOutlined />, color: '#229ed9' },
  WhatsApp: { icon: <WhatsAppOutlined />, color: '#25d366' },
}

export function ChannelIcon({ channel }: { channel: Channel }) {
  const { t } = useTranslation()
  const { icon, color } = channelIcon[channel]
  return (
    <Tooltip title={t(`inbox.channel.${channel}`)}>
      <span style={{ color, fontSize: 13, lineHeight: 1 }}>{icon}</span>
    </Tooltip>
  )
}

/** Текущее время, обновляется раз в минуту: для «сколько ждёт клиент». */
export function useNow(intervalMs = 60_000): number {
  const [now, setNow] = useState(() => Date.now())
  useEffect(() => {
    const id = setInterval(() => setNow(Date.now()), intervalMs)
    return () => clearInterval(id)
  }, [intervalMs])
  return now
}

/** Короткая длительность: «3 min», «19 h», «2 d». */
export function useDuration() {
  const { t } = useTranslation()
  return (fromIso: string, now: number) => {
    const minutes = Math.max(0, Math.floor((now - Date.parse(fromIso)) / 60_000))
    if (minutes < 1) return t('time.justNow')
    if (minutes < 60) return t('time.minutes', { count: minutes })
    const hours = Math.floor(minutes / 60)
    if (hours < 24) return t('time.hours', { count: hours })
    return t('time.days', { count: Math.floor(hours / 24) })
  }
}

/** Подпись для разделителя дат в переписке: Today, Yesterday или дата. */
export function useDayLabel() {
  const { t } = useTranslation()
  return (iso: string) => {
    const day = new Date(iso)
    const today = new Date()
    const yesterday = new Date()
    yesterday.setDate(today.getDate() - 1)
    if (day.toDateString() === today.toDateString()) return t('time.today')
    if (day.toDateString() === yesterday.toDateString()) return t('time.yesterday')
    return day.toLocaleDateString([], { day: 'numeric', month: 'long', year: day.getFullYear() === today.getFullYear() ? undefined : 'numeric' })
  }
}

export function formatTime(iso: string): string {
  return new Date(iso).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })
}
