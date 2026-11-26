import { Button, Card, Empty, Input, Pagination, Select, Space, Spin, Switch, Tag, Tooltip, Typography } from 'antd'
import { EditOutlined, PlusOutlined, SearchOutlined } from '@ant-design/icons'
import { keepPreviousData, useQuery } from '@tanstack/react-query'
import { useEffect, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Link } from 'react-router'
import { clientsApi, type ClientSummary } from '../api/clients'
import { settingsApi } from '../api/settings'
import { useAuth } from '../auth/AuthContext'
import { ClientStatusTag } from '../clients/ClientStatusTag'
import { formatDateTime } from '../format'
import { PersonAvatar } from '../inbox/visuals'
import { Page } from '../layout/Page'

/**
 * Список клиентов, как в Usedesk: строка поиска во всю ширину и карточки клиентов с аватаром и кнопкой
 * редактирования. Поиск — по имени и clientId, по мере ввода.
 */
export function ClientsPage() {
  const { t } = useTranslation()
  const { user } = useAuth()
  const [text, setText] = useState('')
  const [search, setSearch] = useState('')
  const [siteId, setSiteId] = useState<string>()
  const [anonymous, setAnonymous] = useState(false)
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState(20)

  // Запрос уходит, когда пользователь перестал печатать.
  useEffect(() => {
    const timer = setTimeout(() => {
      setSearch(text.trim())
      setPage(1)
    }, 300)
    return () => clearTimeout(timer)
  }, [text])

  const query = { search, siteId, anonymous, page, pageSize }
  const { data, isLoading, isFetching } = useQuery({
    queryKey: ['clients', query],
    queryFn: () => clientsApi.search(query),
    placeholderData: keepPreviousData,
  })
  // Список сайтов для фильтра доступен только администратору.
  const sites = useQuery({ queryKey: ['sites'], queryFn: settingsApi.sites, enabled: user?.role === 'Admin' })

  return (
    <Page
      crumbs={[{ title: t('nav.clients') }]}
      extra={
        <Link to="/clients/new">
          <Button type="primary" icon={<PlusOutlined />}>
            {t('clients.create')}
          </Button>
        </Link>
      }
    >
      <Card>
        <Input
          size="large"
          allowClear
          value={text}
          onChange={(e) => setText(e.target.value)}
          placeholder={t('clients.search')}
          suffix={<SearchOutlined style={{ color: '#9aa0a7' }} />}
          className="client-search"
        />
        <Space wrap size={16} style={{ margin: '12px 0 16px' }}>
          {sites.data && (
            <Select
              allowClear
              placeholder={t('clients.allSites')}
              style={{ width: 220 }}
              value={siteId}
              onChange={(v) => {
                setSiteId(v)
                setPage(1)
              }}
              options={sites.data.map((s) => ({ value: s.id, label: `${s.name} (${s.code})` }))}
            />
          )}
          <Space>
            <Switch
              size="small"
              checked={anonymous}
              onChange={(v) => {
                setAnonymous(v)
                setPage(1)
              }}
            />
            <Typography.Text type="secondary">{t('clients.showAnonymous')}</Typography.Text>
          </Space>
          {isFetching && !isLoading && <Spin size="small" />}
        </Space>

        {isLoading ? (
          <Spin style={{ display: 'block', margin: 48 }} />
        ) : !data?.items.length ? (
          <Empty description={t('clients.empty')} />
        ) : (
          <>
            <div className="client-cards">
              {data.items.map((c) => (
                <ClientCard key={c.id} client={c} />
              ))}
            </div>
            <Pagination
              style={{ marginTop: 16, textAlign: 'right' }}
              current={page}
              pageSize={pageSize}
              total={data.total}
              showSizeChanger
              onChange={(p, size) => {
                setPage(p)
                setPageSize(size)
              }}
            />
          </>
        )}
      </Card>
    </Page>
  )
}

function ClientCard({ client: c }: { client: ClientSummary }) {
  const { t } = useTranslation()
  const meta = [c.siteCode, c.externalClientId ? `clientId ${c.externalClientId}` : null, c.locale, c.position].filter(Boolean)
  return (
    <div className="client-card">
      <Link to={`/clients/${c.id}`}>
        <PersonAvatar name={c.label} seed={c.id} size={72} src={c.avatarUrl} />
      </Link>
      <div className="client-card-body">
        <div className="client-card-title">
          <Link to={`/clients/${c.id}`}>{c.label}</Link>
          {c.isVip && <Tag color="gold">VIP</Tag>}
          {c.isSpammer && <Tag color="red">{t('clients.spammer')}</Tag>}
          <ClientStatusTag {...c} />
        </div>
        <Typography.Text type="secondary">{meta.join(' · ')}</Typography.Text>
        {c.note && (
          <Typography.Paragraph type="secondary" ellipsis={{ rows: 1 }} className="client-card-note">
            {c.note}
          </Typography.Paragraph>
        )}
      </div>
      <div className="client-card-stats">
        <div>
          {t('clients.tickets')}:{' '}
          {c.openTicketCount ? t('clients.ticketsOpen', { total: c.ticketCount, open: c.openTicketCount }) : c.ticketCount}
        </div>
        <Typography.Text type="secondary">
          {t('clients.lastActivity')}: {formatDateTime(c.lastActivityAt ?? c.createdAt)}
        </Typography.Text>
      </div>
      <Tooltip title={t('clients.edit')}>
        <Link to={`/clients/${c.id}`} className="client-card-edit" aria-label={t('clients.edit')}>
          <EditOutlined />
        </Link>
      </Tooltip>
    </div>
  )
}
