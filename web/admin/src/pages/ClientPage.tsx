import { Card, Descriptions, Empty, List, Space, Spin, Table, Tag, Typography } from 'antd'
import { CheckCircleOutlined, WarningOutlined } from '@ant-design/icons'
import { Page } from '../layout/Page'
import { useQuery } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { Link, useParams } from 'react-router'
import { clientsApi, type ClientDetails } from '../api/clients'
import { ClientStatusTag } from '../clients/ClientStatusTag'
import { formatDateTime } from '../format'
import { statusColor } from '../inbox/status'

type ClientTicket = ClientDetails['tickets'][number]

/** Карточка клиента: данные, каналы, тикеты и их чаты (п. 3.2 ТЗ). */
export function ClientPage() {
  const { t } = useTranslation()
  const { clientId } = useParams()
  const { data, isLoading, error } = useQuery({ queryKey: ['client', clientId], queryFn: () => clientsApi.get(clientId!) })

  const crumbs = [{ title: t('nav.clients'), to: '/clients' }, { title: data?.client.label ?? '…' }]
  if (isLoading) return <Page crumbs={crumbs}><Spin style={{ display: 'block', margin: 48 }} /></Page>
  if (error || !data) return <Page crumbs={crumbs}><Empty description={t('clients.notFound')} /></Page>
  const { client, identities, tickets } = data

  return (
    <Page crumbs={crumbs}>

      <Card title={<Space>{client.label}<ClientStatusTag {...client} /></Space>}>
        <Descriptions column={{ xs: 1, md: 3 }}>
          <Descriptions.Item label={t('client.site')}>{client.siteCode}</Descriptions.Item>
          <Descriptions.Item label="clientId">{client.externalClientId ?? '—'}</Descriptions.Item>
          <Descriptions.Item label={t('clients.locale')}>{client.locale ?? '—'}</Descriptions.Item>
          <Descriptions.Item label={t('clients.created')}>{formatDateTime(client.createdAt)}</Descriptions.Item>
          <Descriptions.Item label={t('clients.lastActivity')}>{formatDateTime(client.lastActivityAt)}</Descriptions.Item>
          <Descriptions.Item label={t('clients.tickets')}>{client.ticketCount}</Descriptions.Item>
        </Descriptions>
      </Card>

      <Card title={t('clients.channels')}>
        <List
          dataSource={identities}
          locale={{ emptyText: t('clients.noChannels') }}
          renderItem={(i) => (
            <List.Item>
              <Space>
                <Tag>{t(`clients.identity.${i.type}`)}</Tag>
                {i.displayName && <Typography.Text>{i.displayName}</Typography.Text>}
                {i.isVerified ? (
                  <Typography.Text type="success">{t('clients.identityVerified')}</Typography.Text>
                ) : (
                  <Typography.Text type="secondary">{t('clients.identityUnverified')}</Typography.Text>
                )}
              </Space>
              <Typography.Text type="secondary">
                {t('clients.lastSeen')}: {formatDateTime(i.lastSeenAt)}
              </Typography.Text>
            </List.Item>
          )}
        />
      </Card>

      <Card title={t('clients.ticketsAndChats')}>
        <Table<ClientTicket>
          rowKey="id"
          dataSource={tickets}
          pagination={false}
          locale={{ emptyText: t('clients.noTickets') }}
          expandable={{
            rowExpandable: (r) => r.chats.length > 0,
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
            { title: '#', render: (_, r) => <Link to={`/tickets/${r.id}`}>#{r.number}</Link> },
            { title: t('clients.status'), render: (_, r) => <Tag color={statusColor[r.status]}>{t(`ticket.status.${r.status}`)}</Tag> },
            { title: t('clients.lastMessage'), dataIndex: 'lastMessagePreview', ellipsis: true },
            { title: t('ticket.assignee'), render: (_, r) => r.assigneeName ?? t('ticket.unassigned') },
            { title: t('clients.chats'), render: (_, r) => r.chats.length },
            { title: t('clients.created'), render: (_, r) => formatDateTime(r.createdAt) },
            { title: t('clients.lastActivity'), render: (_, r) => formatDateTime(r.lastMessageAt) },
          ]}
        />
      </Card>
    </Page>
  )
}
