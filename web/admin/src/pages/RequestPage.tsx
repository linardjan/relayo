import { Alert, App as AntApp, Button, Empty, Segmented, Select, Space, Spin, Tag, Tooltip, Typography } from 'antd'
import { CopyOutlined, CrownFilled, EditOutlined, EyeInvisibleOutlined, LeftOutlined, PaperClipOutlined, RightOutlined, RobotOutlined, ShareAltOutlined } from '@ant-design/icons'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useMemo, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Link, useNavigate, useParams } from 'react-router'
import { ApiError } from '../api/client'
import { clientsApi } from '../api/clients'
import { ticketsApi, type OperatorMessage, type TicketDetails, type TicketStatus, type TicketSummary } from '../api/tickets'
import { useAuth } from '../auth/AuthContext'
import { AliasModal } from '../inbox/AliasModal'
import { Composer, MessageAttachments } from '../inbox/Conversation'
import { ChannelIcon, PersonAvatar } from '../inbox/visuals'
import { statusColor } from '../inbox/status'
import { PageHeader, SoonTag } from '../layout/Page'
import { errorText } from '../settings/errors'
import { formatDateTime } from '../format'
import { REQUEST_ORDER_KEY, RelativeDate } from './RequestsPage'

const STATUSES: TicketStatus[] = ['New', 'InProgress', 'WaitingClient', 'Closed']

/**
 * Страница запроса, как карточка запроса в Usedesk: сверху группа, SLA и переход к соседним запросам; заголовок
 * с темой, номером, статусом, типом и приоритетом; доп. поля и теги; поле ответа с исполнителем и статусом;
 * ниже — история (новое сверху). Справа — клиент, его другие запросы и все вложения.
 */
