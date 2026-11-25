import { Alert, App as AntApp, Button, Checkbox, Collapse, Empty, Form, Input, InputNumber, Popconfirm, Select, Space, Spin, Typography } from 'antd'
import { MinusOutlined, PlusOutlined } from '@ant-design/icons'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { useNavigate, useParams } from 'react-router'
import {
  actionTypes,
  conditionFields,
  rulesApi,
  triggerTypes,
  type RuleAction,
  type RuleCondition,
  type RuleInput,
  type RuleTrigger,
  type ValueKind,
} from '../../api/automation'
import { settingsApi } from '../../api/settings'
import { formatDateTime } from '../../format'
import { Page, SectionCard } from '../../layout/Page'
import { errorText } from '../../settings/errors'

const blank: RuleInput = {
  name: '',
  description: null,
  isEnabled: true,
  isManual: false,
  useWordForms: false,
  ignoreHtml: true,
  stopProcessing: false,
  definition: { whenAll: [{ type: 'client_message' }], whenAny: [], whatAll: [], whatAny: [], actions: [{ type: 'send_message', value: '' }] },
}

const newCondition = (): RuleCondition => ({ field: 'channel_type', operator: 'eq', value: null })

/**
 * Правило: «When» — события, «What» — условия, «Do» — действия (п. 3.6 ТЗ).
 * В «All conditions» должны выполниться все строки, в «Any condition» — хотя бы одна.
 */
export function RulePage() {
  const { t } = useTranslation()
  const { message } = AntApp.useApp()
  const { ruleId } = useParams()
  const isNew = ruleId === undefined
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const rule = useQuery({ queryKey: ['rule', ruleId], queryFn: () => rulesApi.get(ruleId!), enabled: !isNew })
  const [draft, setDraft] = useState<RuleInput>(blank)

  useEffect(() => {
    if (rule.data) {
      const { id: _id, createdAt: _c, updatedAt: _u, revisions: _r, ...input } = rule.data
      setDraft(input)
    }
  }, [rule.data])

  const set = (patch: Partial<RuleInput>) => setDraft((d) => ({ ...d, ...patch }))
  const setDef = (patch: Partial<RuleInput['definition']>) => setDraft((d) => ({ ...d, definition: { ...d.definition, ...patch } }))

  const save = useMutation({
    mutationFn: () => (isNew ? rulesApi.create(draft) : rulesApi.update(ruleId, draft)),
    onSuccess: (saved) => {
      void queryClient.invalidateQueries({ queryKey: ['rules'] })
      queryClient.setQueryData(['rule', saved.id], saved)
      message.success(t('settings.saved'))
      if (isNew) navigate(`/automation/rules/${saved.id}`, { replace: true })
    },
    onError: (e) => message.error(errorText(e, t('settings.saveFailed'))),
  })
  const remove = useMutation({
    mutationFn: () => rulesApi.remove(ruleId!),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['rules'] })
      message.success(t('rules.deleted'))
      navigate('/automation/rules')
    },
    onError: (e) => message.error(errorText(e, t('settings.saveFailed'))),
  })

  const crumbs = [
    { title: t('nav.automation') },
    { title: t('nav.rules'), to: '/automation/rules' },
    { title: isNew ? t('rules.new') : t('rules.edit') },
  ]
  if (!isNew && rule.isLoading) return <Page crumbs={crumbs}><Spin style={{ margin: 48 }} /></Page>
  if (!isNew && !rule.data) return <Page crumbs={crumbs}><Empty description={t('rules.notFound')} /></Page>

  const d = draft.definition
  return (
    <Page crumbs={crumbs}>
      <Alert type="info" showIcon title={t('rules.engineNote')} />

      <div className="rule-top">
        <Form layout="horizontal" labelCol={{ flex: '110px' }} wrapperCol={{ flex: 1 }} colon={false}>
          <Form.Item label={t('rules.name')} required>
            <Input value={draft.name} maxLength={200} onChange={(e) => set({ name: e.target.value })} />
          </Form.Item>
          <Form.Item label={t('rules.description')}>
            <Input.TextArea
              value={draft.description ?? ''}
              autoSize={{ minRows: 3, maxRows: 10 }}
              maxLength={4000}
              onChange={(e) => set({ description: e.target.value })}
            />
          </Form.Item>
          <Form.Item label=" ">
            <div className="rule-flags">
              <Checkbox checked={draft.isEnabled} onChange={(e) => set({ isEnabled: e.target.checked })}>
                {t('rules.enabled')}
              </Checkbox>
              <Checkbox checked={draft.isManual} onChange={(e) => set({ isManual: e.target.checked })}>
                {t('rules.manual')}
              </Checkbox>
              <Checkbox checked={draft.stopProcessing} onChange={(e) => set({ stopProcessing: e.target.checked })}>
                {t('rules.stopProcessing')}
              </Checkbox>
              <Checkbox checked={draft.useWordForms} onChange={(e) => set({ useWordForms: e.target.checked })}>
                {t('rules.useWordForms')}
              </Checkbox>
              <Checkbox checked={draft.ignoreHtml} onChange={(e) => set({ ignoreHtml: e.target.checked })}>
                {t('rules.ignoreHtml')}
              </Checkbox>
            </div>
          </Form.Item>
        </Form>
      </div>

      <SectionCard title={t('rules.when')}>
        <TriggerGroup title={t('rules.allConditions')} items={d.whenAll} onChange={(whenAll) => setDef({ whenAll })} />
        <TriggerGroup title={t('rules.anyCondition')} items={d.whenAny} onChange={(whenAny) => setDef({ whenAny })} />
      </SectionCard>

      <SectionCard title={t('rules.what')}>
        <ConditionGroup title={t('rules.allConditions')} items={d.whatAll} onChange={(whatAll) => setDef({ whatAll })} />
        <ConditionGroup title={t('rules.anyCondition')} items={d.whatAny} onChange={(whatAny) => setDef({ whatAny })} />
      </SectionCard>

      <SectionCard title={t('rules.do')}>
        {d.actions.map((action, i) => (
          <Row key={i} onRemove={() => setDef({ actions: d.actions.filter((_, j) => j !== i) })}>
            <ActionEditor
              action={action}
              onChange={(a) => setDef({ actions: d.actions.map((x, j) => (j === i ? a : x)) })}
            />
          </Row>
        ))}
        <AddButton onClick={() => setDef({ actions: [...d.actions, { type: 'send_message', value: '' }] })}>
          {t('rules.addAction')}
        </AddButton>
      </SectionCard>

      {rule.data && (
        <Collapse
          className="rule-history"
          items={[
            {
              key: 'history',
              label: t('rules.history'),
              children: rule.data.revisions.length ? (
                rule.data.revisions.map((r) => (
                  <div key={r.id} className="rule-revision">
                    <Typography.Text type="secondary">{formatDateTime(r.createdAt)}</Typography.Text>
                    <Typography.Text strong>{r.userName}</Typography.Text>
                    <Typography.Text>{r.summary}</Typography.Text>
                  </div>
                ))
              ) : (
                <Typography.Text type="secondary">{t('rules.noHistory')}</Typography.Text>
              ),
            },
          ]}
        />
      )}

      <Space>
        <Button type="primary" onClick={() => save.mutate()} loading={save.isPending}>
          {t('settings.save')}
        </Button>
        {!isNew && (
          <Popconfirm title={t('rules.deleteConfirm')} onConfirm={() => remove.mutate()} okButtonProps={{ danger: true }}>
            <Button danger type="primary" loading={remove.isPending}>
              {t('rules.delete')}
            </Button>
          </Popconfirm>
        )}
        <Button onClick={() => navigate('/automation/rules')}>{t('settings.cancel')}</Button>
      </Space>
    </Page>
  )
}

