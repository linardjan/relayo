import { App as AntApp, Collapse, Descriptions, Empty, Select, Tag, Typography } from 'antd'
import { CheckCircleOutlined, ExportOutlined, WarningOutlined } from '@ant-design/icons'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { Link } from 'react-router'
import { clientsApi } from '../api/clients'
import { ticketsApi, type TicketDetails } from '../api/tickets'
import { errorText } from '../settings/errors'
import { ClientStatusTag } from '../clients/ClientStatusTag'
import { formatDateTime } from '../format'
import { statusColor } from './status'
import { ChannelIcon, PersonAvatar } from './visuals'

const OPEN_KEY = 'relayo:client-panel-sections'

const identityChannel = { Visitor: 'Widget', Telegram: 'Telegram', WhatsApp: 'WhatsApp' } as const

/** Правая панель: кто клиент, через какие каналы пишет и какие у него ещё тикеты. */
export function ClientPanel({ details }: { details: TicketDetails }) {
  const { t } = useTranslation()
  const { client, ticket } = details
  const { data } = useQuery({ queryKey: ['client', client.id], queryFn: () => clientsApi.get(client.id) })
  const otherTickets = data?.tickets.filter((x) => x.id !== ticket.id) ?? []
  const saved = localStorage.getItem(OPEN_KEY)
  const defaultOpen = saved ? (JSON.parse(saved) as string[]) : ['ticket', 'client', 'channels', 'tickets']

  return (
    <Collapse
      ghost
      expandIconPlacement="end"
      defaultActiveKey={defaultOpen}
      onChange={(keys) => localStorage.setItem(OPEN_KEY, JSON.stringify(keys))}
      className="client-panel"
      items={[
        {
          key: 'ticket',
          label: t('inbox.panel.ticket'),
          children: <TicketSection details={details} />,
        },
        {
          key: 'client',
          label: t('inbox.panel.client'),
          children: (
            <>
              <div style={{ display: 'flex', alignItems: 'center', gap: 12, marginBottom: 12 }}>
                <PersonAvatar name={client.label} seed={client.id} size={44} src={data?.client.avatarUrl} />
                <div style={{ minWidth: 0 }}>
                  <Link to={`/clients/${client.id}/details`} style={{ fontWeight: 500, wordBreak: 'break-word' }}>
                    {client.label} <ExportOutlined style={{ fontSize: 11 }} />
                  </Link>
                  <div>
                    <ClientStatusTag {...client} />
                    {data?.client.isVip && <Tag color="gold">VIP</Tag>}
                    {data?.client.isSpammer && <Tag color="red">{t('clients.spammer')}</Tag>}
                  </div>
                </div>
              </div>
              <Descriptions size="small" column={1} colon={false} styles={{ label: { color: '#8c8c8c', width: 80 } }}>
                <Descriptions.Item label={t('client.site')}>{client.siteCode}</Descriptions.Item>
                <Descriptions.Item label="clientId">{client.externalClientId ?? '—'}</Descriptions.Item>
                <Descriptions.Item label={t('clients.locale')}>{client.locale ?? '—'}</Descriptions.Item>
                {data?.client.position && <Descriptions.Item label={t('clients.position')}>{data.client.position}</Descriptions.Item>}
              </Descriptions>
              {/* Заметка о клиенте — то, что сотрудникам важно знать при ответе. */}
              {data?.client.note && <div className="client-note">{data.client.note}</div>}
            </>
          ),
        },
        {
          key: 'channels',
          label: t('clients.channels'),
          children: data?.identities.length ? (
            data.identities.map((i) => (
              <div key={i.id} className="panel-row">
                <ChannelIcon channel={identityChannel[i.type]} />
                <span style={{ flex: 1, minWidth: 0 }}>
                  {i.displayName ?? t(`clients.identity.${i.type}`)}
                  <Typography.Text type="secondary" style={{ display: 'block', fontSize: 12 }}>
                    {t('clients.lastSeen')}: {formatDateTime(i.lastSeenAt)}
                  </Typography.Text>
                </span>
                {i.isVerified ? (
                  <CheckCircleOutlined style={{ color: '#52c41a' }} title={t('clients.identityVerified')} />
                ) : (
                  <WarningOutlined style={{ color: '#bfbfbf' }} title={t('clients.identityUnverified')} />
                )}
              </div>
            ))
          ) : (
            <Typography.Text type="secondary">{t('clients.noChannels')}</Typography.Text>
          ),
        },
        {
          key: 'tickets',
          label: (
            <span>
              {t('inbox.panel.otherTickets')}
              {otherTickets.length > 0 && <span className="inbox-group-count">{otherTickets.length}</span>}
            </span>
          ),
          children: otherTickets.length ? (
            otherTickets.map((x) => (
              <Link key={x.id} to={`/tickets/${x.id}`} className="panel-ticket">
                <div style={{ display: 'flex', justifyContent: 'space-between', gap: 8 }}>
                  <span>#{x.number}</span>
                  <Tag color={statusColor[x.status]} style={{ marginInlineEnd: 0 }}>
                    {t(`ticket.status.${x.status}`)}
                  </Tag>
                </div>
                <Typography.Text type="secondary" ellipsis style={{ display: 'block', fontSize: 12 }}>
                  {x.lastMessagePreview || '—'}
                </Typography.Text>
                <Typography.Text type="secondary" style={{ fontSize: 11 }}>
                  {formatDateTime(x.lastMessageAt)}
                </Typography.Text>
              </Link>
            ))
          ) : (
            <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={t('inbox.panel.noOtherTickets')} />
          ),
        },
      ]}
    />
  )
}

