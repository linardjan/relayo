import { Alert, App as AntApp, Button, Card, Checkbox, Collapse, Empty, Form, Input, InputNumber, Popconfirm, Select, Spin, Switch, Typography } from 'antd'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect, useRef, type ReactNode } from 'react'
import { useTranslation } from 'react-i18next'
import { useNavigate, useParams } from 'react-router'
import { settingsApi, type EmailChannel, type EmailChannelInput, type MailSecurity, type MailServer } from '../../api/settings'
import { Page, SaveBar, SectionCard, SoonField, settingsLayout } from '../../layout/Page'
import { StatusBox } from '../../layout/StatusBox'
import { errorText } from '../../settings/errors'

type Side = 'imap' | 'smtp'

/** Порт по умолчанию для шифрования: подставляется, пока администратор не ввёл свой. */
const defaultPorts: Record<Side, Record<MailSecurity, number>> = {
  imap: { Ssl: 993, StartTls: 143, None: 143 },
  smtp: { Ssl: 465, StartTls: 587, None: 25 },
}

const newServer = (side: Side, security: MailSecurity) => ({ email: '', host: '', port: defaultPorts[side][security], security, username: '', password: '' })

/** Подписи в блоках соединений уже основной формы: блоки стоят рядом. */
const connLabel = { labelCol: { flex: '150px' } }

/**
 * Почтовый канал. Раскладка повторяет страницу почтового канала в Usedesk: блок «Почта» с общими настройками,
 * блок «Настройка типов соединения» с входящим (IMAP) и исходящим (SMTP) соединениями рядом, внизу «Save» и «Delete».
 * Сохранение сразу проверяет вход на оба сервера. Приём и отправка писем — следующий шаг, о чём сказано на странице.
 */
