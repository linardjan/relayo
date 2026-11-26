import {
  App as AntApp,
  Button,
  Checkbox,
  Empty,
  Form,
  Input,
  InputNumber,
  Popconfirm,
  QRCode,
  Radio,
  Select,
  Space,
  Spin,
  Switch,
  Typography,
} from 'antd'
import { CheckCircleFilled, QrcodeOutlined } from '@ant-design/icons'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect, useRef } from 'react'
import { useTranslation } from 'react-i18next'
import { useNavigate, useParams } from 'react-router'
import { settingsApi, type WhatsAppChannel, type WhatsAppChannelInput, type WhatsAppConnection } from '../../api/settings'
import { formatDateTime } from '../../format'
import { Page, SectionCard, SettingsForm, SoonField } from '../../layout/Page'
import { errorText } from '../../settings/errors'

/**
 * Канал WhatsApp. Раскладка повторяет страницу канала WhatsApp в Usedesk: название, QR-код подключения,
 * настройки распределения, внизу «Save», «Disconnect» и «Delete».
 * Два режима (п. 3.4 ТЗ): «QR connection» — номер подключается как связанное устройство через шлюз и переписка
 * идёт в Relayo; «Link only» — виджет только открывает wa.me/<номер>.
 */
export function WhatsAppChannelPage() {
  const { t } = useTranslation()
  const { message } = AntApp.useApp()
  const { channelId } = useParams()
  const isNew = channelId === undefined
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const channels = useQuery({ queryKey: ['whatsAppChannels'], queryFn: settingsApi.whatsAppChannels })
  const sites = useQuery({ queryKey: ['sites'], queryFn: settingsApi.sites })
  const channel = channels.data?.find((c) => c.id === channelId)
  const [form] = Form.useForm<WhatsAppChannelInput>()
  const mode = Form.useWatch('mode', form)
  const loading = channels.isLoading || sites.isLoading
  const connectAfterCreate = useRef(false)

  useEffect(() => {
    if (loading) return
    if (isNew && sites.data)
      form.setFieldsValue({ name: '', mode: 'Gateway', phoneNumber: '', siteId: sites.data[0]?.id, isEnabled: true })
    if (!isNew && channel)
      form.setFieldsValue({
        name: channel.name,
        mode: channel.mode,
        phoneNumber: channel.phoneNumber ? `+${channel.phoneNumber}` : '',
        siteId: channel.siteId,
        isEnabled: channel.isEnabled,
      })
  }, [form, isNew, channel, sites.data, loading])

  const refreshChannels = () => queryClient.invalidateQueries({ queryKey: ['whatsAppChannels'] })

  const connect = useMutation({
    mutationFn: (id: string) => settingsApi.connectWhatsApp(id),
    onSuccess: (state, id) => queryClient.setQueryData(['whatsAppConnection', id], state),
    onError: (e) => message.error(errorText(e, t('whatsapp.connectFailed'))),
  })

  const save = useMutation({
    mutationFn: (input: WhatsAppChannelInput) => {
      const body = { ...input, phoneNumber: input.phoneNumber?.trim() || null }
      return isNew ? settingsApi.createWhatsAppChannel(body) : settingsApi.updateWhatsAppChannel(channelId, body)
    },
    onSuccess: (saved) => {
      void refreshChannels()
      message.success(t('settings.saved'))
      if (isNew) {
        // Новый номер в режиме QR сразу показывает QR-код: дальше — отсканировать его телефоном.
        connectAfterCreate.current = saved.mode === 'Gateway'
        navigate(`/channels/whatsapp/${saved.id}`, { replace: true })
      }
    },
    onError: (e) => message.error(errorText(e, t('settings.saveFailed'))),
  })

  useEffect(() => {
    if (channel && connectAfterCreate.current) {
      connectAfterCreate.current = false
      connect.mutate(channel.id)
    }
  }, [channel, connect])

  const disconnect = useMutation({
    mutationFn: () => settingsApi.disconnectWhatsApp(channelId!),
    onSuccess: () => {
      void refreshChannels()
      void queryClient.invalidateQueries({ queryKey: ['whatsAppConnection', channelId] })
      message.success(t('whatsapp.disconnected'))
    },
    onError: (e) => message.error(errorText(e, t('settings.saveFailed'))),
  })

  const remove = useMutation({
    mutationFn: () => settingsApi.deleteWhatsAppChannel(channelId!),
    onSuccess: () => {
      void refreshChannels()
      void queryClient.invalidateQueries({ queryKey: ['widgets'] })
      message.success(t('whatsapp.deleted'))
      navigate('/channels')
    },
    onError: (e) => message.error(errorText(e, t('settings.saveFailed'))),
  })

  const crumbs = [
    { title: t('nav.channels'), to: '/channels' },
    { title: isNew ? t('whatsapp.add') : `${channel?.name ?? '…'} (WhatsApp)` },
  ]
  if (loading) return <Page crumbs={crumbs}><Spin style={{ margin: 48 }} /></Page>
  if (!isNew && !channel) return <Page crumbs={crumbs}><Empty description={t('channels.notFound')} /></Page>

  const gatewayMode = (mode ?? channel?.mode) === 'Gateway'

  return (
    <Page crumbs={crumbs}>
      <SectionCard title="WhatsApp">
        {isNew && (
          <Typography.Paragraph type="secondary" style={{ maxWidth: 1000 }}>
            {t('whatsapp.intro')}
          </Typography.Paragraph>
        )}
        <SettingsForm<WhatsAppChannelInput>
          form={form}
          onFinish={(v) => save.mutate(v)}
          saving={save.isPending}
          onCancel={() => navigate('/channels')}
          actions={
            channel && (
              <>
                {channel.mode === 'Gateway' && (
                  <Popconfirm title={t('whatsapp.disconnectConfirm')} description={t('whatsapp.disconnectHelp')} onConfirm={() => disconnect.mutate()}>
                    <Button loading={disconnect.isPending}>{t('whatsapp.disconnect')}</Button>
                  </Popconfirm>
                )}
                <Popconfirm title={t('whatsapp.deleteConfirm')} description={t('whatsapp.deleteHelp')} onConfirm={() => remove.mutate()}>
                  <Button danger type="primary" loading={remove.isPending}>
                    {t('whatsapp.delete')}
                  </Button>
                </Popconfirm>
              </>
            )
          }
        >
          <Form.Item name="name" label={t('whatsapp.name')} rules={[{ required: true, whitespace: true, max: 200 }]}>
            <Input className="medium-input" />
          </Form.Item>
          <Form.Item name="mode" label={t('whatsapp.mode')} extra={gatewayMode ? t('whatsapp.modeGatewayHelp') : t('whatsapp.modeLinkOnlyHelp')}>
            <Radio.Group
              disabled={channel?.isConnected}
              options={[
                { value: 'Gateway', label: t('whatsapp.modeGateway') },
                { value: 'LinkOnly', label: t('whatsapp.modeLinkOnly') },
              ]}
            />
          </Form.Item>
          {gatewayMode && channel?.mode === 'Gateway' && (
            <Form.Item label={t('whatsapp.qr')}>
              <Connection channel={channel} onConnect={() => connect.mutate(channel.id)} connecting={connect.isPending} onChanged={refreshChannels} />
            </Form.Item>
          )}
          {gatewayMode && channel?.mode !== 'Gateway' && (
            <Form.Item label={t('whatsapp.qr')}>
              <Typography.Text type="secondary">{t('whatsapp.saveToConnect')}</Typography.Text>
            </Form.Item>
          )}
          {gatewayMode ? (
            <Form.Item label={t('whatsapp.phone')} extra={t('whatsapp.phoneFromWhatsApp')}>
              <Typography.Text>{channel?.phoneNumber ? `+${channel.phoneNumber}` : '—'}</Typography.Text>
            </Form.Item>
          ) : (
            <Form.Item
              name="phoneNumber"
              label={t('whatsapp.phone')}
              extra={t('whatsapp.phoneHelp')}
              rules={[{ required: true, pattern: /^\+?[0-9 ()-]{8,24}$/, message: t('whatsapp.phoneInvalid') }]}
            >
              <Input className="medium-input" placeholder="+7 900 123-45-67" inputMode="tel" />
            </Form.Item>
          )}
          {channel?.phoneNumber && (
            <Form.Item label={t('whatsapp.chatLink')}>
              <Typography.Link href={`https://wa.me/${channel.phoneNumber}`} target="_blank" rel="noreferrer">
                wa.me/{channel.phoneNumber}
              </Typography.Link>
            </Form.Item>
          )}
          <Form.Item name="siteId" label={t('whatsapp.site')} extra={t('whatsapp.siteHelp')} rules={[{ required: true }]}>
            <Select className="medium-input" options={sites.data?.map((s) => ({ value: s.id, label: `${s.name} (${s.code})` }))} />
          </Form.Item>
          <Form.Item name="isEnabled" label={t('telegram.enabled')} valuePropName="checked" extra={t('whatsapp.enabledHelp')}>
            <Switch />
          </Form.Item>
          <SoonField label={t('widgets.maxChats')}><InputNumber disabled placeholder="0" style={{ width: 220 }} /></SoonField>
          <SoonField label={t('whatsapp.groupChats')}><Checkbox disabled /></SoonField>
          <SoonField label={t('widgets.autoDistribution')}><Checkbox disabled /></SoonField>
          <SoonField label={t('widgets.closeOwnOnly')}><Checkbox disabled /></SoonField>
          <SoonField label={t('widgets.noMessagesHours')}><InputNumber disabled placeholder="0" style={{ width: 220 }} /></SoonField>
          {channel && (
            <Form.Item label={t('telegram.widgets')}>
              <Typography.Text>{t('channels.inWidgets', { count: channel.widgetCount })}</Typography.Text>
              <Typography.Text type="secondary"> · {t('whatsapp.widgetsHelp')}</Typography.Text>
            </Form.Item>
          )}
        </SettingsForm>
      </SectionCard>
    </Page>
  )
}