export function RequestPage() {
  const { t } = useTranslation()
  const { message } = AntApp.useApp()
  const { ticketId } = useParams()
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const { user } = useAuth()
  const [aliasOpen, setAliasOpen] = useState(false)
  const [activeChat, setActiveChat] = useState<string>()

  const details = useQuery({ queryKey: ['ticket', ticketId], queryFn: () => ticketsApi.get(ticketId!) })
  const groups = useQuery({ queryKey: ['ticket-groups'], queryFn: ticketsApi.groups, staleTime: 60_000 })
  const agents = useQuery({ queryKey: ['agents'], queryFn: ticketsApi.agents, staleTime: 60_000 })
  const clientId = details.data?.client.id
  const client = useQuery({ queryKey: ['client', clientId], queryFn: () => clientsApi.get(clientId!), enabled: !!clientId })

  const update = (summary: TicketSummary) => {
    queryClient.setQueryData<TicketDetails>(['ticket', summary.id], (old) => (old ? { ...old, ticket: summary } : old))
    void queryClient.invalidateQueries({ queryKey: ['tickets'] })
    void queryClient.invalidateQueries({ queryKey: ['client', clientId] })
  }
  const onError = (e: unknown) => message.error(errorText(e, t('settings.saveFailed')))
  const setGroup = useMutation({ mutationFn: (g: string | null) => ticketsApi.setGroup(ticketId!, g), onSuccess: update, onError })
  const setStatus = useMutation({ mutationFn: (s: TicketStatus) => ticketsApi.setStatus(ticketId!, s), onSuccess: update, onError })
  const setAssignee = useMutation({ mutationFn: (u: string | null) => ticketsApi.setAssignee(ticketId!, u), onSuccess: update, onError })
  const setSubject = useMutation({ mutationFn: (s: string) => ticketsApi.setSubject(ticketId!, s || null), onSuccess: update, onError })

  // Соседние запросы — в порядке последнего открытого списка.
  const order: string[] = JSON.parse(sessionStorage.getItem(REQUEST_ORDER_KEY) ?? '[]')
  const index = order.indexOf(ticketId!)

  const history = useMemo(() => {
    const all = details.data?.chats.flatMap((c) => c.messages) ?? []
    return [...all].sort((a, b) => b.createdAt.localeCompare(a.createdAt))
  }, [details.data])
  const attachments = history.flatMap((m) => m.attachments ?? [])

  const crumbsBase = [{ title: t('nav.requests'), to: '/requests' }]
  if (details.isLoading) return <div className="page"><PageHeader crumbs={[...crumbsBase, { title: '…' }]} /><Spin style={{ display: 'block', margin: 48 }} /></div>
  if (details.error || !details.data) return <div className="page"><PageHeader crumbs={crumbsBase} /><Empty description={t('requests.notFound')} /></div>

  const { ticket, chats } = details.data
  const isClosed = ticket.status === 'Closed'
  const chatId = activeChat ?? chats[chats.length - 1]?.id
  const chat = chats.find((c) => c.id === chatId)
  const title = ticket.subject ?? t('requests.chat')
  const canAssignOthers = user?.role === 'Admin' || user?.role === 'Supervisor'
  const assigneeOptions = [
    { value: '', label: t('ticket.unassigned') },
    ...(agents.data ?? [])
      .filter((a) => canAssignOthers || a.id === user?.id || a.id === ticket.assigneeId)
      .map((a) => ({ value: a.id, label: a.fullName })),
  ]
  const statusSelect = (
    <Select
      value={ticket.status}
      disabled={isClosed}
      loading={setStatus.isPending}
      onChange={(s) => setStatus.mutate(s)}
      className={`status-select status-${ticket.status}`}
      popupMatchSelectWidth={false}
      options={STATUSES.map((s) => ({ value: s, label: t(`ticket.status.${s}`) }))}
    />
  )
  const c = client.data?.client

  return (
    <div className="page">
      <PageHeader
        crumbs={[
          { title: `${t('requests.channel')}: ${t(`inbox.channel.${ticket.lastClientChannel}`)}` },
          ...crumbsBase,
          { title },
        ]}
      />
      <div className="request-grid">
        <section className="request-main">
          <div className="request-tools">
            <Select
              allowClear
              placeholder={t('ticket.noGroup')}
              value={ticket.groupId ?? undefined}
              onChange={(g) => setGroup.mutate(g ?? null)}
              options={groups.data?.map((g) => ({ value: g.id, label: g.name }))}
              style={{ minWidth: 200 }}
            />
            <Tooltip title={t('common.comingSoon')}>
              <Button disabled className="request-sla">{t('requests.noSla')}</Button>
            </Tooltip>
            <span style={{ flex: 1 }} />
            <Button icon={<LeftOutlined />} disabled={index <= 0} onClick={() => navigate(`/requests/${order[index - 1]}`)} aria-label={t('requests.previous')} />
            <Button icon={<RightOutlined />} disabled={index < 0 || index >= order.length - 1} onClick={() => navigate(`/requests/${order[index + 1]}`)} aria-label={t('requests.next')} />
          </div>

          <div className="request-titlebar">
            <ChannelIcon channel={ticket.lastClientChannel} />
            <Typography.Title
              level={4}
              className="request-title"
              editable={{ onChange: (v) => v.trim() !== (ticket.subject ?? '') && setSubject.mutate(v.trim()), tooltip: t('requests.editSubject'), maxLength: 255, text: ticket.subject ?? '' }}
            >
              {title}
            </Typography.Title>
            <Typography.Text type="secondary">#{ticket.number}</Typography.Text>
            {statusSelect}
            <Tooltip title={t('common.comingSoon')}>
              <Select disabled value="question" className="request-pill" options={[{ value: 'question', label: t('requests.typeQuestion') }]} />
            </Tooltip>
            <Tooltip title={t('common.comingSoon')}>
              <Select disabled value="medium" className="request-pill" options={[{ value: 'medium', label: t('requests.priorityMedium') }]} />
            </Tooltip>
          </div>

          <div className="request-fields">
            <span>{t('requests.customFields')}</span> <SoonTag />
          </div>
          <div className="request-tags">
            <Space>
              <Typography.Text type="secondary">{t('requests.tags')}:</Typography.Text>
              <SoonTag />
            </Space>
            <Space>
              <Tooltip title={`${t('requests.copy')} · ${t('common.comingSoon')}`}>
                <Button size="small" icon={<CopyOutlined />} disabled />
              </Tooltip>
              <Tooltip title={`${t('requests.forward')} · ${t('common.comingSoon')}`}>
                <Button size="small" icon={<ShareAltOutlined />} disabled />
              </Tooltip>
            </Space>
          </div>

          {chats.length > 1 && (
            <Segmented
              value={chatId}
              onChange={(v) => setActiveChat(v as string)}
              options={chats.map((x, i) => ({ value: x.id, label: t('ticket.chat', { n: i + 1 }) }))}
              style={{ marginBottom: 8 }}
            />
          )}
          {chat && !chat.isVerified && details.data.client.externalClientId && (
            <Alert type="warning" showIcon title={t('ticket.unverifiedChat')} style={{ marginBottom: 8 }} />
          )}
          {chat && !isClosed && (
            <div className="request-composer">
              <Composer
                replyChannel={chat.replyChannel}
                extra={
                  <>
                    <Typography.Text type="secondary">{t('ticket.assignee')}</Typography.Text>
                    <Select
                      value={ticket.assigneeId ?? ''}
                      loading={setAssignee.isPending}
                      onChange={(v) => setAssignee.mutate(v || null)}
                      options={assigneeOptions}
                      style={{ minWidth: 170 }}
                      showSearch
                      optionFilterProp="label"
                      popupMatchSelectWidth={false}
                    />
                    <Typography.Text type="secondary">{t('requests.columns.status')}</Typography.Text>
                    {statusSelect}
                  </>
                }
                onSend={async (mode, text, files) => {
                  try {
                    if (mode === 'comment') await ticketsApi.note(ticket.id, chat.id, text, files)
                    else await ticketsApi.reply(ticket.id, chat.id, text, files)
                    void queryClient.invalidateQueries({ queryKey: ['ticket', ticket.id] })
                    return true
                  } catch (e) {
                    if (e instanceof ApiError && e.code === 'alias_required') setAliasOpen(true)
                    else message.error(errorText(e, t(mode === 'comment' ? 'ticket.noteFailed' : 'ticket.sendFailed')))
                    return false
                  }
                }}
              />
            </div>
          )}
          {isClosed && <Alert type="info" showIcon title={t('ticket.closedHelp')} style={{ marginBottom: 12 }} />}

          <div className="request-history">
            {history.length === 0 && <Empty description={t('requests.noMessages')} />}
            {history.map((m) => (
              <HistoryItem key={m.id} message={m} clientLabel={details.data.client.label} clientId={details.data.client.id} avatar={c?.avatarUrl} />
            ))}
          </div>
        </section>

        <aside className="request-side">
          <div className="request-card request-profile">
            <PersonAvatar name={details.data.client.label} seed={details.data.client.id} size={98} src={c?.avatarUrl} />
            <div className="request-profile-name">
              {c?.isVip && <Tooltip title="VIP"><CrownFilled style={{ color: '#faad14' }} /></Tooltip>}
              <Link to={`/clients/${details.data.client.id}/details`}>{details.data.client.label}</Link>
              <Tooltip title={t('clients.edit')}>
                <Link to={`/clients/${details.data.client.id}`} aria-label={t('clients.edit')}>
                  <EditOutlined />
                </Link>
              </Tooltip>
            </div>
            {c?.isSpammer && <Tag color="red">{t('clients.spammer')}</Tag>}
            <div className="request-profile-meta">
              <Typography.Text type="secondary">{t('clients.company')}</Typography.Text> <SoonTag />
              {c?.position && <div>{c.position}</div>}
              <div>
                {details.data.client.siteCode}
                {details.data.client.externalClientId ? ` · clientId ${details.data.client.externalClientId}` : ''}
              </div>
            </div>
            {c?.note && <div className="client-note">{c.note}</div>}
          </div>

          <div className="request-card">
            <div className="request-card-title">{t('requests.otherRequests')}</div>
            <div className="request-card-body">
              {client.data?.tickets.filter((x) => x.id !== ticket.id).slice(0, 5).map((x) => (
                <div key={x.id} className="request-other">
                  <Tag color={statusColor[x.status]}>{t(`ticket.status.${x.status}`)}</Tag>
                  <Link to={`/requests/${x.id}`}>
                    <ChannelIcon channel={x.channel} /> {x.subject ?? x.lastMessagePreview ?? t('requests.chat')}
                  </Link>
                  <Typography.Text type="secondary" style={{ fontSize: 12 }}>
                    {' '}— {t('requests.updated')} <RelativeDate iso={x.lastMessageAt} />
                  </Typography.Text>
                </div>
              ))}
              {client.data && client.data.tickets.length <= 1 && <Typography.Text type="secondary">{t('requests.noOtherRequests')}</Typography.Text>}
              {client.data && client.data.tickets.length > 1 && (
                <Link to={`/clients/${details.data.client.id}/details`}>{t('requests.allRequests')}</Link>
              )}
            </div>
          </div>

          <div className="request-card">
            <div className="request-card-title">{t('requests.allAttachments')}</div>
            <div className="request-card-body">
              {attachments.length === 0 ? (
                <Typography.Text type="secondary">{t('requests.noAttachments')}</Typography.Text>
              ) : (
                attachments.map((a) => (
                  <div key={a.id}>
                    <PaperClipOutlined />{' '}
                    {a.url ? (
                      <a href={a.url} target="_blank" rel="noreferrer">
                        {a.fileName}
                      </a>
                    ) : (
                      a.fileName
                    )}
                  </div>
                ))
              )}
            </div>
          </div>
        </aside>
      </div>
      <AliasModal open={aliasOpen} onClose={() => setAliasOpen(false)} />
    </div>
  )
}

