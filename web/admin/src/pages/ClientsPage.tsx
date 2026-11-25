import { Card, Input, Select, Space, Switch, Table, Typography } from 'antd'
import { Page } from '../layout/Page'
import { keepPreviousData, useQuery } from '@tanstack/react-query'
import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Link } from 'react-router'
import { clientsApi, type ClientSummary } from '../api/clients'
import { settingsApi } from '../api/settings'
import { useAuth } from '../auth/AuthContext'
import { ClientStatusTag } from '../clients/ClientStatusTag'
import { formatDateTime } from '../format'

export function ClientsPage() {
  const { t } = useTranslation()
  const { user } = useAuth()
  const [search, setSearch] = useState('')
  const [siteId, setSiteId] = useState<string>()
  const [anonymous, setAnonymous] = useState(false)
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState(20)

  const query = { search, siteId, anonymous, page, pageSize }
  const { data, isFetching } = useQuery({
    queryKey: ['clients', query],
    queryFn: () => clientsApi.search(query),
    placeholderData: keepPreviousData,
  })
  // Список сайтов для фильтра доступен только администратору.
  const sites = useQuery({ queryKey: ['sites'], queryFn: settingsApi.sites, enabled: user?.role === 'Admin' })

  return (
    <Page crumbs={[{ title: t('nav.clients') }]}>
    <Card>
      <Space wrap style={{ marginBottom: 16 }}>
        <Input.Search
          allowClear
          placeholder={t('clients.search')}
          style={{ width: 280 }}
          onSearch={(value) => {
            setSearch(value.trim())
            setPage(1)
          }}
        />
        {sites.data && (
          <Select
            allowClear
            placeholder={t('clients.allSites')}
            style={{ width: 200 }}
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
            checked={anonymous}
            onChange={(v) => {
              setAnonymous(v)
              setPage(1)
            }}
          />
          <Typography.Text>{t('clients.showAnonymous')}</Typography.Text>
        </Space>
      </Space>

      <Table<ClientSummary>
        rowKey="id"
        loading={isFetching}
        dataSource={data?.items}
        pagination={{
          current: page,
          pageSize,
          total: data?.total,
          showSizeChanger: true,
          onChange: (p, size) => {
            setPage(p)
            setPageSize(size)
          },
        }}
        columns={[
          {
            title: t('clients.client'),
            render: (_, c) => <Link to={`/clients/${c.id}`}>{c.label}</Link>,
          },
          { title: t('client.site'), dataIndex: 'siteCode' },
          { title: t('clients.status'), render: (_, c) => <ClientStatusTag {...c} /> },
          { title: t('clients.locale'), dataIndex: 'locale', render: (v) => v ?? '—' },
          {
            title: t('clients.tickets'),
            render: (_, c) => (c.openTicketCount ? t('clients.ticketsOpen', { total: c.ticketCount, open: c.openTicketCount }) : c.ticketCount),
          },
          { title: t('clients.lastActivity'), render: (_, c) => formatDateTime(c.lastActivityAt) },
          { title: t('clients.created'), render: (_, c) => formatDateTime(c.createdAt) },
        ]}
      />
    </Card>
    </Page>
  )
}