export function EmailChannelPage() {
  const { t } = useTranslation()
  const { message } = AntApp.useApp()
  const { channelId } = useParams()
  const isNew = channelId === undefined
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const channels = useQuery({ queryKey: ['emailChannels'], queryFn: settingsApi.emailChannels })
  const sites = useQuery({ queryKey: ['sites'], queryFn: settingsApi.sites })
  const channel = channels.data?.find((c) => c.id === channelId)
  const [form] = Form.useForm<EmailChannelInput>()
  const loading = channels.isLoading || sites.isLoading
  // Адрес входящего ящика, который последним подставили в исходящее: так видно, менял ли его администратор.
  const mirroredEmail = useRef('')

  useEffect(() => {
    if (loading) return
    if (isNew && sites.data)
      form.setFieldsValue({
        name: '',
        siteId: sites.data[0]?.id,
        isEnabled: true,
        forwardCreatesTicket: false,
        quoting: 'SendToClient',
        imap: newServer('imap', 'Ssl'),
        smtp: newServer('smtp', 'StartTls'),
      })
    if (!isNew && channel) {
      const server = (s: MailServer) => ({ ...s, username: s.username ?? '', password: '' })
      form.setFieldsValue({ ...channel, imap: server(channel.imap), smtp: server(channel.smtp) })
      mirroredEmail.current = channel.smtp.email === channel.imap.email ? channel.imap.email : ''
    }
  }, [form, isNew, channel, sites.data, loading])

  const saved = (c: EmailChannel) => {
    queryClient.setQueryData<EmailChannel[]>(['emailChannels'], (list) => (list ? [...list.filter((x) => x.id !== c.id), c] : [c]))
    void queryClient.invalidateQueries({ queryKey: ['emailChannels'] })
  }

  const save = useMutation({
    mutationFn: (input: EmailChannelInput) => {
      const server = (s: EmailChannelInput['imap']) => ({ ...s, username: s.username?.trim() || null, password: s.password || undefined })
      const body = { ...input, imap: server(input.imap), smtp: server(input.smtp) }
      return isNew ? settingsApi.createEmailChannel(body) : settingsApi.updateEmailChannel(channelId, body)
    },
    onSuccess: (c) => {
      saved(c)
      form.setFieldValue(['imap', 'password'], '')
      form.setFieldValue(['smtp', 'password'], '')
      message[c.lastCheckOk ? 'success' : 'warning'](c.lastCheckOk ? t('email.savedConnected') : t('email.savedNotConnected'))
      if (isNew) navigate(`/channels/email/${c.id}`, { replace: true })
    },
    onError: (e) => message.error(errorText(e, t('settings.saveFailed'))),
  })

  const check = useMutation({
    mutationFn: () => settingsApi.checkEmailChannel(channelId!),
    onSuccess: saved,
    onError: (e) => message.error(errorText(e, t('email.checkFailed'))),
  })

  const remove = useMutation({
    mutationFn: () => settingsApi.deleteEmailChannel(channelId!),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['emailChannels'] })
      message.success(t('email.deleted'))
      navigate('/channels')
    },
    onError: (e) => message.error(errorText(e, t('settings.saveFailed'))),
  })

  const crumbs = [
    { title: t('nav.channels'), to: '/channels' },
    { title: isNew ? t('email.add') : `${channel?.name ?? '…'} (${t('channels.email')})` },
  ]
  if (loading) return <Page crumbs={crumbs}><Spin style={{ margin: 48 }} /></Page>
  if (!isNew && !channel) return <Page crumbs={crumbs}><Empty description={t('channels.notFound')} /></Page>

  const onValuesChange = (changed: Partial<EmailChannelInput>, all: EmailChannelInput) => {
    for (const side of ['imap', 'smtp'] as const) {
      // Порт подстраивается под шифрование, если в нём стоит один из портов по умолчанию.
      const security = changed[side]?.security
      if (security && Object.values(defaultPorts[side]).includes(all[side].port))
        form.setFieldValue([side, 'port'], defaultPorts[side][security])
    }
    // Обычно письма уходят с того же адреса: пока исходящий адрес не меняли вручную, он повторяет входящий.
    const email = changed.imap?.email
    if (email !== undefined && (!all.smtp.email || all.smtp.email === mirroredEmail.current)) {
      form.setFieldValue(['smtp', 'email'], email)
      mirroredEmail.current = email
    }
  }

  return (
    <Page crumbs={crumbs}>
      <Form<EmailChannelInput> form={form} {...settingsLayout} onFinish={(v) => save.mutate(v)} onValuesChange={onValuesChange} className="channel-form">
        <SectionCard title={t('channels.email')}>
          <div className="settings-fields">
            <Form.Item name="name" label={t('email.name')} rules={[{ required: true, whitespace: true, max: 200 }]}>
              <Input className="medium-input" />
            </Form.Item>
            <SoonField label={t('email.commonSignature')}><Checkbox disabled /></SoonField>
            <Form.Item name="forwardCreatesTicket" label={t('email.forwardCreatesTicket')} valuePropName="checked" extra={t('email.forwardCreatesTicketHelp')}>
              <Checkbox />
            </Form.Item>
            <SoonField label={t('widgets.noMessagesHours')}><InputNumber disabled placeholder="0" style={{ width: 90 }} /></SoonField>
            <Form.Item name="quoting" label={t('email.quoting')}>
              <Select
                style={{ maxWidth: 365 }}
                options={[
                  { value: 'SendToClient', label: t('email.quotingSend') },
                  { value: 'DoNotSend', label: t('email.quotingDoNotSend') },
                ]}
              />
            </Form.Item>
            <Form.Item name="siteId" label={t('whatsapp.site')} extra={t('email.siteHelp')} rules={[{ required: true }]}>
              <Select className="medium-input" options={sites.data?.map((s) => ({ value: s.id, label: `${s.name} (${s.code})` }))} />
            </Form.Item>
            <Form.Item name="isEnabled" label={t('telegram.enabled')} valuePropName="checked">
              <Switch />
            </Form.Item>
          </div>
        </SectionCard>

        <SectionCard title={t('email.connections')}>
          <Typography.Paragraph type="secondary">{t('email.onlyNewEmails')}</Typography.Paragraph>
          <Alert type="info" showIcon title={t('email.receivingSoon')} style={{ marginBottom: 16 }} />
          <div className="mail-connections">
            <Connection side="imap" title={t('email.incoming')} protocol="IMAP" saved={channel?.imap} />
            <Connection side="smtp" title={t('email.outgoing')} protocol="SMTP" saved={channel?.smtp} />
          </div>
          {channel && (
            <Form.Item label={t('ldap.status')}>
              <StatusBox ok={channel.lastCheckOk} at={channel.lastCheckAt} text={channel.lastCheckMessage ?? t('ldap.notChecked')} />
            </Form.Item>
          )}
        </SectionCard>

        <SaveBar saving={save.isPending}>
          {channel && (
            <>
              <Button onClick={() => check.mutate()} loading={check.isPending} disabled={save.isPending}>
                {t('ldap.check')}
              </Button>
              <Popconfirm title={t('email.deleteConfirm')} description={t('email.deleteHelp')} onConfirm={() => remove.mutate()}>
                <Button danger type="primary" loading={remove.isPending}>
                  {t('whatsapp.delete')}
                </Button>
              </Popconfirm>
            </>
          )}
          <Button onClick={() => navigate('/channels')}>{t('settings.cancel')}</Button>
        </SaveBar>
      </Form>
    </Page>
  )
}

