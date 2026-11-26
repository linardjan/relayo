import { App as AntApp, Button, Checkbox, Empty, Form, Input, Popconfirm, Select, Space, Spin, Tooltip, Typography, Upload } from 'antd'
import { UploadOutlined } from '@ant-design/icons'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect } from 'react'
import { useTranslation } from 'react-i18next'
import { useLocation, useNavigate, useParams } from 'react-router'
import { clientsApi, type ClientDetails, type ClientProfileInput } from '../api/clients'
import { useAuth } from '../auth/AuthContext'
import { ClientStatusTag } from '../clients/ClientStatusTag'
import { PersonAvatar } from '../inbox/visuals'
import { Page, SectionCard, SettingsForm, SoonField, SoonTag } from '../layout/Page'
import { errorText } from '../settings/errors'

interface FormValues {
  siteId?: string
  externalClientId?: string
  name: string
  locale: string
  position: string
  note: string
  isVip: boolean
  isSpammer: boolean
}

const knownLocales = ['ru', 'en']

/**
 * Клиент. Форма повторяет «Редактировать клиента» в Usedesk: имя, аватар, язык, компания, должность, заметка,
 * «Спамер» и VIP, внизу «Save», «Merge with» и «Delete». Запросы клиента — на странице деталей (ClientDetailsPage).
 * По адресу /clients/new клиента заводят вручную: сайт и, если известен, clientId.
 */
