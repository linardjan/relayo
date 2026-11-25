import { Alert, App as AntApp, Button, ColorPicker, Empty, Form, Input, Popconfirm, Radio, Select, Space, Spin, Table, Typography } from 'antd'
import { CloudUploadOutlined } from '@ant-design/icons'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect } from 'react'
import { useTranslation } from 'react-i18next'
import { useNavigate, useParams } from 'react-router'
import { settingsApi, type WidgetInput, type WidgetRow } from '../../api/settings'
import { formatDateTime } from '../../format'
import { Page, SectionCard, SettingsForm } from '../../layout/Page'
import { errorText } from '../../settings/errors'

type FormValues = WidgetInput & { siteId: string }

const defaults: Omit<FormValues, 'siteId' | 'name'> = {
  identificationMode: 'SignedToken',
  title: 'Support',
  greeting: 'Hi! How can we help you?',
  accentColor: '#1677ff',
  position: 'Right',
  defaultLocale: 'en',
  telegramBotId: null,
}

/** Настройки виджета (п. 3.5 ТЗ) и код вставки. Полный конструктор с превью — этап 4. */
export function WidgetPage() {
  const { t } = useTranslation()
  const { message } = AntApp.useApp()
  const { widgetId } = useParams()
  const isNew = widgetId === undefined
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const widgets = useQuery({ queryKey: ['widgets'], queryFn: settingsApi.widgets })
  const sites = useQuery({ queryKey: ['sites'], queryFn: settingsApi.sites })
  const bots = useQuery({ queryKey: ['telegramBots'], queryFn: settingsApi.telegramBots })
  const row = widgets.data?.find((w) => w.widget.id === widgetId)
  const [form] = Form.useForm<FormValues>()
  const mode = Form.useWatch('identificationMode', form)
  // Форму заполняем, только когда она уже на странице (пока идёт загрузка, вместо неё спиннер).
  const loading = widgets.isLoading || sites.isLoading

  useEffect(() => {
    if (loading) return
    if (isNew && sites.data) form.setFieldsValue({ ...defaults, name: '', siteId: sites.data[0]?.id })
    if (!isNew && row) form.setFieldsValue({ ...row.widget })
  }, [form, isNew, row, sites.data, loading])

  const save = useMutation({
    mutationFn: ({ siteId, ...input }: FormValues) =>
      isNew ? settingsApi.createWidget(siteId, input) : settingsApi.updateWidget(widgetId, input),
    onSuccess: (saved) => {
      void queryClient.invalidateQueries({ queryKey: ['widgets'] })
      void queryClient.invalidateQueries({ queryKey: ['sites'] })
      void queryClient.invalidateQueries({ queryKey: ['telegramBots'] })
      message.success(t('settings.saved'))
      // После создания остаёмся на странице виджета: там код вставки.
      if (isNew) navigate(`/channels/widgets/${saved.widget.id}`, { replace: true })
    },
    onError: (e) => message.error(errorText(e, t('settings.saveFailed'))),
  })

  const crumbs = [{ title: t('nav.channels'), to: '/channels' }, { title: isNew ? t('widgets.add') : (row?.widget.name ?? '…') }]
  if (loading) return <Page crumbs={crumbs}><Spin style={{ margin: 48 }} /></Page>
  if (!isNew && !row) return <Page crumbs={crumbs}><Empty description={t('channels.notFound')} /></Page>

  return (
    <Page crumbs={crumbs}>
      <SectionCard title={t('channels.widget')}>
        <SettingsForm<FormValues>
          form={form}
          onFinish={(v) => save.mutate(v)}
          saving={save.isPending}
          onCancel={() => navigate('/channels')}
        >
          <Form.Item name="name" label={t('widgets.name')} rules={[{ required: true, max: 200 }]}>
            <Input placeholder={t('widgets.namePlaceholder')} />
          </Form.Item>
          <Form.Item name="siteId" label={t('client.site')} rules={[{ required: true }]} extra={isNew ? undefined : t('widgets.siteFixed')}>
            <Select disabled={!isNew} options={sites.data?.map((s) => ({ value: s.id, label: `${s.name} (${s.code})` }))} />
          </Form.Item>
          <Form.Item name="defaultLocale" label={t('widgets.locale')} rules={[{ required: true }]}>
            <Select options={[{ value: 'en', label: 'English' }]} />
          </Form.Item>
          <Form.Item name="accentColor" label={t('widgets.color')} getValueFromEvent={(color) => `#${color.toHex().slice(0, 6)}`}>
            <ColorPicker disabledAlpha showText />
          </Form.Item>
          <Form.Item name="title" label={t('widgets.title')} rules={[{ required: true, max: 100 }]}>
            <Input />
          </Form.Item>
          <Form.Item name="greeting" label={t('widgets.greeting')} rules={[{ max: 1000 }]}>
            <Input.TextArea autoSize={{ minRows: 2, maxRows: 6 }} />
          </Form.Item>
          <Form.Item name="position" label={t('widgets.position')}>
            <Radio.Group
              optionType="button"
              options={[
                { value: 'Left', label: t('widgets.left') },
                { value: 'Right', label: t('widgets.right') },
              ]}
            />
          </Form.Item>
          <Form.Item name="identificationMode" label={t('widgets.mode')} extra={mode === 'Direct' ? undefined : t('widgets.modeTokenHelp')}>
            <Radio.Group
              options={[
                { value: 'SignedToken', label: t('widgets.modeToken') },
                { value: 'Direct', label: t('widgets.modeDirect') },
              ]}
            />
          </Form.Item>
          {mode === 'Direct' && (
            <Form.Item label=" ">
              <Alert type="warning" showIcon title={t('widgets.directWarning')} />
            </Form.Item>
          )}
          <Form.Item
            name="telegramBotId"
            label={t('widgets.telegramBot')}
            extra={t('widgets.telegramHelp')}
            getValueFromEvent={(value) => value ?? null}
          >
            <Select
              allowClear
              placeholder={t('widgets.telegramNone')}
              notFoundContent={t('widgets.noBots')}
              options={bots.data?.map((b) => ({
                value: b.id,
                label: `${b.name} (@${b.username})${b.isEnabled ? '' : ` · ${t('telegram.disabled')}`}`,
              }))}
            />
          </Form.Item>
          {!isNew && (
            <Form.Item label=" ">
              <Typography.Text type="secondary">{t('widgets.publishNote')}</Typography.Text>
            </Form.Item>
          )}
        </SettingsForm>
      </SectionCard>

      {row && <PublishCard row={row} />}

      {row && (
        <SectionCard title={t('widgets.embedCode')}>
          {row.fileUrl && !row.widget.publishedVersion ? (
            <Alert type="warning" showIcon title={t('widgets.publishFirst')} />
          ) : (
            <>
              <Typography.Paragraph>{t('widgets.embedHelp')}</Typography.Paragraph>
              <Typography.Paragraph copyable={{ text: row.embedCode }} code style={{ fontSize: 12, wordBreak: 'break-all' }}>
                {row.embedCode}
              </Typography.Paragraph>
            </>
          )}
          <Typography.Paragraph type="secondary" style={{ marginBottom: 4 }}>
            {row.widget.identificationMode === 'SignedToken' ? t('widgets.embedTokenHint') : t('widgets.embedDirectHint')}{' '}
            {t('widgets.embedOpenHint')}
          </Typography.Paragraph>
          <Typography.Paragraph type="secondary" style={{ marginBottom: 4 }}>
            {t('widgets.embedReady')}
          </Typography.Paragraph>
          <Typography.Paragraph code style={{ fontSize: 12 }}>
            {row.widget.identificationMode === 'SignedToken'
              ? "window.addEventListener('relayo:ready', () => Relayo.identify(token))"
              : "window.addEventListener('relayo:ready', () => Relayo.setClientId(id))"}
          </Typography.Paragraph>
          <Typography.Text type="secondary">
            ID: <Typography.Text code copyable>{row.widget.id}</Typography.Text>
          </Typography.Text>
        </SectionCard>
      )}
    </Page>
  )
}