/** Входящее или исходящее соединение: адрес, пароль, сервер, порт, шифрование и в «доп. настройках» — имя пользователя. */
function Connection({ side, title, protocol, saved }: { side: Side; title: ReactNode; protocol: string; saved?: MailServer }) {
  const { t } = useTranslation()
  const security = Form.useWatch([side, 'security']) as MailSecurity | undefined
  const email = Form.useWatch([side, 'email']) as string | undefined
  return (
    <Card size="small" title={title}>
      <Form.Item {...connLabel} label={t('email.connectionType')} tooltip={t('email.connectionTypeHelp')}>
        <Select
          value="External"
          options={[
            { value: 'External', label: t('email.external') },
            { value: 'Internal', label: `${t('email.internal')} — ${t('common.comingSoon')}`, disabled: true },
          ]}
        />
      </Form.Item>
      <Form.Item {...connLabel} name={[side, 'email']} label={t('email.address')} rules={[{ required: true, type: 'email', whitespace: true }]}>
        <Input type="email" autoComplete="off" placeholder="support@company.com" />
      </Form.Item>
      <Form.Item
        {...connLabel}
        name={[side, 'password']}
        label={t('email.password')}
        extra={saved?.hasPassword ? t('ldap.passwordKeep') : undefined}
        rules={saved?.hasPassword ? [] : [{ required: true }]}
      >
        <Input.Password autoComplete="new-password" placeholder={saved?.hasPassword ? '••••••••' : undefined} />
      </Form.Item>
      <Form.Item {...connLabel} name={[side, 'host']} label={t('email.server', { protocol })} rules={[{ required: true, whitespace: true }]}>
        <Input autoComplete="off" placeholder={`${side}.company.com`} />
      </Form.Item>
      <Form.Item
        {...connLabel}
        name={[side, 'port']}
        label={t('email.port', { protocol })}
        tooltip={t(side === 'imap' ? 'email.imapPortHelp' : 'email.smtpPortHelp')}
        rules={[{ required: true }]}
      >
        <InputNumber min={1} max={65535} style={{ width: 120 }} />
      </Form.Item>
      <Form.Item
        {...connLabel}
        name={[side, 'security']}
        label={t('email.security')}
        extra={security === 'None' ? <Typography.Text type="warning">{t('email.noEncryptionWarning')}</Typography.Text> : undefined}
      >
        <Select
          options={[
            { value: 'Ssl', label: 'SSL/TLS' },
            { value: 'StartTls', label: 'STARTTLS' },
            { value: 'None', label: t('email.noEncryption') },
          ]}
        />
      </Form.Item>
      <Collapse
        ghost
        size="small"
        defaultActiveKey={saved?.username ? ['more'] : []}
        items={[
          {
            key: 'more',
            label: t('email.more'),
            forceRender: true,
            children: (
              <Form.Item {...connLabel} name={[side, 'username']} label={t('email.username')} extra={t('email.usernameHelp')}>
                <Input autoComplete="off" placeholder={email || undefined} />
              </Form.Item>
            ),
          },
        ]}
      />
    </Card>
  )
}
