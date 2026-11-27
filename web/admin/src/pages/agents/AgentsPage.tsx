import { App as AntApp, Button, Card, Form, Input, Modal, Popconfirm, Space, Table, Tabs, Tooltip, Typography } from 'antd'
import { DeleteOutlined, EditOutlined, SearchOutlined, UserOutlined } from '@ant-design/icons'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Link, useNavigate, useSearchParams } from 'react-router'
import type { Role } from '../../api/client'
import { settingsApi, type AgentGroup, type OperatorStatus, type StaffUser } from '../../api/settings'
import { Page } from '../../layout/Page'
import { errorText } from '../../settings/errors'

type TabKey = 'all' | Role | 'groups'

/**
 * Сотрудники (п. 3.1 ТЗ): поиск, вкладки по ролям, статус оператора переключается прямо в списке.
 * Последняя вкладка — группы агентов (отделы).
 */
export function AgentsPage() {
  const { t } = useTranslation()
  const { message } = AntApp.useApp()
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const users = useQuery({ queryKey: ['users'], queryFn: settingsApi.users })
  const groups = useQuery({ queryKey: ['groups'], queryFn: settingsApi.groups })
  // ?tab=groups — ссылка на группы (например, со страницы импорта из Usedesk).
  const [params] = useSearchParams()
  const [tab, setTab] = useState<TabKey>(params.get('tab') === 'groups' ? 'groups' : 'all')
  const [search, setSearch] = useState('')
  const [groupModal, setGroupModal] = useState<AgentGroup | 'new' | null>(null)

  const setStatus = useMutation({
    mutationFn: ({ id, status }: { id: string; status: OperatorStatus }) => settingsApi.setUserStatus(id, status),
    onMutate: ({ id, status }) =>
      queryClient.setQueryData<StaffUser[]>(['users'], (old) => old?.map((u) => (u.id === id ? { ...u, status } : u))),
    onError: (e) => {
      message.error(errorText(e, t('settings.saveFailed')))
      void queryClient.invalidateQueries({ queryKey: ['users'] })
    },
  })

  const q = search.trim().toLowerCase()
  const agents = (users.data ?? []).filter(
    (u) =>
      (tab === 'all' || u.role === tab) &&
      [u.email, u.fullName, u.login, u.alias, u.position, u.phone].some((v) => v?.toLowerCase().includes(q)),
  )
  const groupRows = (groups.data ?? []).filter((g) => g.name.toLowerCase().includes(q))

  return (
    <Page crumbs={[{ title: t('nav.agents') }]}>
      <Card>
        <Input
          size="large"
          allowClear
          placeholder={t('agents.search')}
          suffix={<SearchOutlined style={{ color: '#a1a8b0' }} />}
          value={search}
          onChange={(e) => setSearch(e.target.value)}
          className="agents-search"
        />
        <div className="agents-toolbar">
          <Typography.Text type="secondary">
            {t('users.adNote')} <Link to="/settings/ldap">{t('users.adNoteLink')}</Link>
          </Typography.Text>
          <Space>
            <Button type="primary" onClick={() => navigate('/agents/new')}>
              {t('agents.create')}
            </Button>
            <Button onClick={() => setGroupModal('new')}>{t('agents.createGroup')}</Button>
          </Space>
        </div>

        <Tabs
          type="card"
          activeKey={tab}
          onChange={(k) => setTab(k as TabKey)}
          items={[
            { key: 'all', label: t('agents.tabs.all') },
            { key: 'Admin', label: t('agents.tabs.admins') },
            { key: 'Supervisor', label: t('agents.tabs.supervisors') },
            { key: 'Operator', label: t('agents.tabs.operators') },
            { key: 'groups', label: t('agents.tabs.groups') },
          ]}
        />

        {tab === 'groups' ? (
          <Table<AgentGroup>
            rowKey="id"
            className="striped"
            loading={groups.isLoading}
            dataSource={groupRows}
            pagination={false}
            locale={{ emptyText: t('agents.noGroups') }}
            columns={[
              { title: t('agents.groupName'), dataIndex: 'name' },
              { title: t('agents.memberCount'), dataIndex: 'memberCount', width: 200 },
              {
                width: 100,
                align: 'right',
                render: (_, g) => <GroupActions group={g} onRename={() => setGroupModal(g)} />,
              },
            ]}
          />
        ) : (
          <Table<StaffUser>
            rowKey="id"
            className="striped"
            loading={users.isLoading}
            dataSource={agents}
            pagination={false}
            locale={{ emptyText: t('agents.notFound') }}
            columns={[
              {
                title: t('agents.email'),
                width: '22%',
                render: (_, u) => <Link to={`/agents/${u.id}`}>{u.email ?? <Typography.Text type="secondary">{u.login}</Typography.Text>}</Link>,
              },
              {
                title: t('agents.name'),
                // ФИО, под ним — только псевдоним, который видят клиенты.
                render: (_, u) => (
                  <div className="agent-name">
                    <Link to={`/agents/${u.id}`}>{u.fullName}</Link>
                    {u.alias ? (
                      <Typography.Text type="secondary">{u.alias}</Typography.Text>
                    ) : (
                      <Typography.Text type="warning">{t('users.noAlias')}</Typography.Text>
                    )}
                  </div>
                ),
              },
              { title: t('agents.position'), dataIndex: 'position', render: (v) => v ?? '' },
              { title: t('agents.phone'), dataIndex: 'phone', width: 170, render: (v) => v ?? '' },
              {
                title: t('agents.chat'),
                width: 130,
                render: (_, u) => (
                  <StatusSwitch
                    status={u.status}
                    disabled={u.isBlocked}
                    onChange={(status) => setStatus.mutate({ id: u.id, status })}
                  />
                ),
              },
              {
                width: 80,
                align: 'right',
                render: (_, u) => (
                  <Space size={12}>
                    <Tooltip title={t('agents.open')}>
                      <Link to={`/agents/${u.id}`} aria-label={t('agents.open')}>
                        <UserOutlined />
                      </Link>
                    </Tooltip>
                    <Tooltip title={t('agents.edit')}>
                      <Link to={`/agents/${u.id}`} aria-label={t('agents.edit')}>
                        <EditOutlined />
                      </Link>
                    </Tooltip>
                  </Space>
                ),
              },
            ]}
          />
        )}
      </Card>

      <GroupModal
        group={groupModal}
        onClose={(created) => {
          setGroupModal(null)
          if (created) setTab('groups')
        }}
      />
    </Page>
  )
}