function Row({ children, onRemove }: { children: React.ReactNode; onRemove: () => void }) {
  const { t } = useTranslation()
  return (
    <div className="rule-item">
      <div className="rule-item-fields">{children}</div>
      <Button color="gold" variant="solid" icon={<MinusOutlined />} onClick={onRemove} aria-label={t('rules.remove')} title={t('rules.remove')} />
    </div>
  )
}

function AddButton({ onClick, children }: { onClick: () => void; children: React.ReactNode }) {
  return (
    <Button color="green" variant="solid" icon={<PlusOutlined />} onClick={onClick}>
      {children}
    </Button>
  )
}

function TriggerGroup({ title, items, onChange }: { title: string; items: RuleTrigger[]; onChange: (items: RuleTrigger[]) => void }) {
  const { t } = useTranslation()
  return (
    <div className="rule-group">
      <div className="rule-subhead">{title}</div>
      {items.map((item, i) => {
        const type = triggerTypes.find((x) => x.type === item.type)
        const update = (patch: Partial<RuleTrigger>) => onChange(items.map((x, j) => (j === i ? { ...x, ...patch } : x)))
        return (
          <Row key={i} onRemove={() => onChange(items.filter((_, j) => j !== i))}>
            <Select
              value={item.type}
              onChange={(v) => update({ type: v, minutes: triggerTypes.find((x) => x.type === v)?.hasMinutes ? (item.minutes ?? 15) : null })}
              options={triggerTypes.map((x) => ({ value: x.type, label: t(`rules.triggers.${x.type}`) }))}
            />
            {type?.hasMinutes ? (
              <Space.Compact>
                <InputNumber min={1} max={10080} value={item.minutes ?? undefined} onChange={(v) => update({ minutes: v ?? null })} style={{ width: 120 }} />
                <Button disabled tabIndex={-1} style={{ color: 'rgba(0,0,0,0.65)', cursor: 'default' }}>
                  {t('ldap.minutes')}
                </Button>
              </Space.Compact>
            ) : (
              <span />
            )}
            <span />
          </Row>
        )
      })}
      <AddButton onClick={() => onChange([...items, { type: 'client_message' }])}>{t('rules.addCondition')}</AddButton>
    </div>
  )
}