/** Поля тикета: группа (очередь), тема и откуда тикет — для перенесённых из Usedesk; страница, с которой писал клиент. */
function TicketSection({ details }: { details: TicketDetails }) {
  const { t } = useTranslation()
  const { message } = AntApp.useApp()
  const queryClient = useQueryClient()
  const { ticket, chats } = details
  const groups = useQuery({ queryKey: ['ticket-groups'], queryFn: ticketsApi.groups, staleTime: 60_000 })
  const setGroup = useMutation({
    mutationFn: (groupId: string | null) => ticketsApi.setGroup(ticket.id, groupId),
    onSuccess: (summary) => {
      queryClient.setQueryData<TicketDetails>(['ticket', ticket.id], (old) => (old ? { ...old, ticket: summary } : old))
      void queryClient.invalidateQueries({ queryKey: ['tickets'] })
    },
    onError: (e) => message.error(errorText(e, t('ticket.groupFailed'))),
  })
  // Страница последнего чата, где она известна.
  const pageUrl = [...chats].reverse().find((c) => c.pageUrl)?.pageUrl

  return (
    <Descriptions size="small" column={1} colon={false} styles={{ label: { color: '#8c8c8c', width: 80 } }}>
      <Descriptions.Item label={t('ticket.group')}>
        <Select
          size="small"
          style={{ width: '100%' }}
          allowClear
          placeholder={t('ticket.noGroup')}
          value={ticket.groupId ?? undefined}
          loading={groups.isLoading || setGroup.isPending}
          options={(groups.data ?? []).map((g) => ({ value: g.id, label: g.name }))}
          onChange={(value?: string) => setGroup.mutate(value ?? null)}
        />
      </Descriptions.Item>
      {ticket.subject && <Descriptions.Item label={t('ticket.subject')}>{ticket.subject}</Descriptions.Item>}
      {ticket.source === 'Usedesk' && (
        <Descriptions.Item label={t('ticket.source')}>
          <Tag style={{ marginInlineEnd: 0 }}>{t('ticket.sourceUsedesk')}</Tag>
          {ticket.sourceChannel && <Typography.Text type="secondary"> · {ticket.sourceChannel}</Typography.Text>}
        </Descriptions.Item>
      )}
      {pageUrl && (
        <Descriptions.Item label={t('ticket.page')}>
          <a className="ticket-page-url" href={pageUrl} target="_blank" rel="noreferrer noopener">
            {pageUrl}
          </a>
        </Descriptions.Item>
      )}
    </Descriptions>
  )
}
