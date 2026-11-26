import { App as AntApp, Button, Input, Select, Space, Table, Tag, Tooltip, Typography } from 'antd'
import { CaretRightOutlined, PlusOutlined, SearchOutlined } from '@ant-design/icons'
import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect, useMemo, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Link, useNavigate, useSearchParams } from 'react-router'
import { clientsApi } from '../api/clients'
import { ticketsApi, type Channel, type TicketFilter, type TicketStatus, type TicketSummary } from '../api/tickets'
import { useAuth } from '../auth/AuthContext'
import { statusColor } from '../inbox/status'
import { ChannelIcon } from '../inbox/visuals'
import { Page, SoonTag } from '../layout/Page'
import { errorText } from '../settings/errors'
import { formatDateTime } from '../format'

const OPEN: TicketStatus[] = ['New', 'InProgress', 'WaitingClient']
const ALL_STATUSES: TicketStatus[] = [...OPEN, 'Closed']

/** Готовые фильтры слева, как «Сохранённые фильтры» Usedesk. */
const views: { key: string; statuses?: TicketStatus[]; assignee?: string }[] = [
  { key: 'new', statuses: ['New'] },
  { key: 'mine', statuses: OPEN, assignee: 'me' },
  { key: 'unassigned', statuses: OPEN, assignee: 'none' },
  { key: 'inProgress', statuses: ['InProgress'] },
  { key: 'waiting', statuses: ['WaitingClient'] },
  { key: 'closed', statuses: ['Closed'] },
  { key: 'all' },
]

const periods = ['today', '7d', '30d'] as const

/** Начало периода: сегодня с полуночи, 7 или 30 дней назад. */
function periodStart(period: string | null): string | undefined {
  if (!period) return undefined
  const d = new Date()
  if (period === 'today') d.setHours(0, 0, 0, 0)
  else d.setDate(d.getDate() - (period === '7d' ? 7 : 30))
  return d.toISOString()
}

/** Ключ, под которым список сохраняет порядок запросов для кнопок «‹ ›» на странице запроса. */
export const REQUEST_ORDER_KEY = 'relayo.requests.order'

/**
 * Раздел «Запросы», как в Usedesk: слева «Создать запрос», «Взять запрос» и сохранённые фильтры, справа строка
 * фильтров, поиск и таблица запросов. Фильтры хранятся в адресе страницы: после возврата из запроса они те же.
 */
