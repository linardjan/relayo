import { Alert, App as AntApp, Button, Empty, Form, Input, InputNumber, Popconfirm, Select, Space, Spin, Table, Tabs, Tag, Typography } from 'antd'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Link } from 'react-router'
import { settingsApi, type IdentificationMode } from '../../api/settings'
import { usedeskApi, type UsedeskConnection, type UsedeskConnectionInput, type UsedeskImportKind, type UsedeskImportResult, type UsedeskPreview, type UsedeskPreviewRow } from '../../api/usedesk'
import { Page, SectionCard, SettingsForm } from '../../layout/Page'
import { StatusBox } from '../../layout/StatusBox'
import { errorText } from '../../settings/errors'

/** Раздел предпросмотра: откуда строки и куда ведёт ссылка на перенесённое. */
const sections: { kind: UsedeskImportKind; rows: (p: UsedeskPreview) => UsedeskPreviewRow[]; link: (id: string) => string; needsSite: boolean }[] = [
  { kind: 'Users', rows: (p) => p.users, link: (id) => `/agents/${id}`, needsSite: false },
  { kind: 'Email', rows: (p) => p.email, link: (id) => `/channels/email/${id}`, needsSite: true },
  { kind: 'Telegram', rows: (p) => p.telegram, link: (id) => `/channels/telegram/${id}`, needsSite: true },
  { kind: 'WhatsApp', rows: (p) => p.whatsApp, link: (id) => `/channels/whatsapp/${id}`, needsSite: true },
  { kind: 'Widgets', rows: (p) => p.widgets, link: (id) => `/channels/widgets/${id}`, needsSite: true },
]

/**
 * Временный импорт из Usedesk: подключение к его базе (MySQL) и перенос сотрудников, почтовых каналов, Telegram-ботов,
 * номеров WhatsApp и виджетов. После переезда подключение удаляют кнопкой «Forget connection».
 */
export function UsedeskImportPage() {
  const { t } = useTranslation()
  const connection = useQuery({ queryKey: ['usedeskConnection'], queryFn: usedeskApi.connection })
  const crumbs = [{ title: t('nav.settings') }, { title: t('usedesk.title') }]
  if (connection.isLoading) return <Page crumbs={crumbs}><Spin style={{ margin: 48 }} /></Page>

  return (
    <Page crumbs={crumbs}>
      <ConnectionCard connection={connection.data ?? null} />
      {connection.data?.lastCheckOk && <ImportCard />}
    </Page>
  )
}

