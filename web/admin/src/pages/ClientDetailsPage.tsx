import { Empty, List, Space, Spin, Table, Tag, Tooltip, Typography } from 'antd'
import { CheckCircleOutlined, EditOutlined, WarningOutlined } from '@ant-design/icons'
import { useQuery } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { Link, useNavigate, useParams } from 'react-router'
import { clientsApi, type ClientDetails } from '../api/clients'
import { ClientStatusTag } from '../clients/ClientStatusTag'
import { formatDateTime } from '../format'
import { statusColor } from '../inbox/status'
import { ChannelIcon, PersonAvatar } from '../inbox/visuals'
import { PageHeader, SoonTag } from '../layout/Page'

type ClientTicket = ClientDetails['tickets'][number]

/** Каналы клиента одной меткой на тип и имя: у посетителя виджета обычно несколько браузеров. */
function channelTags(identities: ClientDetails['identities']) {
  const groups = new Map<string, { key: string; type: ClientDetails['identities'][number]['type']; displayName: string | null; count: number; lastSeenAt: string }>()
  for (const i of identities) {
    const key = `${i.type}:${i.displayName ?? ''}`
    const group = groups.get(key)
    if (group) {
      group.count++
      if (i.lastSeenAt > group.lastSeenAt) group.lastSeenAt = i.lastSeenAt
    } else groups.set(key, { key, type: i.type, displayName: i.displayName, count: 1, lastSeenAt: i.lastSeenAt })
  }
  return [...groups.values()]
}

/** Сегодня — только время, раньше — дата и время (как столбец «Время» в Usedesk). */
function shortTime(iso: string) {
  const date = new Date(iso)
  return date.toDateString() === new Date().toDateString()
    ? date.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })
    : formatDateTime(iso)
}

/**
 * Детали клиента — сюда ведёт клик по клиенту в чате. Раскладка как в Usedesk: шапка с аватаром, именем и кнопкой
 * редактирования (ведёт на страницу клиента), ниже — запросы клиента и комментарии к постам.
 */
export function ClientDetailsPage() {
  const { t } = useTranslation()
  const { clientId } = useParams()
  const navigate = useNavigate()
  const { data, isLoading, error } = useQuery({ queryKey: ['client', clientId], queryFn: () => clientsApi.get(clientId!) })

  const crumbs = [{ title: t('nav.clients'), to: '/clients' }, { title: data?.client.label ?? '…' }]
  if (isLoading) return <div className="page"><PageHeader crumbs={crumbs} /><Spin style={{ display: 'block', margin: 48 }} /></div>
  if (error || !data) return <div className="page"><PageHeader crumbs={crumbs} /><Empty description={t('clients.notFound')} /></div>
  const { client, identities, tickets } = data
  const meta = [client.siteCode, client.externalClientId ? `clientId ${client.externalClientId}` : null, client.locale, client.position].filter(Boolean)

  return (
    <div className="page">
      <PageHeader crumbs={crumbs} />
      <div className="client-hero">
        <PersonAvatar name={client.label} seed={client.id} size={196} src={client.avatarUrl} />
        <div className="client-hero-title">
          <div className="client-hero-name">
            {client.label}
            <Tooltip title={t('clients.edit')}>
              <Link to={`/clients/${client.id}`} className="client-card-edit" aria-label={t('clients.edit')}>
                <EditOutlined />
              </Link>
            </Tooltip>
          </div>
          <Space size={[6, 6]} wrap>
            <ClientStatusTag {...client} />
            {client.isVip && <Tag color="gold">VIP</Tag>}
            {client.isSpammer && <Tag color="red">{t('clients.spammer')}</Tag>}
            {channelTags(identities).map((c) => (
              <Tooltip key={c.key} title={`${t('clients.lastSeen')}: ${formatDateTime(c.lastSeenAt)}`}>
                <Tag>
                  {t(`clients.identity.${c.type}`)}
                  {c.displayName ? `: ${c.displayName}` : ''}
                  {c.count > 1 ? ` ×${c.count}` : ''}
                </Tag>
              </Tooltip>
            ))}
          </Space>
          {meta.length > 0 && <Typography.Text type="secondary">{meta.join(' · ')}</Typography.Text>}
          {client.note && <div className="client-note">{client.note}</div>}
        </div>
      </div>

      <section className="client-section">
        <h2>{t('clients.requests')}</h2>
        <Table<ClientTicket>
          rowKey="id"
          size="middle"
          bordered
          dataSource={tickets}
          locale={{ emptyText: t('clients.noTickets') }}
          onRow={(r) => ({ onClick: () => navigate(`/requests/${r.id}`), style: { cursor: 'pointer' } })}
          pagination={{
            defaultPageSize: 25,
            pageSizeOptions: [25, 50, 100],
            showSizeChanger: true,
            hideOnSinglePage: false,
            showTotal: (total, [from, to]) => t('clients.requestsRange', { from, to, total }),
          }}
          expandable={{
            rowExpandable: (r) => r.chats.length > 1,
            expandedRowRender: (r) => (
              <List
                size="small"
                dataSource={r.chats}
                renderItem={(c, index) => (
                  <List.Item>
                    <Space>
                      {c.isVerified ? <CheckCircleOutlined style={{ color: '#52c41a' }} /> : <WarningOutlined style={{ color: '#faad14' }} />}
                      {t('ticket.chat', { n: index + 1 })}
                      <Typography.Text type="secondary">
                        {t('clients.messages', { count: c.messageCount })} · {formatDateTime(c.createdAt)} — {formatDateTime(c.lastMessageAt)}
                      </Typography.Text>
                    </Space>
                  </List.Item>
                )}
              />
            ),
          }}
          columns={[
            {
              title: t('clients.status'),
              width: 130,
              sorter: (a, b) => a.status.localeCompare(b.status),
              render: (_, r) => <Tag color={statusColor[r.status]}>{t(`ticket.status.${r.status}`)}</Tag>,
            },
            { title: t('clients.client'), width: '20%', render: () => client.label },
            {
              title: t('clients.subject'),
              render: (_, r) => (
                <Space size={6}>
                  <ChannelIcon channel={r.channel} />
                  <Link to={`/requests/${r.id}`} onClick={(e) => e.stopPropagation()}>
                    #{r.number}
                  </Link>
                  <Typography.Text ellipsis style={{ maxWidth: 480 }}>
                    {r.subject ?? r.lastMessagePreview}
                  </Typography.Text>
                </Space>
              ),
            },
            { title: t('clients.agent'), width: '14%', render: (_, r) => r.assigneeName ?? '' },
            {
              title: t('clients.time'),
              width: 150,
              defaultSortOrder: 'descend',
              sorter: (a, b) => a.lastMessageAt.localeCompare(b.lastMessageAt),
              render: (_, r) => <Typography.Text type="secondary">{shortTime(r.lastMessageAt)}</Typography.Text>,
            },
          ]}
        />
      </section>

      <section className="client-section">
        <h2>
          {t('clients.postComments')} <SoonTag />
        </h2>
        <Table
          size="middle"
          bordered
          dataSource={[]}
          pagination={false}
          locale={{ emptyText: t('clients.noPostComments') }}
          columns={[
            { title: t('clients.status'), width: 130 },
            { title: t('clients.client'), width: '20%' },
            { title: t('clients.subject') },
            { title: t('clients.agent'), width: '14%' },
            { title: t('clients.time'), width: 150 },
          ]}
        />
      </section>
    </div>
  )
}
