import {
  Alert,
  App as AntApp,
  Button,
  Checkbox,
  ColorPicker,
  Empty,
  Form,
  Input,
  InputNumber,
  Popconfirm,
  Radio,
  Select,
  Space,
  Spin,
  Table,
  Typography,
} from 'antd'
import { CaretRightOutlined, CloudUploadOutlined, DeleteOutlined, MinusOutlined, PlusOutlined, UploadOutlined } from '@ant-design/icons'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect, useRef } from 'react'
import { useTranslation } from 'react-i18next'
import { useNavigate, useParams } from 'react-router'
import { settingsApi, type LauncherItem, type LauncherItemType, type WidgetInput, type WidgetOptions, type WidgetRow } from '../../api/settings'
import { formatDateTime } from '../../format'
import { Page, SaveBar, SectionCard, SoonField, SoonTag, settingsLayout } from '../../layout/Page'
import { errorText } from '../../settings/errors'

/** В форме бот выбирается в строке пункта Telegram, поэтому у пунктов есть botId. */
type FormItem = LauncherItem & { botId?: string | null }
type FormValues = Omit<WidgetInput, 'options'> & { siteId: string; options: Omit<WidgetOptions, 'items'> & { items: FormItem[] } }

/** Те же значения по умолчанию, что у сервера (WidgetOptions). */
const defaultOptions: WidgetOptions = {
  caption: '',
  allowAttachments: true,
  zIndex: 2147483000,
  hideOnOutsideClick: false,
  visibility: 'Always',
  buttonDelaySeconds: 0,
  buttonColor: null,
  buttonIcon: null,
  tooltipMode: 'Never',
  tooltipText: '',
  tooltipBackground: '#8fa0ab',
  tooltipColor: '#111111',
  tooltipTextSize: 14,
  sound: 'None',
  operatorName: null,
  items: [{ type: 'Chat', label: 'Online chat' }],
}

const defaults: Omit<FormValues, 'siteId' | 'name'> = {
  identificationMode: 'SignedToken',
  title: 'Support',
  greeting: 'Hi! How can we help you?',
  accentColor: '#1677ff',
  position: 'Right',
  defaultLocale: 'en',
  telegramBotId: null,
  options: defaultOptions,
}

/** ColorPicker отдаёт объект цвета: в форме храним строку #rrggbb (или null, если цвет сброшен). */
const hexColor = (color: { toHex: () => string; cleared?: boolean } | null) =>
  color && !color.cleared ? `#${color.toHex().slice(0, 6)}` : null

