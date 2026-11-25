import { Alert, App as AntApp, Button, Empty, Form, Input, Modal, Popconfirm, Spin, Table, Tag, Typography } from 'antd'
import { KeyOutlined } from '@ant-design/icons'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { useNavigate, useParams } from 'react-router'
import { settingsApi } from '../../api/settings'
import { formatDateTime } from '../../format'
import { Page, SectionCard, SettingsForm } from '../../layout/Page'
import { errorText } from '../../settings/errors'

interface SiteForm {
  code: string
  name: string
  origins: string
}

/** Сайт: код product, домены и зеркала; отдельным блоком — ключи подписи токена клиента (п. 3.3.1 ТЗ). */
export function SitePage() {
  const { t } = useTranslation()
  const { message } = AntApp.useApp()
  const { siteId } = useParams()
  const isNew = siteId === undefined
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const { data, isLoading } = useQuery({ queryKey: ['sites'], queryFn: settingsApi.sites })
  const site = data?.find((s) => s.id === siteId)
  const [secret, setSecret] = useState<{ keyId: string; secret: string } | null>(null)
  const [form] = Form.useForm<SiteForm>()

  useEffect(() => {
    if (isNew) form.setFieldsValue({ code: '', name: '', origins: '' })
    else if (site) form.setFieldsValue({ code: site.code, name: site.name, origins: site.allowedOrigins.join('\n') })
  }, [form, isNew, site])

  const refresh = () => queryClient.invalidateQueries({ queryKey: ['sites'] })

  const save = useMutation({
    mutationFn: (v: SiteForm) => {
      const origins = v.origins.split(/[\s,]+/).filter(Boolean)
      return isNew ? settingsApi.createSite(v.code, v.name, origins) : settingsApi.updateSite(siteId, v.name, origins)
    },
    onSuccess: (saved) => {
      void refresh()
      message.success(t('settings.saved'))
      // Новому сайту сразу нужен ключ: остаёмся на его странице.
      if (isNew) navigate(`/settings/sites/${saved.id}`, { replace: true })
    },
    onError: (e) => message.error(errorText(e, t('settings.saveFailed'))),
  })

  const createKey = useMutation({
    mutationFn: () => settingsApi.createKey(siteId!),
    onSuccess: (r) => {
      void refresh()
      setSecret({ keyId: r.key.keyId, secret: r.secret })
    },
    onError: (e) => message.error(errorText(e, t('settings.saveFailed'))),
  })

  const revokeKey = useMutation({
    mutationFn: (keyId: string) => settingsApi.revokeKey(siteId!, keyId),
    onSuccess: () => void refresh(),
    onError: (e) => message.error(errorText(e, t('settings.saveFailed'))),
  })

  const crumbs = [
    { title: t('nav.settings') },
    { title: t('settings.sites'), to: '/settings/sites' },
    { title: isNew ? t('sites.add') : (site?.name ?? '…') },
  ]
  if (isLoading) return <Page crumbs={crumbs}><Spin style={{ margin: 48 }} /></Page>
  if (!isNew && !site) return <Page crumbs={crumbs}><Empty description={t('sites.notFound')} /></Page>

  return (
    <Page crumbs={crumbs}>
      <SectionCard title={t('sites.site')}>
        <SettingsForm<SiteForm>
          form={form}
          onFinish={(v) => save.mutate(v)}
          saving={save.isPending}
          onCancel={() => navigate('/settings/sites')}
        >
          <Form.Item name="name" label={t('sites.name')} rules={[{ required: true, max: 200 }]}>
            <Input />
          </Form.Item>
          <Form.Item
            name="code"
            label={t('sites.code')}
            extra={t('sites.codeHelp')}
            rules={[{ required: true, pattern: /^[a-z0-9][a-z0-9_-]{0,63}$/, message: t('sites.codeInvalid') }]}
          >
            <Input disabled={!isNew} />
          </Form.Item>
          <Form.Item name="origins" label={t('sites.domains')} extra={t('sites.domainsHelp')} rules={[{ required: true }]}>
            <Input.TextArea autoSize={{ minRows: 3, maxRows: 10 }} placeholder={'https://example.com\nhttps://mirror.example.com'} />
          </Form.Item>
          {!isNew && (
            <Form.Item label=" ">
              <Typography.Text type="secondary">{t('sites.cacheNote')}</Typography.Text>
            </Form.Item>
          )}
        </SettingsForm>
      </SectionCard>

      {site && (
        <SectionCard
          title={t('sites.keys')}
          extra={
            <Button icon={<KeyOutlined />} onClick={() => createKey.mutate()} loading={createKey.isPending}>
              {t('sites.createKey')}
            </Button>
          }
        >
          <Typography.Paragraph type="secondary">{t('sites.keysHelp')}</Typography.Paragraph>
          <Table
            size="small"
            rowKey="id"
            pagination={false}
            dataSource={site.keys}
            locale={{ emptyText: t('sites.noKeys') }}
            columns={[
              { title: 'kid', dataIndex: 'keyId', render: (v) => <Typography.Text code copyable>{v}</Typography.Text> },
              { title: t('clients.created'), render: (_, k) => formatDateTime(k.createdAt) },
              {
                title: t('clients.status'),
                render: (_, k) => (k.revokedAt ? <Tag>{t('sites.revoked')}</Tag> : <Tag color="success">{t('sites.active')}</Tag>),
              },
              {
                align: 'right',
                render: (_, k) =>
                  !k.revokedAt && (
                    <Popconfirm title={t('sites.revokeConfirm')} onConfirm={() => revokeKey.mutate(k.id)}>
                      <Button size="small" danger>
                        {t('sites.revoke')}
                      </Button>
                    </Popconfirm>
                  ),
              },
            ]}
          />
        </SectionCard>
      )}

      <Modal
        open={secret !== null}
        title={t('sites.secretTitle')}
        closable={false}
        mask={{ closable: false }}
        footer={<Button type="primary" onClick={() => setSecret(null)}>{t('sites.secretSaved')}</Button>}
      >
        <Alert type="warning" showIcon style={{ marginBottom: 16 }} title={t('sites.secretWarning')} />
        <p>
          kid: <Typography.Text code copyable>{secret?.keyId}</Typography.Text>
        </p>
        <p>
          {t('sites.secret')}: <Typography.Text code copyable>{secret?.secret}</Typography.Text>
        </p>
      </Modal>
    </Page>
  )
}