function ConditionGroup({ title, items, onChange }: { title: string; items: RuleCondition[]; onChange: (items: RuleCondition[]) => void }) {
  const { t } = useTranslation()
  return (
    <div className="rule-group">
      <div className="rule-subhead">{title}</div>
      {items.map((item, i) => {
        const field = conditionFields.find((x) => x.field === item.field)!
        const update = (patch: Partial<RuleCondition>) => onChange(items.map((x, j) => (j === i ? { ...x, ...patch } : x)))
        return (
          <Row key={i} onRemove={() => onChange(items.filter((_, j) => j !== i))}>
            <Select
              value={item.field}
              onChange={(v) => update({ field: v, operator: conditionFields.find((x) => x.field === v)!.operators[0]!, value: null })}
              options={conditionFields.map((x) => ({ value: x.field, label: t(`rules.fields.${x.field}`) }))}
            />
            <Select
              value={item.operator}
              onChange={(v) => update({ operator: v })}
              options={field.operators.map((o) => ({ value: o, label: t(`rules.operators.${o}`) }))}
            />
            <ValueInput kind={field.value} value={item.value} onChange={(value) => update({ value })} allowNone />
          </Row>
        )
      })}
      <AddButton onClick={() => onChange([...items, newCondition()])}>{t('rules.addCondition')}</AddButton>
    </div>
  )
}

function ActionEditor({ action, onChange }: { action: RuleAction; onChange: (a: RuleAction) => void }) {
  const { t } = useTranslation()
  const kind = actionTypes.find((x) => x.type === action.type)?.value ?? 'None'
  return (
    <>
      <Select
        value={action.type}
        onChange={(type) => onChange({ type, value: null })}
        options={actionTypes.map((x) => ({ value: x.type, label: t(`rules.actions.${x.type}`) }))}
      />
      {kind === 'Text' ? (
        <div className="rule-message">
          <Input.TextArea
            value={action.value ?? ''}
            autoSize={{ minRows: 4, maxRows: 12 }}
            maxLength={10000}
            placeholder={t('rules.messagePlaceholder')}
            onChange={(e) => onChange({ ...action, value: e.target.value })}
          />
          <Typography.Text type="secondary" style={{ fontSize: 12 }}>
            {t('rules.messageHelp')}
          </Typography.Text>
        </div>
      ) : kind === 'None' ? (
        <span />
      ) : (
        <ValueInput kind={kind} value={action.value ?? null} onChange={(value) => onChange({ ...action, value })} />
      )}
    </>
  )
}

/** Поле значения: список или текст, в зависимости от того, что ждёт условие или действие. */
function ValueInput({
  kind,
  value,
  onChange,
  allowNone,
}: {
  kind: ValueKind
  value: string | null
  onChange: (v: string | null) => void
  allowNone?: boolean
}) {
  const { t } = useTranslation()
  const widgets = useQuery({ queryKey: ['widgets'], queryFn: settingsApi.widgets, enabled: kind === 'Channel' })
  const bots = useQuery({ queryKey: ['telegramBots'], queryFn: settingsApi.telegramBots, enabled: kind === 'Channel' })
  const sites = useQuery({ queryKey: ['sites'], queryFn: settingsApi.sites, enabled: kind === 'Site' })
  const users = useQuery({ queryKey: ['users'], queryFn: settingsApi.users, enabled: kind === 'User' })

  const select = (options: { value: string; label: string }[]) => (
    <Select value={value ?? undefined} placeholder={t('rules.choose')} onChange={(v) => onChange(v)} options={options} showSearch optionFilterProp="label" />
  )

  switch (kind) {
    case 'Text':
      return <Input value={value ?? ''} onChange={(e) => onChange(e.target.value)} placeholder={t('rules.valuePlaceholder')} />
    case 'ChannelType':
      return select(['Widget', 'Telegram', 'WhatsApp'].map((c) => ({ value: c, label: t(`inbox.channel.${c}`) })))
    case 'Channel':
      return select([
        ...(widgets.data ?? []).map(({ widget: w }) => ({ value: `widget:${w.id}`, label: `${w.name} (${t('channels.widget')})` })),
        ...(bots.data ?? []).map((b) => ({ value: `telegram:${b.id}`, label: `${b.name} (Telegram)` })),
      ])
    case 'Site':
      return select((sites.data ?? []).map((s) => ({ value: s.id, label: `${s.name} (${s.code})` })))
    case 'Status':
      return select(['New', 'InProgress', 'WaitingClient', 'Closed'].map((s) => ({ value: s, label: t(`ticket.status.${s}`) })))
    case 'User':
      return select([
        ...(allowNone ? [{ value: 'none', label: t('ticket.unassigned') }] : []),
        ...(users.data ?? []).filter((u) => !u.isBlocked).map((u) => ({ value: u.id, label: u.fullName })),
      ])
    case 'Boolean':
      return select([
        { value: 'true', label: t('rules.yes') },
        { value: 'false', label: t('rules.no') },
      ])
    default:
      return <span />
  }
}
