import { App as AntApp, Button, Empty, Form, Input, Popconfirm, Select, Spin, Switch, Transfer } from 'antd'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect } from 'react'
import { useTranslation } from 'react-i18next'
import { useNavigate, useParams } from 'react-router'
import type { Role } from '../../api/client'
import { settingsApi } from '../../api/settings'
import { useAuth } from '../../auth/AuthContext'
import { Page, SectionCard, SettingsForm } from '../../layout/Page'
import { errorText } from '../../settings/errors'

const roles: Role[] = ['Operator', 'Supervisor', 'Admin']
const MIN_PASSWORD = 10

interface AgentForm {
  login: string
  email: string
  password: string
  fullName: string
  alias: string
  position: string
  phone: string
  locale: string
  role: Role
  groupIds: string[]
  defaultGroupId: string | null
  isBlocked: boolean
}

/**
 * Сотрудник: контакты, пароль, роль и группы на одной странице. У пользователей из AD имя, почту, телефон
 * и должность задаёт AD, пароль проверяет AD.
 */
export function AgentPage() {
  const { t } = useTranslation()
  const { message } = AntApp.useApp()
  const { user: me, updateUser } = useAuth()
  const { agentId } = useParams()
  const isNew = agentId === undefined
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const users = useQuery({ queryKey: ['users'], queryFn: settingsApi.users })
  const groups = useQuery({ queryKey: ['groups'], queryFn: settingsApi.groups })
  const agent = users.data?.find((u) => u.id === agentId)
  const [form] = Form.useForm<AgentForm>()
  const groupIds = Form.useWatch('groupIds', form) ?? []
  const loading = users.isLoading || groups.isLoading

  useEffect(() => {
    if (loading) return
    if (isNew)
      form.setFieldsValue({ login: '', email: '', password: '', fullName: '', alias: '', position: '', phone: '', locale: 'en', role: 'Operator', groupIds: [], defaultGroupId: null, isBlocked: false })
    else if (agent)
      form.setFieldsValue({
        login: agent.login,
        email: agent.email ?? '',
        password: '',
        fullName: agent.fullName,
        alias: agent.alias ?? '',
        position: agent.position ?? '',
        phone: agent.phone ?? '',
        locale: agent.locale,
        role: agent.role,
        groupIds: agent.groupIds,
        defaultGroupId: agent.defaultGroupId,
        isBlocked: agent.isBlocked,
      })
  }, [form, isNew, agent, loading])

  const save = useMutation({
    mutationFn: (v: AgentForm) => {
      const profile = { email: v.email || null, position: v.position || null, phone: v.phone || null, locale: v.locale, groupIds: v.groupIds, defaultGroupId: v.defaultGroupId }
      return isNew
        ? settingsApi.createUser({ login: v.login, fullName: v.fullName, role: v.role, alias: v.alias || undefined, password: v.password, profile })
        : settingsApi.updateUser(agentId, {
            fullName: v.fullName,
            alias: v.alias || null,
            role: v.role,
            isBlocked: v.isBlocked,
            profile,
            password: v.password || undefined,
          })
    },
    onSuccess: (saved) => {
      void queryClient.invalidateQueries({ queryKey: ['users'] })
      void queryClient.invalidateQueries({ queryKey: ['groups'] })
      message.success(t('settings.saved'))
      // Свои ФИО и псевдоним сразу видны в боковой панели.
      if (me && saved.id === me.id) updateUser({ ...me, fullName: saved.fullName, alias: saved.alias, role: saved.role })
      navigate('/agents')
    },
    onError: (e) => message.error(errorText(e, t('settings.saveFailed'))),
  })

  const remove = useMutation({
    mutationFn: () => settingsApi.deleteUser(agentId!),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['users'] })
      message.success(t('agents.deleted'))
      navigate('/agents')
    },
    onError: (e) => message.error(errorText(e, t('settings.saveFailed'))),
  })

  const crumbs = [{ title: t('nav.agents'), to: '/agents' }, { title: isNew ? t('agents.create') : t('agents.edit') }]
  if (loading) return <Page crumbs={crumbs}><Spin style={{ margin: 48 }} /></Page>
  if (!isNew && !agent) return <Page crumbs={crumbs}><Empty description={t('users.notFound')} /></Page>

  const fromAd = agent?.source === 'ActiveDirectory'
  const adHint = fromAd ? t('users.fromAd') : undefined
  const groupOptions = (groups.data ?? []).map((g) => ({ key: g.id, title: g.name }))

  return (
    <Page crumbs={crumbs}>
      <SectionCard title={isNew ? t('agents.create') : agent!.fullName}>
        <SettingsForm<AgentForm>
          form={form}
          onFinish={(v) => save.mutate(v)}
          saving={save.isPending}
          onCancel={() => navigate('/agents')}
          actions={
            !isNew && (
              <Popconfirm title={t('agents.deleteConfirm')} onConfirm={() => remove.mutate()} okButtonProps={{ danger: true }}>
                <Button
                  danger
                  type="primary"
                  loading={remove.isPending}
                  disabled={agent?.id === me?.id || agent?.source === 'ActiveDirectory'}
                  title={agent?.source === 'ActiveDirectory' ? t('agents.deleteAd') : undefined}
                >
                  {t('agents.delete')}
                </Button>
              </Popconfirm>
            )
          }
        >
          <Form.Item name="login" label={t('users.login')} extra={t('agents.loginHelp')} rules={[{ required: true, whitespace: true, max: 256 }]}>
            <Input disabled={!isNew} autoComplete="off" />
          </Form.Item>
          <Form.Item name="email" label={t('agents.email')} extra={adHint ?? t('agents.emailHelp')} rules={[{ type: 'email', max: 256 }]}>
            <Input disabled={fromAd} autoComplete="off" />
          </Form.Item>
          <Form.Item
            name="password"
            label={t('login.password')}
            extra={fromAd ? t('agents.passwordAd') : isNew ? t('agents.passwordHelp') : t('agents.passwordKeep')}
            rules={
              fromAd
                ? []
                : [{ required: isNew, min: MIN_PASSWORD, message: t('users.passwordRule', { min: MIN_PASSWORD }) }]
            }
          >
            <Input.Password disabled={fromAd} autoComplete="new-password" />
          </Form.Item>
          <Form.Item name="fullName" label={t('agents.name')} extra={adHint ?? t('agents.nameHelp')} rules={[{ required: true, max: 256 }]}>
            <Input disabled={fromAd} />
          </Form.Item>
          <Form.Item name="alias" label={t('alias.label')} extra={t('alias.help')} rules={[{ max: 100 }]}>
            <Input />
          </Form.Item>
          <Form.Item name="position" label={t('agents.position')} extra={adHint} rules={[{ max: 200 }]}>
            <Input disabled={fromAd} />
          </Form.Item>
          <Form.Item name="phone" label={t('agents.phone')} extra={adHint ?? t('agents.phoneHelp')} rules={[{ max: 50 }]}>
            <Input disabled={fromAd} />
          </Form.Item>
          <Form.Item name="locale" label={t('agents.language')}>
            <Select
              options={[
                { value: 'en', label: 'English' },
                { value: 'ru', label: `Русский · ${t('common.comingSoon')}`, disabled: true },
              ]}
            />
          </Form.Item>
          <Form.Item name="role" label={t('users.role')}>
            <Select options={roles.map((r) => ({ value: r, label: t(`user.roles.${r}`) }))} />
          </Form.Item>
          <Form.Item label={t('agents.groups')} extra={groupOptions.length ? undefined : t('agents.noGroupsHint')}>
            <div className="agent-groups">
              <Form.Item name="groupIds" valuePropName="targetKeys" noStyle>
                <Transfer
                  dataSource={groupOptions}
                  titles={[t('agents.notMember'), t('agents.member')]}
                  render={(item) => item.title}
                  showSelectAll={false}
                  oneWay={false}
                  styles={{ section: { width: 190, height: 190 } }}
                  locale={{ notFoundContent: '—', itemUnit: '', itemsUnit: '' }}
                />
              </Form.Item>
              <Form.Item name="defaultGroupId" label={t('agents.defaultGroup')} layout="vertical" style={{ marginBottom: 0, minWidth: 200 }}>
                <Select
                  allowClear
                  placeholder={t('agents.noDefaultGroup')}
                  options={(groups.data ?? []).filter((g) => groupIds.includes(g.id)).map((g) => ({ value: g.id, label: g.name }))}
                />
              </Form.Item>
            </div>
          </Form.Item>
          {!isNew && (
            <Form.Item
              name="isBlocked"
              label={t('users.blocked')}
              valuePropName="checked"
              extra={agent?.blockedByDirectory ? t('users.blockedInAdHelp') : t('users.blockHelp')}
            >
              <Switch />
            </Form.Item>
          )}
        </SettingsForm>
      </SectionCard>
    </Page>
  )
}