/**
 * Канал «виджет» (п. 3.5 ТЗ). Раскладка повторяет страницу канала Usedesk: карточки настроек одна под другой,
 * внизу код виджета и кнопки. Поля, которые ещё не работают, показаны выключенными с пометкой «Coming soon».
 */
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
    if (!isNew && row) {
      const options = { ...defaultOptions, ...row.widget.options }
      const items = options.items.map((i) => (i.type === 'Telegram' ? { ...i, botId: row.widget.telegramBotId } : i))
      form.setFieldsValue({ ...row.widget, options: { ...options, items } })
    }
  }, [form, isNew, row, sites.data, loading])

  const save = useMutation({
    mutationFn: ({ siteId, ...input }: FormValues) => {
      // Бот выбирается в пункте Telegram: без такого пункта бот отключается от виджета.
      const formItems = input.options.items ?? []
      const telegramBotId = formItems.find((i) => i.type === 'Telegram')?.botId ?? null
      const items: LauncherItem[] = formItems.map(({ botId: _, ...item }) => item)
      const body: WidgetInput = { ...input, telegramBotId, options: { ...input.options, items } }
      return isNew ? settingsApi.createWidget(siteId, body) : settingsApi.updateWidget(widgetId, body)
    },
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

  const remove = useMutation({
    mutationFn: () => settingsApi.deleteWidget(widgetId!),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['widgets'] })
      void queryClient.invalidateQueries({ queryKey: ['sites'] })
      message.success(t('widgets.deleted'))
      navigate('/channels')
    },
    onError: (e) => message.error(errorText(e, t('settings.saveFailed'))),
  })

  const crumbs = [{ title: t('nav.channels'), to: '/channels' }, { title: isNew ? t('widgets.add') : (row?.widget.name ?? '…') }]
  if (loading) return <Page crumbs={crumbs}><Spin style={{ margin: 48 }} /></Page>
  if (!isNew && !row) return <Page crumbs={crumbs}><Empty description={t('channels.notFound')} /></Page>

  return (
    <Page crumbs={crumbs}>
      <Form<FormValues> form={form} {...settingsLayout} onFinish={(v) => save.mutate(v)} className="channel-form">
        <SectionCard title={t('channels.widget')}>
          <div className="settings-fields">
            <Form.Item name="name" label={t('widgets.name')} rules={[{ required: true, max: 200 }]}>
              <Input placeholder={t('widgets.namePlaceholder')} />
            </Form.Item>
            <Form.Item name="siteId" label={t('client.site')} rules={[{ required: true }]} extra={isNew ? undefined : t('widgets.siteFixed')}>
              <Select disabled={!isNew} options={sites.data?.map((s) => ({ value: s.id, label: `${s.name} (${s.code})` }))} />
            </Form.Item>
            <Form.Item name="defaultLocale" label={t('widgets.locale')} rules={[{ required: true }]}>
              <Select options={[{ value: 'en', label: 'English' }]} />
            </Form.Item>
            <Form.Item name="accentColor" label={t('widgets.color')} getValueFromEvent={hexColor}>
              <ColorPicker disabledAlpha showText />
            </Form.Item>
            <Form.Item name="title" label={t('widgets.title')} rules={[{ required: true, max: 100 }]}>
              <Input />
            </Form.Item>
            <Form.Item name={['options', 'caption']} label={t('widgets.caption')} rules={[{ max: 300 }]} extra={t('widgets.captionHelp')}>
              <Input />
            </Form.Item>
            <Form.Item name={['options', 'allowAttachments']} label={t('widgets.allowAttachments')} valuePropName="checked" extra={t('widgets.allowAttachmentsHelp')}>
              <Checkbox />
            </Form.Item>
            <SoonField label={t('widgets.allowRating')}><Checkbox disabled /></SoonField>
            <SoonField label={t('widgets.ratingHint')}><Input disabled placeholder="Rate" /></SoonField>
            <Form.Item name={['options', 'zIndex']} label={t('widgets.zIndex')} rules={[{ required: true }]}>
              <InputNumber min={0} max={2147483646} style={{ width: 220 }} />
            </Form.Item>
            <SoonField label={t('widgets.kbSendButton')}><Checkbox disabled /></SoonField>
            <SoonField label={t('widgets.missedDelay')}><InputNumber disabled placeholder="0" style={{ width: 220 }} /></SoonField>
            <SoonField label={t('widgets.maxChats')}><InputNumber disabled placeholder="0" style={{ width: 220 }} /></SoonField>
            <SoonField label={t('widgets.replyOffline')}><Checkbox disabled checked /></SoonField>
            <Form.Item name={['options', 'hideOnOutsideClick']} label={t('widgets.hideOnOutsideClick')} valuePropName="checked">
              <Checkbox />
            </Form.Item>
            <SoonField label={t('widgets.autoDistribution')}><Checkbox disabled /></SoonField>
            <SoonField label={t('widgets.closeOwnOnly')}><Checkbox disabled /></SoonField>
            <Form.Item name={['options', 'visibility']} label={t('widgets.visibility')}>
              <Select
                options={[
                  { value: 'Always', label: t('widgets.visibilityAlways') },
                  { value: 'Never', label: t('widgets.visibilityNever') },
                  { value: 'WorkingHours', label: `${t('widgets.visibilityWorkingHours')} · ${t('common.comingSoon')}`, disabled: true },
                ]}
              />
            </Form.Item>
            <SoonField label={t('widgets.noMessagesHours')}><InputNumber disabled placeholder="0" style={{ width: 220 }} /></SoonField>
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
          </div>
        </SectionCard>

        <SectionCard title={t('widgets.button')}>
          <div className="settings-fields">
            <SoonField label={t('widgets.waves')}><InputNumber disabled placeholder="0" style={{ width: 220 }} /></SoonField>
            <Form.Item name={['options', 'buttonDelaySeconds']} label={t('widgets.buttonDelay')} extra={t('widgets.buttonDelayHelp')}>
              <InputNumber min={0} max={3600} style={{ width: 220 }} />
            </Form.Item>
            <Form.Item name="position" label={t('widgets.position')}>
              <Select
                options={[
                  { value: 'Right', label: t('widgets.right') },
                  { value: 'Left', label: t('widgets.left') },
                ]}
              />
            </Form.Item>
            <Form.Item name={['options', 'buttonColor']} label={t('widgets.buttonColor')} getValueFromEvent={hexColor} extra={t('widgets.buttonColorHelp')}>
              <ColorPicker showText allowClear />
            </Form.Item>
            <Form.Item label={t('widgets.icon')}>
              <ButtonIcon row={row} />
            </Form.Item>
          </div>
        </SectionCard>

        <SectionCard title={t('widgets.tooltip')}>
          <div className="settings-fields">
            <Form.Item name={['options', 'tooltipMode']} label={t('widgets.tooltipShow')}>
              <Select
                options={[
                  { value: 'Never', label: t('widgets.tooltipNever') },
                  { value: 'Always', label: t('widgets.tooltipAlways') },
                  { value: 'Hover', label: t('widgets.tooltipHover') },
                ]}
              />
            </Form.Item>
            <Form.Item name={['options', 'tooltipBackground']} label={t('widgets.tooltipBackground')} getValueFromEvent={hexColor}>
              <ColorPicker disabledAlpha showText />
            </Form.Item>
            <Form.Item name={['options', 'tooltipText']} label={t('widgets.tooltipText')} rules={[{ max: 200 }]}>
              <Input />
            </Form.Item>
            <Form.Item name={['options', 'tooltipColor']} label={t('widgets.tooltipColor')} getValueFromEvent={hexColor}>
              <ColorPicker disabledAlpha showText />
            </Form.Item>
            <Form.Item name={['options', 'tooltipTextSize']} label={t('widgets.tooltipTextSize')}>
              <InputNumber min={10} max={32} style={{ width: 220 }} />
            </Form.Item>
          </div>
        </SectionCard>

        <SectionCard title={<span className="soon-title">{t('widgets.feedbackForm')} <SoonTag /></span>}>
          <div className="settings-fields">
            <SoonField label={t('widgets.feedbackChannel')}><Select disabled placeholder={t('widgets.feedbackChannelNone')} /></SoonField>
            <SoonField label={t('widgets.feedbackShow')}><Select disabled placeholder={t('widgets.tooltipNever')} /></SoonField>
            <SoonField label={t('widgets.feedbackTitle')}><Input disabled placeholder="Feedback" /></SoonField>
            <SoonField label={t('widgets.feedbackGreeting')}><Input.TextArea disabled rows={2} /></SoonField>
            <SoonField label={t('widgets.feedbackTopics')}><Button disabled icon={<PlusOutlined />}>{t('widgets.addRow')}</Button></SoonField>
            <SoonField label={t('widgets.feedbackTopicTitle')}><Input disabled placeholder="Choose a topic" /></SoonField>
            <SoonField label={t('widgets.feedbackTopicRequired')}><Checkbox disabled /></SoonField>
            <SoonField label={t('widgets.feedbackFields')}><Button disabled icon={<PlusOutlined />}>{t('widgets.addRow')}</Button></SoonField>
            <SoonField label={t('widgets.feedbackTicketFields')}><Button disabled icon={<PlusOutlined />}>{t('widgets.addRow')}</Button></SoonField>
          </div>
        </SectionCard>

        <SectionCard title={t('widgets.chat')}>
          <div className="settings-fields">
            <Form.Item label={t('widgets.sound')}>
              <Space.Compact style={{ width: '100%' }}>
                <Form.Item name={['options', 'sound']} noStyle>
                  <Select
                    style={{ width: '100%' }}
                    options={[
                      { value: 'None', label: t('widgets.soundNone') },
                      { value: 'Beep', label: t('widgets.soundBeep') },
                    ]}
                  />
                </Form.Item>
                <Button icon={<CaretRightOutlined />} aria-label={t('widgets.soundPlay')} onClick={() => form.getFieldValue(['options', 'sound']) === 'Beep' && beep()} />
              </Space.Compact>
            </Form.Item>
            <Form.Item name={['options', 'operatorName']} label={t('widgets.operatorName')} rules={[{ max: 64 }]} extra={t('widgets.operatorNameHelp')}>
              <Input placeholder={t('widgets.operatorNamePlaceholder')} />
            </Form.Item>
            <SoonField label={t('widgets.noEmail')}><Checkbox disabled checked /></SoonField>
            <Form.Item name="greeting" label={t('widgets.greeting')} rules={[{ max: 1000 }]}>
              <Input.TextArea autoSize={{ minRows: 2, maxRows: 6 }} />
            </Form.Item>
            <SoonField label={t('widgets.greetingEveryInit')}><Checkbox disabled /></SoonField>
            <SoonField label={t('widgets.greetingReaction')}><Checkbox disabled /></SoonField>
            <SoonField label={t('widgets.greetingReactionText')}><Input.TextArea disabled rows={3} /></SoonField>
            <SoonField label={t('widgets.messageButtonText')}><ColorPicker disabled value="#ffffff" showText /></SoonField>
            <SoonField label={t('widgets.messageButtonBackground')}><ColorPicker disabled value="#e8424f" showText /></SoonField>
          </div>
        </SectionCard>

        <SectionCard title={t('widgets.items')}>
          <LauncherItems bots={bots.data ?? []} />
        </SectionCard>

        {row && (
          <Form.Item
            label={t('widgets.embedCode')}
            className="embed-row"
            extra={
              row.fileUrl && !row.widget.publishedVersion ? (
                <Typography.Text type="warning">{t('widgets.publishFirst')}</Typography.Text>
              ) : (
                <EmbedHints row={row} />
              )
            }
          >
            <Input.TextArea readOnly value={row.embedCode} autoSize={{ minRows: 2, maxRows: 4 }} className="code-area" />
          </Form.Item>
        )}

        <SaveBar saving={save.isPending}>
          {!isNew && (
            <Popconfirm title={t('widgets.deleteConfirm')} description={t('widgets.deleteHelp')} onConfirm={() => remove.mutate()}>
              <Button danger type="primary" loading={remove.isPending}>
                {t('widgets.delete')}
              </Button>
            </Popconfirm>
          )}
          <Button onClick={() => navigate('/channels')}>{t('settings.cancel')}</Button>
        </SaveBar>
      </Form>

      {row && <PublishCard row={row} />}
    </Page>
  )
}

