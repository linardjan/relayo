import { Collapse, Descriptions, Empty, Tag, Typography } from 'antd'
import { CheckCircleOutlined, ExportOutlined, WarningOutlined } from '@ant-design/icons'
import { useQuery } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { Link } from 'react-router'
import { clientsApi } from '../api/clients'
import type { TicketDetails } from '../api/tickets'
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
  const defaultOpen = saved ? (JSON.parse(saved) as string[]) : ['client', 'channels', 'tickets']

  return (
    <Collapse
      ghost
      expandIconPlacement="end"
      defaultActiveKey={defaultOpen}
      onChange={(keys) => localStorage.setItem(OPEN_KEY, JSON.stringify(keys))}
      className="client-panel"
      items={[
        {
          key: 'client',
          label: t('inbox.panel.client'),
          children: (
            <>
              <div style={{ display: 'flex', alignItems: 'center', gap: 12, marginBottom: 12 }}>
                <PersonAvatar name={client.label} seed={client.id} size={44} />
                <div style={{ minWidth: 0 }}>
                  <Link to={`/clients/${client.id}`} style={{ fontWeight: 500, wordBreak: 'break-word' }}>
                    {client.label} <ExportOutlined style={{ fontSize: 11 }} />
                  </Link>
                  <div>
                    <ClientStatusTag {...client} />
                  </div>
                </div>
              </div>
              <Descriptions size="small" column={1} colon={false} styles={{ label: { color: '#8c8c8c', width: 80 } }}>
                <Descriptions.Item label={t('client.site')}>{client.siteCode}</Descriptions.Item>
                <Descriptions.Item label="clientId">{client.externalClientId ?? '—'}</Descriptions.Item>
                <Descriptions.Item label={t('clients.locale')}>{client.locale ?? '—'}</Descriptions.Item>
              </Descriptions>
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
