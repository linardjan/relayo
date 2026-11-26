import { App as AntApp, Button, Checkbox, Empty, Form, Input, InputNumber, Popconfirm, Select, Spin, Switch, Typography } from 'antd'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect } from 'react'
import { useTranslation } from 'react-i18next'
import { useNavigate, useParams } from 'react-router'
import { settingsApi, type TelegramBotInput } from '../../api/settings'
import { Page, SectionCard, SettingsForm, SoonField } from '../../layout/Page'
import { errorText } from '../../settings/errors'

/**
 * Канал Telegram (п. 3.4 ТЗ). Раскладка повторяет страницу канала Usedesk: одна карточка, внизу «Save» и «Delete».
 * Токен вводится один раз и потом не показывается. Распределение чатов ещё не сделано — эти поля выключены.
 */
export function TelegramBotPage() {
  const { t } = useTranslation()
  const { message } = AntApp.useApp()
  const { botId } = useParams()
  const isNew = botId === undefined
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const bots = useQuery({ queryKey: ['telegramBots'], queryFn: settingsApi.telegramBots })
  const sites = useQuery({ queryKey: ['sites'], queryFn: settingsApi.sites })
  const bot = bots.data?.find((b) => b.id === botId)
  const [form] = Form.useForm<TelegramBotInput>()
  // Форму заполняем, только когда она уже на странице (пока идёт загрузка, вместо неё спиннер).
  const loading = bots.isLoading || sites.isLoading

  useEffect(() => {
    if (loading) return
    if (isNew && sites.data) form.setFieldsValue({ name: '', siteId: sites.data[0]?.id, isEnabled: true, token: '' })
    if (!isNew && bot) form.setFieldsValue({ name: bot.name, siteId: bot.siteId, isEnabled: bot.isEnabled, token: '' })
  }, [form, isNew, bot, sites.data, loading])

  const save = useMutation({
    mutationFn: (input: TelegramBotInput) => {
      const body = { ...input, token: input.token?.trim() || undefined }
      return isNew ? settingsApi.createTelegramBot(body) : settingsApi.updateTelegramBot(botId, body)
    },
    onSuccess: (saved) => {
      void queryClient.invalidateQueries({ queryKey: ['telegramBots'] })
      message.success(t('settings.saved'))
      if (isNew) navigate(`/channels/telegram/${saved.id}`, { replace: true })
      else form.setFieldValue('token', '')
    },
    onError: (e) => message.error(errorText(e, t('settings.saveFailed'))),
  })

  const remove = useMutation({
    mutationFn: () => settingsApi.deleteTelegramBot(botId!),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['telegramBots'] })
      void queryClient.invalidateQueries({ queryKey: ['widgets'] })
      message.success(t('telegram.deleted'))
      navigate('/channels')
    },
    onError: (e) => message.error(errorText(e, t('settings.saveFailed'))),
  })

  const crumbs = [{ title: t('nav.channels'), to: '/channels' }, { title: isNew ? t('telegram.add') : (bot?.name ?? '…') }]
  if (loading) return <Page crumbs={crumbs}><Spin style={{ margin: 48 }} /></Page>
  if (!isNew && !bot) return <Page crumbs={crumbs}><Empty description={t('channels.notFound')} /></Page>

  return (
    <Page crumbs={crumbs}>
      <SectionCard title={t('channels.telegram')}>
        {isNew && (
          <Typography.Paragraph type="secondary" style={{ maxWidth: 1000 }}>
            {t('telegram.intro')}
          </Typography.Paragraph>
        )}
        <SettingsForm<TelegramBotInput>
          form={form}
          onFinish={(v) => save.mutate(v)}
          saving={save.isPending}
          onCancel={() => navigate('/channels')}
          actions={
            bot && (
              <Popconfirm title={t('telegram.deleteConfirm')} description={t('telegram.deleteHelp')} onConfirm={() => remove.mutate()}>
                <Button danger type="primary" loading={remove.isPending}>
                  {t('telegram.delete')}
                </Button>
              </Popconfirm>
            )
          }
        >
          <Form.Item name="name" label={t('telegram.name')} rules={[{ required: !isNew, max: 200 }]} extra={isNew ? t('telegram.nameHelp') : undefined}>
            <Input className="medium-input" />
          </Form.Item>
          <Form.Item
            name="token"
            label={isNew ? t('telegram.token') : t('telegram.tokenReplace')}
            extra={isNew ? t('telegram.tokenHelp') : t('telegram.tokenReplaceHelp')}
            rules={isNew ? [{ required: true }] : []}
          >
            <Input.Password className="medium-input" placeholder="123456789:AA…" autoComplete="new-password" />
          </Form.Item>
          {bot && (
            <Form.Item label={t('telegram.bot')}>
              <Typography.Link href={`https://t.me/${bot.username}`} target="_blank" rel="noreferrer">
                @{bot.username}
              </Typography.Link>
            </Form.Item>
          )}
          <Form.Item name="siteId" label={t('telegram.site')} extra={t('telegram.siteHelp')} rules={[{ required: true }]}>
            <Select options={sites.data?.map((s) => ({ value: s.id, label: `${s.name} (${s.code})` }))} />
          </Form.Item>
          <Form.Item name="isEnabled" label={t('telegram.enabled')} valuePropName="checked" extra={t('telegram.enabledHelp')}>
            <Switch />
          </Form.Item>
          <SoonField label={t('widgets.maxChats')}><InputNumber disabled placeholder="0" style={{ width: 220 }} /></SoonField>
          <SoonField label={t('widgets.autoDistribution')}><Checkbox disabled /></SoonField>
          <SoonField label={t('widgets.closeOwnOnly')}><Checkbox disabled /></SoonField>
          <SoonField label={t('widgets.noMessagesHours')}><InputNumber disabled placeholder="0" style={{ width: 220 }} /></SoonField>
          {bot && (
            <Form.Item label={t('telegram.widgets')}>
              <Typography.Text>{t('channels.inWidgets', { count: bot.widgetCount })}</Typography.Text>
              <Typography.Text type="secondary"> · {t('telegram.widgetsHelp')}</Typography.Text>
            </Form.Item>
          )}
        </SettingsForm>
      </SectionCard>
    </Page>
  )
}
