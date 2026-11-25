import { App as AntApp, Alert, Button, Card, Dropdown, Empty, Spin, Tag, Typography } from 'antd'
import { DownOutlined, HolderOutlined, PlusOutlined } from '@ant-design/icons'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import { useNavigate } from 'react-router'
import { rulesApi, type RuleSummary } from '../../api/automation'
import { formatDateTime } from '../../format'
import { Page } from '../../layout/Page'
import { errorText } from '../../settings/errors'

/**
 * Правила списком в порядке применения: верхнее применяется первым. Порядок меняется перетаскиванием.
 */
export function RulesPage() {
  const { t } = useTranslation()
  const { message } = AntApp.useApp()
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const { data, isLoading } = useQuery({ queryKey: ['rules'], queryFn: rulesApi.list })

  const reorder = useMutation({
    mutationFn: rulesApi.reorder,
    onMutate: (order) => {
      // Список сразу показываем в новом порядке, не дожидаясь сервера.
      queryClient.setQueryData<RuleSummary[]>(['rules'], (old) =>
        old ? order.map((id) => old.find((r) => r.id === id)!).filter(Boolean) : old,
      )
    },
    onSuccess: (rules) => queryClient.setQueryData(['rules'], rules),
    onError: (e) => {
      message.error(errorText(e, t('settings.saveFailed')))
      void queryClient.invalidateQueries({ queryKey: ['rules'] })
    },
  })

  const active = data?.filter((r) => r.isEnabled) ?? []
  const disabled = data?.filter((r) => !r.isEnabled) ?? []
  const move = (list: RuleSummary[], from: string, to: string) => {
    const ids = list.map((r) => r.id)
    ids.splice(ids.indexOf(to), 0, ...ids.splice(ids.indexOf(from), 1))
    const order = list === active ? [...ids, ...disabled.map((r) => r.id)] : [...active.map((r) => r.id), ...ids]
    reorder.mutate(order)
  }

  const exportCsv = async (key: string) => {
    if (key === 'log') {
      const log = await rulesApi.log(7)
      download('relayo-rules-log.csv', 'Time;Rule;Ticket;Event;Applied;Details', log.map((e) =>
        [e.createdAt, quote(e.ruleName), e.ticketNumber, e.trigger, e.applied ? 'yes' : 'no', quote(e.details)].join(';'),
      ))
      return
    }
    download('relayo-rules.csv', 'Order;Name;Status;Run;Updated', (data ?? []).map((r, i) =>
      [i + 1, quote(r.name), r.isEnabled ? 'Enabled' : 'Disabled', r.isManual ? 'Manual' : 'Automatic', r.updatedAt].join(';'),
    ))
  }

  return (
    <Page
      crumbs={[{ title: t('nav.automation') }, { title: t('nav.rules') }]}
      extra={
        <>
          <Dropdown
            menu={{
              items: [
                { key: 'log', label: t('rules.downloadLog') },
                { key: 'csv', label: t('rules.downloadCsv') },
              ],
              onClick: ({ key }) => void exportCsv(key).catch((e) => message.error(errorText(e, t('settings.saveFailed')))),
            }}
          >
            <Button>
              {t('rules.export')} <DownOutlined />
            </Button>
          </Dropdown>
          <Button type="primary" icon={<PlusOutlined />} onClick={() => navigate('/automation/rules/new')}>
            {t('rules.create')}
          </Button>
        </>
      }
    >
      <Alert type="info" showIcon title={t('rules.engineNote')} />
      <Card>
        {isLoading ? (
          <Spin style={{ margin: 24 }} />
        ) : !data?.length ? (
          <Empty description={t('rules.empty')} />
        ) : (
          <>
            <RuleList title={t('rules.active')} rules={active} onMove={(from, to) => move(active, from, to)} />
            {disabled.length > 0 && (
              <RuleList title={t('rules.disabled')} rules={disabled} onMove={(from, to) => move(disabled, from, to)} muted />
            )}
          </>
        )}
      </Card>
    </Page>
  )
}

const quote = (v: string) => `"${v.replaceAll('"', '""')}"`

/** CSV с BOM: Excel тогда правильно открывает кириллицу. */
function download(name: string, header: string, rows: string[]) {
  const csv = [header, ...rows].join('\r\n')
  const url = URL.createObjectURL(new Blob(['﻿', csv], { type: 'text/csv;charset=utf-8' }))
  const a = document.createElement('a')
  a.href = url
  a.download = name
  a.click()
  setTimeout(() => URL.revokeObjectURL(url), 1000)
}

function RuleList({
  title,
  rules,
  onMove,
  muted,
}: {
  title: string
  rules: RuleSummary[]
  onMove: (from: string, to: string) => void
  muted?: boolean
}) {
  const { t } = useTranslation()
  const navigate = useNavigate()
  const [dragged, setDragged] = useState<string | null>(null)
  const [over, setOver] = useState<string | null>(null)

  return (
    <section style={{ marginBottom: 24 }}>
      <Typography.Title level={4} style={{ fontWeight: 400, marginTop: 0 }}>
        {title}
      </Typography.Title>
      {rules.length === 0 && <Typography.Text type="secondary">{t('rules.noneActive')}</Typography.Text>}
      {rules.map((rule) => (
        <div
          key={rule.id}
          className={`rule-row${muted ? ' muted' : ''}${over === rule.id && dragged !== rule.id ? ' drop-target' : ''}`}
          draggable
          onDragStart={(e) => {
            setDragged(rule.id)
            e.dataTransfer.effectAllowed = 'move'
          }}
          onDragOver={(e) => {
            e.preventDefault()
            setOver(rule.id)
          }}
          onDragLeave={() => setOver((v) => (v === rule.id ? null : v))}
          onDrop={(e) => {
            e.preventDefault()
            if (dragged && dragged !== rule.id) onMove(dragged, rule.id)
            setDragged(null)
            setOver(null)
          }}
          onDragEnd={() => {
            setDragged(null)
            setOver(null)
          }}
        >
          <span className="rule-handle" title={t('rules.dragHint')}>
            <HolderOutlined />
          </span>
          <button type="button" className="rule-name" onClick={() => navigate(`/automation/rules/${rule.id}`)}>
            {rule.name}
          </button>
          {rule.isManual && <Tag style={{ marginInlineEnd: 8 }}>{t('rules.manualTag')}</Tag>}
          <span className="rule-updated">{formatDateTime(rule.updatedAt)}</span>
        </div>
      ))}
    </section>
  )
}