/** Что ещё нужно сайту, кроме строки кода. */
function EmbedHints({ row }: { row: WidgetRow }) {
  const { t } = useTranslation()
  return (
    <Space orientation="vertical" size={4} style={{ marginTop: 6 }}>
      <span>{t('widgets.embedHelp')}</span>
      <span>
        {row.widget.identificationMode === 'SignedToken' ? t('widgets.embedTokenHint') : t('widgets.embedDirectHint')}{' '}
        {t('widgets.embedOpenHint')}
      </span>
      <span>
        {t('widgets.embedReady')}{' '}
        <Typography.Text code>
          {row.widget.identificationMode === 'SignedToken'
            ? "window.addEventListener('relayo:ready', () => Relayo.identify(token))"
            : "window.addEventListener('relayo:ready', () => Relayo.setClientId(id))"}
        </Typography.Text>
      </span>
      <span>
        ID: <Typography.Text code copyable>{row.widget.id}</Typography.Text>
      </span>
    </Space>
  )
}

/**
 * «Настройка пунктов»: кнопки рядом с кнопкой чата — чат, Telegram (с выбором бота) и ссылки.
 * Порядок строк — порядок кнопок на сайте.
 */
function LauncherItems({ bots }: { bots: { id: string; name: string; username: string; isEnabled: boolean }[] }) {
  const { t } = useTranslation()
  const form = Form.useFormInstance<FormValues>()
  const items = Form.useWatch(['options', 'items'], form) ?? []
  const typeOptions = (current: LauncherItemType) => [
    { value: 'Chat', label: t('widgets.itemChat'), disabled: current !== 'Chat' && items.some((i) => i.type === 'Chat') },
    { value: 'Telegram', label: t('widgets.itemTelegram'), disabled: current !== 'Telegram' && items.some((i) => i.type === 'Telegram') },
    { value: 'Link', label: t('widgets.itemLink') },
    { value: 'WhatsApp', label: `WhatsApp · ${t('common.comingSoon')}`, disabled: true },
  ]

  return (
    <Form.List name={['options', 'items']}>
      {(fields, { add, remove }) => (
        <div className="launcher-items">
          {fields.map((field) => {
            const type = items[field.name]?.type
            return (
              <div className="launcher-item" key={field.key}>
                <Form.Item name={[field.name, 'type']} noStyle>
                  <Select style={{ width: 200 }} options={typeOptions(type)} />
                </Form.Item>
                <Form.Item name={[field.name, 'label']} noStyle rules={[{ max: 40 }]}>
                  <Input style={{ width: 220 }} placeholder={t('widgets.itemLabel')} maxLength={40} />
                </Form.Item>
                {type === 'Telegram' && (
                  <Form.Item name={[field.name, 'botId']} noStyle rules={[{ required: true, message: t('widgets.chooseBot') }]}>
                    <Select
                      style={{ width: 260 }}
                      placeholder={t('widgets.chooseBot')}
                      notFoundContent={t('widgets.noBots')}
                      options={bots.map((b) => ({
                        value: b.id,
                        label: `${b.name} (@${b.username})${b.isEnabled ? '' : ` · ${t('telegram.disabled')}`}`,
                      }))}
                    />
                  </Form.Item>
                )}
                {type === 'Link' && (
                  <Form.Item name={[field.name, 'url']} noStyle rules={[{ required: true, type: 'url', message: t('widgets.itemUrlHelp') }]}>
                    <Input style={{ width: 260 }} placeholder="https://example.com" />
                  </Form.Item>
                )}
                {/* Чат — основной пункт, его не убрать. */}
                <Button
                  className="minus-button"
                  icon={<MinusOutlined />}
                  disabled={type === 'Chat'}
                  aria-label={t('widgets.itemRemove')}
                  onClick={() => remove(field.name)}
                />
              </div>
            )
          })}
          <Button
            className="add-button"
            icon={<PlusOutlined />}
            disabled={fields.length >= 6}
            onClick={() => add({ type: items.some((i) => i.type === 'Telegram') ? 'Link' : 'Telegram', label: '' })}
          >
            {t('widgets.addRow')}
          </Button>
        </div>
      )}
    </Form.List>
  )
}