/**
 * Подключение по QR: пока ждём сканирования, состояние спрашивается каждые 2 секунды (QR меняется примерно
 * раз в 20 секунд), после подключения — раз в 15 секунд.
 */
function Connection({
  channel,
  onConnect,
  connecting,
  onChanged,
}: {
  channel: WhatsAppChannel
  onConnect: () => void
  connecting: boolean
  onChanged: () => void
}) {
  const { t } = useTranslation()
  const state = useQuery({
    queryKey: ['whatsAppConnection', channel.id],
    queryFn: () => settingsApi.whatsAppConnection(channel.id),
    refetchInterval: (q) => (q.state.data?.status === 'qr' || q.state.data?.status === 'starting' ? 2_000 : 15_000),
  })
  const status = state.data?.status
  const known = useRef<WhatsAppConnection['status'] | undefined>(undefined)

  // Номер подключился или отключился: обновляем канал (номер, имя аккаунта, плитку).
  useEffect(() => {
    if (status && known.current && known.current !== status && (status === 'connected' || known.current === 'connected')) onChanged()
    known.current = status
  }, [status, onChanged])

  if (state.isLoading) return <Spin />
  const data = state.data
  if (!data?.configured) return <Typography.Text type="warning">{t('whatsapp.gatewayNotConfigured')}</Typography.Text>
  if (status === 'unavailable') return <Typography.Text type="danger">{t('whatsapp.gatewayUnavailable')}</Typography.Text>

  if (status === 'connected')
    return (
      <Space orientation="vertical" size={2}>
        <Typography.Text>
          <CheckCircleFilled style={{ color: '#52c41a' }} /> {t('whatsapp.connectedAs', { phone: data.phone ? `+${data.phone}` : '—' })}
          {data.name ? ` (${data.name})` : ''}
        </Typography.Text>
        {channel.connectedAt && (
          <Typography.Text type="secondary">
            {t('whatsapp.connectedSince', { date: formatDateTime(channel.connectedAt) })}
          </Typography.Text>
        )}
      </Space>
    )

  if (status === 'qr' && data.qr)
    return (
      <div className="whatsapp-qr-live">
        <QRCode value={data.qr} size={264} bordered={false} />
        <div>
          <Typography.Paragraph style={{ marginTop: 0 }}>{t('whatsapp.scanSteps')}</Typography.Paragraph>
          {data.qrExpiresAt && (
            <Typography.Text type="secondary">
              {t('whatsapp.qrValidUntil', { time: new Date(data.qrExpiresAt).toLocaleTimeString() })}
            </Typography.Text>
          )}
        </div>
      </div>
    )

  if (status === 'starting')
    return (
      <Space>
        <Spin size="small" /> {t('whatsapp.starting')}
      </Space>
    )

  // Не подключён: впервые, QR не отсканировали вовремя или вышли из WhatsApp в телефоне.
  return (
    <div className="whatsapp-qr">
      <QrcodeOutlined className="whatsapp-qr-icon" />
      <Space orientation="vertical" size={8}>
        <Typography.Text type="secondary">
          {data.reason === 'qr_timeout'
            ? t('whatsapp.qrTimeout')
            : data.reason === 'logged_out'
              ? t('whatsapp.loggedOut')
              : t('whatsapp.notConnectedHelp')}
        </Typography.Text>
        <Button type="primary" icon={<QrcodeOutlined />} loading={connecting} onClick={onConnect}>
          {t('whatsapp.connect')}
        </Button>
      </Space>
    </div>
  )
}