function ConnectionCard({ connection }: { connection: UsedeskConnection | null }) {
  const { t } = useTranslation()
  const { message } = AntApp.useApp()
  const queryClient = useQueryClient()
  const [form] = Form.useForm<UsedeskConnectionInput>()

  useEffect(() => {
    form.setFieldsValue(connection ? { ...connection, password: '' } : { host: '', port: 3306, database: 'usedesk', username: '', password: '' })
  }, [connection, form])

  const done = (c: UsedeskConnection) => {
    queryClient.setQueryData(['usedeskConnection'], c)
    void queryClient.invalidateQueries({ queryKey: ['usedeskPreview'] })
    form.setFieldValue('password', '')
  }
  const save = useMutation({
    mutationFn: (input: UsedeskConnectionInput) => usedeskApi.saveConnection({ ...input, password: input.password || undefined }),
    onSuccess: (c) => {
      done(c)
      message[c.lastCheckOk ? 'success' : 'warning'](c.lastCheckOk ? t('usedesk.savedConnected') : t('usedesk.savedNotConnected'))
    },
    onError: (e) => message.error(errorText(e, t('settings.saveFailed'))),
  })
  const check = useMutation({
    mutationFn: usedeskApi.check,
    onSuccess: done,
    onError: (e) => message.error(errorText(e, t('settings.saveFailed'))),
  })
  const forget = useMutation({
    mutationFn: usedeskApi.forget,
    onSuccess: () => {
      queryClient.setQueryData(['usedeskConnection'], null)
      queryClient.removeQueries({ queryKey: ['usedeskPreview'] })
      message.success(t('usedesk.forgotten'))
    },
    onError: (e) => message.error(errorText(e, t('settings.saveFailed'))),
  })

  return (
    <SectionCard title={t('usedesk.connection')}>
      <Alert type="info" showIcon title={t('usedesk.intro')} style={{ marginBottom: 16, maxWidth: 1000 }} />
      <SettingsForm<UsedeskConnectionInput>
        form={form}
        onFinish={(v) => save.mutate(v)}
        saving={save.isPending}
        actions={
          connection && (
            <>
              <Button onClick={() => check.mutate()} loading={check.isPending} disabled={save.isPending}>
                {t('ldap.check')}
              </Button>
              <Popconfirm title={t('usedesk.forgetConfirm')} description={t('usedesk.forgetHelp')} onConfirm={() => forget.mutate()}>
                <Button danger loading={forget.isPending}>{t('usedesk.forget')}</Button>
              </Popconfirm>
            </>
          )
        }
      >
        <Form.Item name="host" label={t('usedesk.host')} rules={[{ required: true, whitespace: true }]}>
          <Input className="medium-input" placeholder="usedesk-db.company.local" autoComplete="off" />
        </Form.Item>
        <Form.Item name="port" label={t('ldap.port')} rules={[{ required: true }]}>
          <InputNumber min={1} max={65535} style={{ width: 120 }} />
        </Form.Item>
        <Form.Item name="database" label={t('usedesk.database')} rules={[{ required: true, whitespace: true }]}>
          <Input className="medium-input" autoComplete="off" />
        </Form.Item>
        <Form.Item name="username" label={t('usedesk.username')} extra={t('usedesk.usernameHelp')} rules={[{ required: true, whitespace: true }]}>
          <Input className="medium-input" autoComplete="off" />
        </Form.Item>
        <Form.Item
          name="password"
          label={t('ldap.password')}
          extra={connection?.hasPassword ? t('ldap.passwordKeep') : undefined}
          rules={connection?.hasPassword ? [] : [{ required: true }]}
        >
          <Input.Password className="medium-input" autoComplete="new-password" placeholder={connection?.hasPassword ? '••••••••' : undefined} />
        </Form.Item>
        <Form.Item name="companyId" label={t('usedesk.companyId')} extra={t('usedesk.companyIdHelp')} rules={[{ required: true }]}>
          <InputNumber min={1} style={{ width: 160 }} />
        </Form.Item>
        {connection && (
          <Form.Item label={t('ldap.status')}>
            <StatusBox ok={connection.lastCheckOk} at={connection.lastCheckAt} text={connection.lastCheckMessage ?? t('ldap.notChecked')} />
          </Form.Item>
        )}
      </SettingsForm>
    </SectionCard>
  )
}

function ImportCard() {
  const { t } = useTranslation()
  const preview = useQuery({ queryKey: ['usedeskPreview'], queryFn: usedeskApi.preview })
  const sites = useQuery({ queryKey: ['sites'], queryFn: settingsApi.sites })
  const [siteId, setSiteId] = useState<string>()
  const [mode, setMode] = useState<IdentificationMode>('SignedToken')
  const [tab, setTab] = useState<UsedeskImportKind>('Users')

  useEffect(() => {
    if (!siteId && sites.data?.length) setSiteId(sites.data[0].id)
  }, [sites.data, siteId])

  return (
    <SectionCard
      title={t('usedesk.import')}
      extra={<Button onClick={() => void preview.refetch()} loading={preview.isFetching}>{t('usedesk.refresh')}</Button>}
    >
      <Space wrap size={24} style={{ marginBottom: 16 }}>
        <Space>
          <Typography.Text type="secondary">{t('usedesk.site')}</Typography.Text>
          <Select
            style={{ minWidth: 240 }}
            value={siteId}
            onChange={setSiteId}
            options={sites.data?.map((s) => ({ value: s.id, label: `${s.name} (${s.code})` }))}
          />
        </Space>
        {tab === 'Widgets' && (
          <Space>
            <Typography.Text type="secondary">{t('widgets.mode')}</Typography.Text>
            <Select
              style={{ minWidth: 220 }}
              value={mode}
              onChange={setMode}
              options={[
                { value: 'SignedToken', label: t('widgets.modeToken') },
                { value: 'Direct', label: t('widgets.modeDirect') },
              ]}
            />
          </Space>
        )}
      </Space>
      {preview.data?.warnings.map((w) => <Alert key={w} type="warning" showIcon title={w} style={{ marginBottom: 12 }} />)}
      {preview.isLoading ? (
        <Spin style={{ margin: 48 }} />
      ) : preview.isError ? (
        <Alert type="error" showIcon title={errorText(preview.error, t('usedesk.previewFailed'))} />
      ) : (
        preview.data && (
          <Tabs
            activeKey={tab}
            onChange={(k) => setTab(k as UsedeskImportKind)}
            items={sections.map((s) => {
              const rows = s.rows(preview.data)
              const imported = rows.filter((r) => r.importedId).length
              return {
                key: s.kind,
                label: `${t(`usedesk.kinds.${s.kind}`)} (${imported}/${rows.length})`,
                children: <Section kind={s.kind} rows={rows} link={s.link} siteId={s.needsSite ? siteId : undefined} needsSite={s.needsSite} mode={mode} />,
              }
            })}
          />
        )
      )}
    </SectionCard>
  )
}