/** Запись истории: серая строка «кто и когда», под ней текст. Заметки — жёлтые, системные и автоответы — серые. */
function HistoryItem({ message: m, clientLabel, clientId, avatar }: { message: OperatorMessage; clientLabel: string; clientId: string; avatar?: string | null }) {
  const { t } = useTranslation()
  const isNote = m.visibility === 'Internal'
  const who =
    m.author === 'Client' ? clientLabel : m.author === 'System' ? t('ticket.system') : (m.authorName ?? t('ticket.operator'))
  return (
    <div className={`history-item history-${m.author.toLowerCase()}${isNote ? ' history-note' : ''}`}>
      <div className="history-head">
        {m.author === 'Client' ? (
          <PersonAvatar name={clientLabel} seed={clientId} size={24} src={avatar} />
        ) : m.automated ? (
          <RobotOutlined />
        ) : null}
        <b>{who}</b>
        {isNote && (
          <Tag icon={<EyeInvisibleOutlined />} color="gold">
            {t('ticket.note')}
          </Tag>
        )}
        <ChannelIcon channel={m.channel} />
        <span className="history-time">{formatDateTime(m.createdAt)}</span>
      </div>
      {m.text && <div className="history-text">{m.text}</div>}
      <MessageAttachments items={m.attachments} />
    </div>
  )
}