/** Своя картинка кнопки: загружается сразу в S3, на сайтах появится после публикации. Убрать — кнопкой и «Save». */
function ButtonIcon({ row }: { row?: WidgetRow }) {
  const { t } = useTranslation()
  const { message } = AntApp.useApp()
  const queryClient = useQueryClient()
  const form = Form.useFormInstance<FormValues>()
  const icon = Form.useWatch(['options', 'buttonIcon'], form)
  const accent = Form.useWatch('accentColor', form) ?? '#1677ff'
  const buttonColor = Form.useWatch(['options', 'buttonColor'], form)
  const input = useRef<HTMLInputElement>(null)

  const upload = useMutation({
    mutationFn: (file: File) => settingsApi.uploadButtonIcon(row!.widget.id, file),
    onSuccess: (updated) => {
      queryClient.setQueryData<WidgetRow[]>(['widgets'], (old) => old?.map((r) => (r.widget.id === updated.widget.id ? updated : r)))
      form.setFieldValue(['options', 'buttonIcon'], updated.widget.options.buttonIcon)
      message.success(t('widgets.iconUploaded'))
    },
    onError: (e) => message.error(errorText(e, t('settings.saveFailed'))),
  })

  // Размер в пикселях проверяем здесь: сервер проверяет тип и объём файла.
  const choose = (file: File) => {
    const url = URL.createObjectURL(file)
    const image = new Image()
    image.onload = () => {
      URL.revokeObjectURL(url)
      if (image.width > 512 || image.height > 512) message.error(t('widgets.iconTooBig'))
      else upload.mutate(file)
    }
    image.onerror = () => {
      URL.revokeObjectURL(url)
      message.error(t('widgets.iconBroken'))
    }
    image.src = url
  }

  const showCustom = !!icon && icon === row?.widget.options.buttonIcon && !!row?.buttonIconUrl
  return (
    <div>
      <div className="icon-preview">
        {showCustom ? (
          <img src={row!.buttonIconUrl!} alt="" />
        ) : (
          <span className="icon-default" style={{ background: buttonColor ?? accent }}>
            <svg width="72" height="72" viewBox="0 0 32 32" aria-hidden="true">
              <path fill="#fff" d="M16 3a12 12 0 0 0-10 19L3 29l8-4A12 12 0 1 0 16 3Z" />
            </svg>
          </span>
        )}
      </div>
      <Space style={{ marginTop: 8 }}>
        <Button icon={<UploadOutlined />} disabled={!row || !row.fileUrl} loading={upload.isPending} onClick={() => input.current?.click()}>
          {t('widgets.iconChoose')}
        </Button>
        {icon && (
          <Button icon={<DeleteOutlined />} onClick={() => form.setFieldValue(['options', 'buttonIcon'], null)}>
            {t('widgets.iconRemove')}
          </Button>
        )}
      </Space>
      <input
        ref={input}
        type="file"
        accept="image/png,image/jpeg,image/gif,image/webp"
        style={{ display: 'none' }}
        onChange={(e) => {
          const file = e.target.files?.[0]
          if (file) choose(file)
          e.target.value = ''
        }}
      />
      <Form.Item name={['options', 'buttonIcon']} hidden>
        <Input />
      </Form.Item>
      <Alert type="info" style={{ marginTop: 12 }} title={!row ? t('widgets.iconAfterCreate') : !row.fileUrl ? t('widgets.storageOff') : t('widgets.iconLimit')} />
    </div>
  )
}

/** Короткий сигнал — тот же, что проиграет виджет посетителю. */
function beep() {
  const audio = new AudioContext()
  const oscillator = audio.createOscillator()
  const gain = audio.createGain()
  oscillator.connect(gain)
  gain.connect(audio.destination)
  gain.gain.value = 0.08
  oscillator.frequency.value = 660
  oscillator.start()
  oscillator.stop(audio.currentTime + 0.18)
  oscillator.onended = () => void audio.close()
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
