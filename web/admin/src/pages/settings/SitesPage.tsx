import { Button, Card, Table, Tag, Typography } from 'antd'
import { PlusOutlined } from '@ant-design/icons'
import { useQuery } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { Link, useNavigate } from 'react-router'
import { settingsApi, type Site } from '../../api/settings'
import { Page } from '../../layout/Page'

/** Сайты (product), их домены и зеркала (п. 3.3 ТЗ). */
export function SitesPage() {
  const { t } = useTranslation()
  const navigate = useNavigate()
  const { data, isLoading } = useQuery({ queryKey: ['sites'], queryFn: settingsApi.sites })

  return (
    <Page
      crumbs={[{ title: t('nav.settings') }, { title: t('settings.sites') }]}
      extra={
        <Button type="primary" icon={<PlusOutlined />} onClick={() => navigate('/settings/sites/new')}>
          {t('sites.add')}
        </Button>
      }
    >
      <Card>
        <Table<Site>
          rowKey="id"
          loading={isLoading}
          dataSource={data}
          pagination={false}
          onRow={(s) => ({ onDoubleClick: () => navigate(`/settings/sites/${s.id}`) })}
          columns={[
            { title: t('sites.name'), render: (_, s) => <Link to={`/settings/sites/${s.id}`}>{s.name}</Link> },
            { title: t('sites.code'), dataIndex: 'code', render: (v) => <Typography.Text code>{v}</Typography.Text> },
            { title: t('sites.domains'), render: (_, s) => s.allowedOrigins.map((o) => <Tag key={o}>{o}</Tag>) },
            { title: t('sites.activeKeys'), render: (_, s) => s.keys.filter((k) => !k.revokedAt).length },
            { title: t('sites.widgets'), dataIndex: 'widgetCount' },
          ]}
        />
      </Card>
    </Page>
  )
}
