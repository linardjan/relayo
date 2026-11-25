import { Empty, Segmented, Spin, Typography } from 'antd'
import { ArrowDownOutlined, ArrowUpOutlined, DownOutlined, UpOutlined } from '@ant-design/icons'
import { useQuery } from '@tanstack/react-query'
import { useCallback, useEffect, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { ticketsApi, type TicketSummary } from '../api/tickets'
import { useAuth } from '../auth/AuthContext'
import { useInboxState } from './InboxState'
import { ChannelIcon, PersonAvatar, useDuration, useNow } from './visuals'

type GroupKey = 'mine' | 'unassigned' | 'others'
const COLLAPSED_KEY = 'relayo:inbox-collapsed-groups'

function readCollapsed(): GroupKey[] {
  try {
    return JSON.parse(localStorage.getItem(COLLAPSED_KEY) ?? '[]') as GroupKey[]
  } catch {
    return []
  }
}

/**
 * Список тикетов. Открытые разбиты на группы «Мои», «Неназначенные», «У других»: оператор сразу видит свою работу
 * и очередь. Группы сворачиваются, состояние запоминается.
 */
export function TicketList({ selectedId, onSelect }: { selectedId?: string; onSelect: (id: string) => void }) {
  const { t } = useTranslation()
  const { user } = useAuth()
  const { isUnread } = useInboxState()
  const [closed, setClosed] = useState(false)
  const [collapsed, setCollapsed] = useState<GroupKey[]>(readCollapsed)
  const listRef = useRef<HTMLDivElement>(null)
  const [offscreen, setOffscreen] = useState<{ above: number; below: number }>({ above: 0, below: 0 })

  // Непросмотренные тикеты вне видимой части списка: показываем плашку «↓ 1 new», чтобы их не пропустить.
  const measure = useCallback(() => {
    const list = listRef.current
    if (!list) return
    const top = list.scrollTop
    const bottom = top + list.clientHeight
    let above = 0
    let below = 0
    list.querySelectorAll<HTMLElement>('.inbox-row.unread').forEach((row) => {
      if (row.offsetTop + row.offsetHeight / 2 > bottom) below++
      else if (row.offsetTop + row.offsetHeight / 2 < top) above++
    })
    setOffscreen((old) => (old.above === above && old.below === below ? old : { above, below }))
  }, [])
  const scrollToUnread = (direction: 'up' | 'down') => {
    const list = listRef.current
    if (!list) return
    const rows = [...list.querySelectorAll<HTMLElement>('.inbox-row.unread')]
    const target = direction === 'down'
      ? rows.find((r) => r.offsetTop > list.scrollTop + list.clientHeight - r.offsetHeight)
      : rows.reverse().find((r) => r.offsetTop < list.scrollTop)
    target?.scrollIntoView({ behavior: 'smooth', block: 'center' })
  }
  const { data, isLoading } = useQuery({ queryKey: ['tickets', closed], queryFn: () => ticketsApi.list(closed) })

  const toggle = (key: GroupKey) => {
    const next = collapsed.includes(key) ? collapsed.filter((k) => k !== key) : [...collapsed, key]
    localStorage.setItem(COLLAPSED_KEY, JSON.stringify(next))
    setCollapsed(next)
  }

  useEffect(() => {
    measure()
    window.addEventListener('resize', measure)
    return () => window.removeEventListener('resize', measure)
  })

  const groups: { key: GroupKey; tickets: TicketSummary[] }[] = [
    { key: 'mine', tickets: data?.filter((x) => x.assigneeId === user?.id) ?? [] },
    { key: 'unassigned', tickets: data?.filter((x) => x.assigneeId === null) ?? [] },
    { key: 'others', tickets: data?.filter((x) => x.assigneeId !== null && x.assigneeId !== user?.id) ?? [] },
  ]

  return (
    <div style={{ display: 'flex', flexDirection: 'column', height: '100%' }}>
      <div style={{ padding: '10px 12px', borderBottom: '1px solid #f0f0f0' }}>
        <Segmented
          block
          size="small"
          value={closed ? 'closed' : 'open'}
          onChange={(v) => setClosed(v === 'closed')}
          options={[
            { value: 'open', label: t('inbox.open') },
            { value: 'closed', label: t('inbox.closed') },
          ]}
        />
      </div>
      <div style={{ position: 'relative', flex: 1, minHeight: 0, display: 'flex' }}>
      {offscreen.above > 0 && (
        <button type="button" className="inbox-new-pill top" onClick={() => scrollToUnread('up')}>
          <ArrowUpOutlined /> {t('inbox.newCount', { count: offscreen.above })}
        </button>
      )}
      <div ref={listRef} onScroll={measure} style={{ flex: 1, overflowY: 'auto' }}>
        {isLoading ? (
          <Spin style={{ display: 'block', margin: 24 }} />
        ) : closed ? (
          data?.length ? (
            data.map((ticket) => <TicketRow key={ticket.id} ticket={ticket} selected={ticket.id === selectedId} unread={false} onSelect={onSelect} />)
          ) : (
            <Empty style={{ marginTop: 48 }} description={t('inbox.empty')} />
          )
        ) : (
          groups.map((group) => (
            <div key={group.key}>
              <button type="button" className="inbox-group" onClick={() => toggle(group.key)}>
                <span>
                  {t(`inbox.groups.${group.key}`)}
                  {group.tickets.length > 0 && <span className="inbox-group-count">{group.tickets.length}</span>}
                  {group.key !== 'others' && group.tickets.some(isUnread) && (
                    <span className="inbox-group-new">{t('inbox.newCount', { count: group.tickets.filter(isUnread).length })}</span>
                  )}
                </span>
                {collapsed.includes(group.key) ? <DownOutlined /> : <UpOutlined />}
              </button>
              {!collapsed.includes(group.key) &&
                (group.tickets.length ? (
                  group.tickets.map((ticket) => (
                    <TicketRow key={ticket.id} ticket={ticket} selected={ticket.id === selectedId} unread={isUnread(ticket)} onSelect={onSelect} />
                  ))
                ) : (
                  <Typography.Text type="secondary" style={{ display: 'block', padding: '8px 16px 12px', fontSize: 12 }}>
                    {t('inbox.groupEmpty')}
                  </Typography.Text>
                ))}
            </div>
          ))
        )}
      </div>
      {offscreen.below > 0 && (
        <button type="button" className="inbox-new-pill bottom" onClick={() => scrollToUnread('down')}>
          <ArrowDownOutlined /> {t('inbox.newCount', { count: offscreen.below })}
        </button>
      )}
      </div>
    </div>
  )
}

function TicketRow({
  ticket,
  selected,
  unread,
  onSelect,
}: {
  ticket: TicketSummary
  selected: boolean
  unread: boolean
  onSelect: (id: string) => void
}) {
  const now = useNow()
  const duration = useDuration()
  // Клиент ждёт ответа оператора: время подсвечивается красным.
  const awaiting = ticket.status === 'New' || ticket.status === 'InProgress'

  return (
    <div className={`inbox-row${selected ? ' selected' : ''}${unread ? ' unread' : ''}`} onClick={() => onSelect(ticket.id)}>
      <PersonAvatar name={ticket.clientLabel} seed={ticket.clientId} />
      <div style={{ flex: 1, minWidth: 0 }}>
        <div className="inbox-row-line">
          <ChannelIcon channel={ticket.lastClientChannel} />
          <span className={`inbox-row-name${unread ? ' unread' : ''}`}>{ticket.clientLabel}</span>
          {unread && <span className="inbox-row-dot" />}
          <span className="inbox-row-number">#{ticket.number}</span>
        </div>
        <div className="inbox-row-line">
          <span className="inbox-row-preview">{ticket.lastMessagePreview}</span>
          {ticket.status !== 'Closed' && (
            <span className={`inbox-row-time${awaiting ? ' awaiting' : ''}`}>{duration(ticket.lastMessageAt, now)}</span>
          )}
        </div>
      </div>
    </div>
  )
}