export function RequestsPage() {
  const { t } = useTranslation()
  const { message } = AntApp.useApp()
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const { user } = useAuth()
  const [params, setParams] = useSearchParams()
  const [text, setText] = useState(params.get('search') ?? '')
  const [selected, setSelected] = useState<string[]>([])

  const page = Number(params.get('page') ?? 1)
  const pageSize = Number(params.get('pageSize') ?? 25)
  const filter: TicketFilter = {
    statuses: params.get('status')?.split(',').filter(Boolean) as TicketStatus[] | undefined,
    channel: (params.get('channel') as Channel) || undefined,
    assignee: params.get('assignee') ?? undefined,
    groupId: params.get('groupId') ?? undefined,
    siteId: params.get('siteId') ?? undefined,
    search: params.get('search') ?? undefined,
    createdFrom: periodStart(params.get('created')),
    updatedFrom: periodStart(params.get('updated')),
  }

  const set = (changes: Record<string, string | undefined>) => {
    const next = new URLSearchParams(params)
    for (const [k, v] of Object.entries(changes)) {
      if (v) next.set(k, v)
      else next.delete(k)
    }
    if (!('page' in changes)) next.delete('page')
    setParams(next, { replace: true })
    setSelected([])
  }

  // Поиск уходит, когда перестали печатать.
  useEffect(() => {
    const timer = setTimeout(() => {
      if ((params.get('search') ?? '') !== text.trim()) set({ search: text.trim() || undefined, view: undefined })
    }, 400)
    return () => clearTimeout(timer)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [text])

  const list = useQuery({
    queryKey: ['tickets', 'search', params.toString()],
    queryFn: () => ticketsApi.search(filter, page, pageSize),
    placeholderData: keepPreviousData,
  })
  const agents = useQuery({ queryKey: ['agents'], queryFn: ticketsApi.agents, staleTime: 60_000 })
  const groups = useQuery({ queryKey: ['ticket-groups'], queryFn: ticketsApi.groups, staleTime: 60_000 })
  const sites = useQuery({ queryKey: ['clientSites'], queryFn: clientsApi.sites, staleTime: 60_000 })

  // Порядок для «‹ ›» на странице запроса.
  useEffect(() => {
    if (list.data) sessionStorage.setItem(REQUEST_ORDER_KEY, JSON.stringify(list.data.items.map((x) => x.id)))
  }, [list.data])

  const take = useMutation({
    mutationFn: ticketsApi.takeNext,
    onSuccess: (ticket) => {
      if (!ticket) message.info(t('requests.nothingToTake'))
      else navigate(`/requests/${ticket.id}`)
    },
    onError: (e) => message.error(errorText(e, t('settings.saveFailed'))),
  })

  const bulk = useMutation({
    mutationFn: async (action: 'take' | 'close') => {
      for (const id of selected) {
        if (action === 'take') await ticketsApi.setAssignee(id, user!.id)
        else await ticketsApi.setStatus(id, 'Closed')
      }
    },
    onSettled: () => {
      setSelected([])
      void queryClient.invalidateQueries({ queryKey: ['tickets'] })
    },
    onError: (e) => message.error(errorText(e, t('settings.saveFailed'))),
  })

  const activeView = params.get('view')
  const agentOptions = useMemo(
    () => [
      { value: 'me', label: t('requests.assigneeMe') },
      { value: 'none', label: t('ticket.unassigned') },
      ...(agents.data ?? []).map((a) => ({ value: a.id, label: a.fullName })),
    ],
    [agents.data, t],
  )

  return (
    <Page crumbs={[{ title: t('nav.requests') }]}>
      <div className="requests-layout">
        <aside className="requests-side">
          {/* Запросы, которые начинает сам сотрудник (письмо, сообщение клиенту), — позже. */}
          <Button icon={<PlusOutlined />} disabled title={t('requests.createHelp')}>
            {t('requests.create')}
          </Button>
          <Button block type="primary" icon={<CaretRightOutlined />} loading={take.isPending} onClick={() => take.mutate()}>
            {t('requests.take')}
          </Button>
          <div className="requests-side-title">{t('requests.savedFilters')}</div>
          {views.map((v) => (
            <a
              key={v.key}
              className={`requests-view${activeView === v.key ? ' active' : ''}`}
              onClick={() => {
                setText('')
                setParams(new URLSearchParams({ view: v.key, ...(v.statuses ? { status: v.statuses.join(',') } : {}), ...(v.assignee ? { assignee: v.assignee } : {}) }), { replace: true })
              }}
            >
              {t(`requests.views.${v.key}`)}
            </a>
          ))}
          <Tooltip title={t('common.comingSoonHelp')}>
            <span className="requests-view disabled">
              {t('requests.saveFilter')} <SoonTag />
            </span>
          </Tooltip>
        </aside>

        <div className="requests-main">
          <div className="requests-filters">
            <Select
              allowClear
              variant="borderless"
              placeholder={t('requests.filters.channel')}
              value={filter.channel}
              onChange={(v) => set({ channel: v, view: undefined })}
              options={(['Widget', 'Telegram', 'WhatsApp'] as Channel[]).map((c) => ({ value: c, label: t(`inbox.channel.${c}`) }))}
              popupMatchSelectWidth={false}
            />
            <Select
              allowClear
              showSearch
              optionFilterProp="label"
              variant="borderless"
              placeholder={t('requests.filters.assignee')}
              value={filter.assignee}
              onChange={(v) => set({ assignee: v, view: undefined })}
              options={agentOptions}
              popupMatchSelectWidth={false}
            />
            <Select
              allowClear
              variant="borderless"
              placeholder={t('requests.filters.group')}
              value={filter.groupId}
              onChange={(v) => set({ groupId: v, view: undefined })}
              options={groups.data?.map((g) => ({ value: g.id, label: g.name }))}
              popupMatchSelectWidth={false}
            />
            <Select
              allowClear
              mode="multiple"
              maxTagCount="responsive"
              variant="borderless"
              placeholder={t('requests.filters.status')}
              value={filter.statuses ?? []}
              onChange={(v: TicketStatus[]) => set({ status: v.length ? v.join(',') : undefined, view: undefined })}
              options={ALL_STATUSES.map((s) => ({ value: s, label: t(`ticket.status.${s}`) }))}
              style={{ minWidth: 140 }}
              popupMatchSelectWidth={false}
            />
            <Select
              allowClear
              variant="borderless"
              placeholder={t('requests.filters.site')}
              value={filter.siteId}
              onChange={(v) => set({ siteId: v, view: undefined })}
              options={sites.data?.map((s) => ({ value: s.id, label: `${s.name} (${s.code})` }))}
              popupMatchSelectWidth={false}
            />
            {(['updated', 'created'] as const).map((key) => (
              <Select
                key={key}
                allowClear
                variant="borderless"
                placeholder={t(`requests.filters.${key}`)}
                value={params.get(key) ?? undefined}
                onChange={(v) => set({ [key]: v, view: undefined })}
                options={periods.map((p) => ({ value: p, label: `${t(`requests.filters.${key}`)}: ${t(`requests.periods.${p}`)}` }))}
                popupMatchSelectWidth={false}
              />
            ))}
            {(['type', 'priority', 'tags', 'sla', 'company'] as const).map((key) => (
              <Tooltip key={key} title={t('common.comingSoon')}>
                <Select disabled variant="borderless" placeholder={t(`requests.filters.${key}`)} popupMatchSelectWidth={false} />
              </Tooltip>
            ))}
          </div>

          <Input
            allowClear
            value={text}
            onChange={(e) => setText(e.target.value)}
            prefix={<SearchOutlined style={{ color: '#9aa0a7' }} />}
            placeholder={t('requests.search')}
            className="requests-search"
          />

          {selected.length > 0 && (
            <Space className="requests-bulk">
              <Typography.Text>{t('requests.selected', { count: selected.length })}</Typography.Text>
              <Button size="small" loading={bulk.isPending} onClick={() => bulk.mutate('take')}>
                {t('requests.bulkTake')}
              </Button>
              <Button size="small" loading={bulk.isPending} onClick={() => bulk.mutate('close')}>
                {t('requests.bulkClose')}
              </Button>
            </Space>
          )}

          <Table<TicketSummary>
            rowKey="id"
            size="middle"
            bordered
            loading={list.isFetching}
            dataSource={list.data?.items}
            rowSelection={{ selectedRowKeys: selected, onChange: (keys) => setSelected(keys as string[]), getCheckboxProps: (r) => ({ disabled: r.status === 'Closed' }) }}
            onRow={(r) => ({ onClick: () => navigate(`/requests/${r.id}`), style: { cursor: 'pointer' } })}
            locale={{ emptyText: t('requests.empty') }}
            pagination={{
              current: page,
              pageSize,
              total: list.data?.total,
              showSizeChanger: true,
              pageSizeOptions: [25, 50, 100],
              showTotal: (total, [from, to]) => t('requests.range', { from, to, total }),
              onChange: (p, size) => set({ page: String(p), pageSize: String(size) }),
            }}
            columns={[
              {
                title: t('requests.columns.status'),
                width: 130,
                render: (_, r) => <Tag color={statusColor[r.status]}>{t(`ticket.status.${r.status}`)}</Tag>,
              },
              {
                title: t('requests.columns.subject'),
                render: (_, r) => (
                  <div className="request-subject">
                    <Link to={`/requests/${r.id}`} onClick={(e) => e.stopPropagation()} className="request-subject-title">
                      <ChannelIcon channel={r.lastClientChannel} /> {r.subject ?? t('requests.chat')}
                    </Link>
                    <Typography.Text type="secondary" ellipsis className="request-subject-preview">
                      {r.lastMessagePreview}
                    </Typography.Text>
                    <div>
                      <Tag>#{r.number}</Tag>
                      <Tag>{r.clientLabel}</Tag>
                      {r.groupName && <Tag>{r.groupName}</Tag>}
                      {r.source === 'Usedesk' && <Tag color="purple">Usedesk</Tag>}
                    </div>
                  </div>
                ),
              },
              { title: t('requests.columns.agent'), width: '12%', render: (_, r) => r.assigneeName ?? '' },
              { title: 'SLA', width: 60, render: () => '' },
              { title: t('requests.columns.created'), width: 150, render: (_, r) => <RelativeDate iso={r.createdAt} /> },
              { title: t('requests.columns.updated'), width: 150, render: (_, r) => <RelativeDate iso={r.lastMessageAt} /> },
              {
                title: t('requests.columns.channel'),
                width: '14%',
                render: (_, r) => (
                  <Typography.Text type="secondary">
                    {r.source === 'Usedesk' && r.sourceChannel ? `Usedesk · ${r.sourceChannel}` : t(`inbox.channel.${r.lastClientChannel}`)}
                    <br />
                    {r.siteCode}
                  </Typography.Text>
                ),
              },
            ]}
          />
        </div>
      </div>
    </Page>
  )
}

/** «сегодня в 16:52», «вчера в 09:10» или дата — как в списке запросов Usedesk. */
export function RelativeDate({ iso }: { iso: string }) {
  const { t } = useTranslation()
  const date = new Date(iso)
  const time = date.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })
  const yesterday = new Date()
  yesterday.setDate(yesterday.getDate() - 1)
  const text =
    date.toDateString() === new Date().toDateString()
      ? t('requests.todayAt', { time })
      : date.toDateString() === yesterday.toDateString()
        ? t('requests.yesterdayAt', { time })
        : formatDateTime(iso)
  return <Typography.Text type="secondary">{text}</Typography.Text>
}