function Section({
  kind,
  rows,
  link,
  siteId,
  needsSite,
  mode,
}: {
  kind: UsedeskImportKind
  rows: UsedeskPreviewRow[]
  link: (id: string) => string
  siteId?: string
  needsSite: boolean
  mode: IdentificationMode
}) {
  const { t } = useTranslation()
  const { message } = AntApp.useApp()
  const queryClient = useQueryClient()
  const [selected, setSelected] = useState<number[]>([])
  const [result, setResult] = useState<UsedeskImportResult>()

  const run = useMutation({
    mutationFn: () => usedeskApi.import(kind, selected, siteId, kind === 'Widgets' ? mode : undefined),
    onSuccess: (r) => {
      setResult(r)
      setSelected([])
      message[r.skipped.length ? 'warning' : 'success'](t('usedesk.imported', { count: r.imported, skipped: r.skipped.length }))
      void queryClient.invalidateQueries({ queryKey: ['usedeskPreview'] })
      for (const key of ['users', 'widgets', 'telegramBots', 'whatsAppChannels', 'emailChannels']) void queryClient.invalidateQueries({ queryKey: [key] })
    },
    onError: (e) => message.error(errorText(e, t('usedesk.importFailed'))),
  })

  if (rows.length === 0) return <Empty description={t('usedesk.nothing')} />

  return (
    <>
      <Alert type="info" showIcon title={t(`usedesk.hints.${kind}`)} style={{ marginBottom: 12 }} />
      {result && result.skipped.length > 0 && (
        <Alert
          type="warning"
          showIcon
          closable
          onClose={() => setResult(undefined)}
          style={{ marginBottom: 12 }}
          title={t('usedesk.skippedTitle')}
          description={
            <ul className="usedesk-skipped">
              {result.skipped.map((s) => (
                <li key={s.id}>
                  {s.name} (#{s.id}): {s.reason}
                </li>
              ))}
            </ul>
          }
        />
      )}
      <Table<UsedeskPreviewRow>
        rowKey="id"
        size="small"
        pagination={rows.length > 50 ? { pageSize: 50 } : false}
        dataSource={rows}
        rowSelection={{
          selectedRowKeys: selected,
          onChange: (keys) => setSelected(keys as number[]),
          getCheckboxProps: (r) => ({ disabled: !!r.importedId || !!r.problem }),
        }}
        columns={[
          { title: 'ID', dataIndex: 'id', width: 80 },
          {
            title: t('usedesk.name'),
            dataIndex: 'name',
            render: (name: string, r) => (
              <>
                {name} {r.deleted && <Tag>{t('usedesk.deleted')}</Tag>}
              </>
            ),
          },
          { title: t('usedesk.details'), dataIndex: 'details', render: (d: string) => <Typography.Text type="secondary">{d}</Typography.Text> },
          {
            title: t('usedesk.status'),
            width: 260,
            render: (_, r) =>
              r.importedId ? (
                <Link to={link(r.importedId)}>{t('usedesk.statusImported')}</Link>
              ) : r.problem ? (
                <Typography.Text type="danger">{r.problem}</Typography.Text>
              ) : (
                <Tag color="blue">{t('usedesk.statusNew')}</Tag>
              ),
          },
          {
            title: t('usedesk.notes'),
            dataIndex: 'notes',
            render: (notes: string[]) => (
              <ul className="usedesk-notes">
                {notes.map((n) => <li key={n}>{n}</li>)}
              </ul>
            ),
          },
        ]}
      />
      <Space style={{ marginTop: 12 }}>
        <Button type="primary" disabled={selected.length === 0 || (needsSite && !siteId)} loading={run.isPending} onClick={() => run.mutate()}>
          {t('usedesk.importSelected', { count: selected.length })}
        </Button>
        {needsSite && !siteId && <Typography.Text type="warning">{t('usedesk.needSite')}</Typography.Text>}
      </Space>
    </>
  )
}