/**
 * Публикация в S3: сайты получают настройки виджета из файла widget_<ключ>.js,
 * поэтому сохранённые изменения попадают на сайты только после публикации.
 */
function PublishCard({ row }: { row: WidgetRow }) {
  const { t } = useTranslation()
  const { message } = AntApp.useApp()
  const queryClient = useQueryClient()
  const { widget } = row
  const versions = useQuery({
    queryKey: ['widgetVersions', widget.id],
    queryFn: () => settingsApi.widgetVersions(widget.id),
    enabled: !!row.fileUrl,
  })
  const done = (updated: WidgetRow, text: string) => {
    queryClient.setQueryData<WidgetRow[]>(['widgets'], (old) => old?.map((r) => (r.widget.id === updated.widget.id ? updated : r)))
    void queryClient.invalidateQueries({ queryKey: ['widgetVersions', widget.id] })
    message.success(text)
  }
  const publish = useMutation({
    mutationFn: () => settingsApi.publishWidget(widget.id),
    onSuccess: (r) => done(r, t('widgets.published', { version: r.widget.publishedVersion })),
    onError: (e) => message.error(errorText(e, t('settings.saveFailed'))),
  })
  const rollback = useMutation({
    mutationFn: (version: number) => settingsApi.rollbackWidget(widget.id, version),
    onSuccess: (r) => done(r, t('widgets.rolledBack', { version: r.widget.publishedVersion })),
    onError: (e) => message.error(errorText(e, t('settings.saveFailed'))),
  })

  if (!row.fileUrl)
    return (
      <SectionCard title={t('widgets.publication')}>
        <Alert type="info" showIcon title={t('widgets.storageOff')} />
      </SectionCard>
    )

  return (
    <SectionCard
      title={t('widgets.publication')}
      extra={
        <Button type="primary" icon={<CloudUploadOutlined />} loading={publish.isPending} onClick={() => publish.mutate()}>
          {t('widgets.publish')}
        </Button>
      }
    >
      <Space orientation="vertical" size={12} style={{ width: '100%' }}>
        {!widget.publishedVersion ? (
          <Alert type="warning" showIcon title={t('widgets.notPublished')} />
        ) : widget.hasUnpublishedChanges ? (
          <Alert type="warning" showIcon title={t('widgets.unpublishedChanges', { version: widget.publishedVersion })} />
        ) : (
          <Alert type="success" showIcon title={t('widgets.upToDate', { version: widget.publishedVersion, date: formatDateTime(widget.publishedAt) })} />
        )}
        <Typography.Text type="secondary">
          {t('widgets.fileUrl')}: <Typography.Text copyable={{ text: row.fileUrl }}>{row.fileUrl}</Typography.Text>
        </Typography.Text>
        {(versions.data?.length ?? 0) > 0 && (
          <Table
            size="small"
            rowKey="version"
            pagination={{ pageSize: 5, hideOnSinglePage: true }}
            dataSource={versions.data}
            columns={[
              { title: t('widgets.version'), render: (_, v) => `v${v.version}` },
              { title: t('widgets.publishedAt'), render: (_, v) => formatDateTime(v.publishedAt) },
              { title: t('widgets.publishedBy'), dataIndex: 'publishedBy' },
              { title: t('widgets.note'), render: (_, v) => v.note ?? '' },
              {
                align: 'right',
                render: (_, v) =>
                  v.version !== widget.publishedVersion && (
                    <Popconfirm title={t('widgets.rollbackConfirm', { version: v.version })} onConfirm={() => rollback.mutate(v.version)}>
                      <Button size="small" loading={rollback.isPending && rollback.variables === v.version}>
                        {t('widgets.rollback')}
                      </Button>
                    </Popconfirm>
                  ),
              },
            ]}
          />
        )}
      </Space>
    </SectionCard>
  )
}