export function ClientPage() {
  const { t } = useTranslation()
  const { message } = AntApp.useApp()
  const { user } = useAuth()
  const { clientId } = useParams()
  const isNew = clientId === undefined
  const navigate = useNavigate()
  const location = useLocation()
  // «Cancel» возвращает туда, откуда пришли (список или детали клиента); при открытии по ссылке — в список.
  const goBack = () => (location.key === 'default' ? navigate('/clients') : navigate(-1))
  const queryClient = useQueryClient()
  const [form] = Form.useForm<FormValues>()
  const { data, isLoading, error } = useQuery({ queryKey: ['client', clientId], queryFn: () => clientsApi.get(clientId!), enabled: !isNew })
  const sites = useQuery({ queryKey: ['clientSites'], queryFn: clientsApi.sites, enabled: isNew })
  const client = data?.client

  useEffect(() => {
    if (isNew) form.setFieldsValue({ name: '', locale: '', position: '', note: '', isVip: false, isSpammer: false })
    else if (client)
      form.setFieldsValue({
        name: client.displayName ?? '',
        locale: client.locale ?? '',
        position: client.position ?? '',
        note: client.note ?? '',
        isVip: client.isVip,
        isSpammer: client.isSpammer,
      })
  }, [form, isNew, client])

  useEffect(() => {
    if (isNew && sites.data?.length && !form.getFieldValue('siteId')) form.setFieldValue('siteId', sites.data[0].id)
  }, [form, isNew, sites.data])

  const refresh = (d: ClientDetails) => {
    queryClient.setQueryData(['client', d.client.id], d)
    void queryClient.invalidateQueries({ queryKey: ['clients'] })
  }

  const save = useMutation({
    mutationFn: (v: FormValues) => {
      const profile: ClientProfileInput = {
        name: v.name || null,
        locale: v.locale || null,
        position: v.position || null,
        note: v.note || null,
        isVip: v.isVip,
        isSpammer: v.isSpammer,
      }
      return isNew ? clientsApi.create(v.siteId!, v.externalClientId?.trim() || null, profile) : clientsApi.update(clientId, profile)
    },
    onSuccess: (d) => {
      refresh(d)
      message.success(t('settings.saved'))
      if (isNew) navigate(`/clients/${d.client.id}`, { replace: true })
    },
    onError: (e) => message.error(errorText(e, t('settings.saveFailed'))),
  })

  const avatar = useMutation({
    mutationFn: (file: File | null) => (file ? clientsApi.setAvatar(clientId!, file) : clientsApi.removeAvatar(clientId!)),
    onSuccess: refresh,
    onError: (e) => message.error(errorText(e, t('settings.saveFailed'))),
  })

  const remove = useMutation({
    mutationFn: () => clientsApi.remove(clientId!),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['clients'] })
      message.success(t('clients.deleted'))
      navigate('/clients')
    },
    onError: (e) => message.error(errorText(e, t('settings.saveFailed'))),
  })

  const crumbs = [{ title: t('nav.clients'), to: '/clients' }, { title: isNew ? t('clients.create') : (client?.label ?? '…') }]
  if (!isNew && isLoading) return <Page crumbs={crumbs}><Spin style={{ display: 'block', margin: 48 }} /></Page>
  if (!isNew && (error || !data)) return <Page crumbs={crumbs}><Empty description={t('clients.notFound')} /></Page>

  // Язык не из списка (например, de) остаётся выбранным как есть.
  const localeOptions = [
    { value: '', label: t('clients.languageNone') },
    ...knownLocales.map((l) => ({ value: l, label: t(`clients.languages.${l}`) })),
    ...(client?.locale && !knownLocales.includes(client.locale) ? [{ value: client.locale, label: client.locale }] : []),
  ]

  return (
    <Page crumbs={crumbs}>
      <SectionCard title={isNew ? t('clients.create') : <Space>{client!.label}<ClientStatusTag {...client!} /></Space>}>
        <SettingsForm<FormValues>
          form={form}
          onFinish={(v) => save.mutate(v)}
          saving={save.isPending}
          onCancel={goBack}
          actions={
            !isNew && (
              <>
                <Button disabled>
                  {t('clients.merge')} <SoonTag />
                </Button>
                {user?.role === 'Admin' &&
                  (client!.ticketCount > 0 ? (
                    // Клиента с перепиской сервер не удалит: кнопка видна, но выключена с объяснением.
                    <Tooltip title={t('clients.deleteHasTickets')}>
                      <Button danger type="primary" disabled>
                        {t('clients.delete')}
                      </Button>
                    </Tooltip>
                  ) : (
                    <Popconfirm title={t('clients.deleteConfirm')} description={t('clients.deleteHelp')} onConfirm={() => remove.mutate()}>
                      <Button danger type="primary" loading={remove.isPending}>
                        {t('clients.delete')}
                      </Button>
                    </Popconfirm>
                  ))}
              </>
            )
          }
        >
          {isNew ? (
            <>
              <Form.Item name="siteId" label={t('client.site')} rules={[{ required: true }]}>
                <Select className="medium-input" options={sites.data?.map((s) => ({ value: s.id, label: `${s.name} (${s.code})` }))} />
              </Form.Item>
              <Form.Item name="externalClientId" label="clientId" extra={t('clients.clientIdHelp')} rules={[{ max: 128 }]}>
                <Input className="medium-input" autoComplete="off" />
              </Form.Item>
            </>
          ) : (
            <>
              <Form.Item label={t('client.site')}>
                <Typography.Text>{client!.siteCode}</Typography.Text>
              </Form.Item>
              <Form.Item label="clientId">
                <Typography.Text>{client!.externalClientId ?? '—'}</Typography.Text>
              </Form.Item>
            </>
          )}
          <Form.Item name="name" label={t('clients.name')} extra={t('clients.nameHelp')} rules={[{ max: 200 }]}>
            <Input className="medium-input" />
          </Form.Item>
          <Form.Item label={t('clients.avatar')}>
            {isNew ? (
              <Typography.Text type="secondary">{t('clients.avatarSaveFirst')}</Typography.Text>
            ) : (
              <Space align="start" size={16}>
                <PersonAvatar name={client!.label} seed={client!.id} size={100} src={client!.avatarUrl} />
                <Space orientation="vertical" size={4}>
                  <Upload
                    accept="image/png,image/jpeg,image/gif,image/webp"
                    showUploadList={false}
                    beforeUpload={(file) => {
                      avatar.mutate(file)
                      return false
                    }}
                  >
                    <Button icon={<UploadOutlined />} loading={avatar.isPending}>
                      {t('clients.uploadAvatar')}
                    </Button>
                  </Upload>
                  {client!.avatarUrl && (
                    <Typography.Link onClick={() => avatar.mutate(null)}>{t('clients.removeAvatar')}</Typography.Link>
                  )}
                  <Typography.Text type="secondary">{t('clients.avatarHelp')}</Typography.Text>
                </Space>
              </Space>
            )}
          </Form.Item>
          <Form.Item name="locale" label={t('clients.language')}>
            <Select className="medium-input" options={localeOptions} />
          </Form.Item>
          <SoonField label={t('clients.company')}>
            <Select disabled value="none" style={{ width: 220 }} options={[{ value: 'none', label: t('clients.companyNone') }]} />
          </SoonField>
          <Form.Item name="position" label={t('clients.position')} extra={t('clients.positionHelp')} rules={[{ max: 200 }]}>
            <Input className="medium-input" />
          </Form.Item>
          <Form.Item name="note" label={t('clients.note')} extra={t('clients.noteHelp')} rules={[{ max: 4000 }]}>
            <Input.TextArea className="medium-input" autoSize={{ minRows: 1, maxRows: 6 }} />
          </Form.Item>
          <Form.Item label=" " style={{ marginBottom: 4 }}>
            <Form.Item name="isSpammer" valuePropName="checked" noStyle>
              <Checkbox>{t('clients.spammer')}</Checkbox>
            </Form.Item>
          </Form.Item>
          <Form.Item label=" ">
            <Form.Item name="isVip" valuePropName="checked" noStyle>
              <Checkbox>VIP</Checkbox>
            </Form.Item>
          </Form.Item>
        </SettingsForm>
      </SectionCard>

    </Page>
  )
}