/** Переключатель статуса в виде пилюли: Online — зелёный, Offline — серый, Break — жёлтый. */
function StatusSwitch({ status, disabled, onChange }: { status: OperatorStatus; disabled?: boolean; onChange: (s: OperatorStatus) => void }) {
  const { t } = useTranslation()
  const online = status === 'Online'
  return (
    <button
      type="button"
      className={`status-switch ${status.toLowerCase()}`}
      disabled={disabled}
      title={online ? t('agents.goOffline') : t('agents.goOnline')}
      onClick={() => onChange(online ? 'Offline' : 'Online')}
    >
      <span>{t(`agents.status.${status}`)}</span>
    </button>
  )
}

function GroupActions({ group, onRename }: { group: AgentGroup; onRename: () => void }) {
  const { t } = useTranslation()
  const { message } = AntApp.useApp()
  const queryClient = useQueryClient()
  const remove = useMutation({
    mutationFn: () => settingsApi.deleteGroup(group.id),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['groups'] })
      void queryClient.invalidateQueries({ queryKey: ['users'] })
    },
    onError: (e) => message.error(errorText(e, t('settings.saveFailed'))),
  })
  return (
    <Space size={12}>
      <Tooltip title={t('agents.renameGroup')}>
        <Button type="text" size="small" icon={<EditOutlined />} onClick={onRename} aria-label={t('agents.renameGroup')} />
      </Tooltip>
      <Popconfirm title={t('agents.deleteGroupConfirm', { count: group.memberCount })} onConfirm={() => remove.mutate()} okButtonProps={{ danger: true }}>
        <Button type="text" size="small" danger icon={<DeleteOutlined />} aria-label={t('agents.deleteGroup')} />
      </Popconfirm>
    </Space>
  )
}

function GroupModal({ group, onClose }: { group: AgentGroup | 'new' | null; onClose: (created: boolean) => void }) {
  const { t } = useTranslation()
  const { message } = AntApp.useApp()
  const queryClient = useQueryClient()
  const [form] = Form.useForm<{ name: string }>()
  const save = useMutation({
    mutationFn: ({ name }: { name: string }) =>
      group === 'new' ? settingsApi.createGroup(name) : settingsApi.renameGroup((group as AgentGroup).id, name),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['groups'] })
      message.success(t('settings.saved'))
      onClose(group === 'new')
    },
    onError: (e) => message.error(errorText(e, t('settings.saveFailed'))),
  })
  return (
    <Modal
      open={group !== null}
      title={group === 'new' ? t('agents.createGroup') : t('agents.renameGroup')}
      okText={t('settings.save')}
      onOk={() => form.validateFields().then((v) => save.mutate(v))}
      confirmLoading={save.isPending}
      onCancel={() => onClose(false)}
      destroyOnHidden
      afterOpenChange={(open) => open && form.setFieldsValue({ name: group && group !== 'new' ? group.name : '' })}
    >
      <Form form={form} layout="vertical">
        <Form.Item name="name" label={t('agents.groupName')} rules={[{ required: true, whitespace: true, max: 100 }]}>
          <Input autoFocus placeholder={t('agents.groupPlaceholder')} />
        </Form.Item>
      </Form>
    </Modal>
  )
}
