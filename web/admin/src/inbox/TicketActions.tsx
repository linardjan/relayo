import { Button, Dropdown, Space, Tag, Tooltip } from 'antd'
import { DownOutlined, UserOutlined } from '@ant-design/icons'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { useTranslation } from 'react-i18next'
import { ticketsApi, type TicketDetails } from '../api/tickets'
import { useAuth } from '../auth/AuthContext'
import { statusColor } from './status'

/** Действия с открытым тикетом в верхней панели: взять, исполнитель, статус, закрыть. */
export function TicketActions({ details }: { details: TicketDetails }) {
  const { t } = useTranslation()
  const { user } = useAuth()
  const queryClient = useQueryClient()
  const { ticket } = details
  const isClosed = ticket.status === 'Closed'
  const mine = ticket.assigneeId === user?.id

  const update = (summary: TicketDetails['ticket']) => {
    queryClient.setQueryData<TicketDetails>(['ticket', ticket.id], (old) => (old ? { ...old, ticket: summary } : old))
    void queryClient.invalidateQueries({ queryKey: ['tickets'] })
  }
  const assign = useMutation({ mutationFn: () => ticketsApi.assign(ticket.id), onSuccess: update })
  const close = useMutation({ mutationFn: () => ticketsApi.close(ticket.id), onSuccess: update })

  return (
    <div className="inbox-actions">
      <Space size={12}>
        {!isClosed && !mine && (
          <Button onClick={() => assign.mutate()} loading={assign.isPending} className="soft-button">
            {t('ticket.take')}
          </Button>
        )}
        <Dropdown
          disabled={isClosed}
          trigger={['click']}
          menu={{
            items: [
              { key: 'me', label: t('ticket.assignToMe'), disabled: mine },
              { type: 'divider' },
              { key: 'others', label: <Tooltip title={t('common.comingSoon')}>{t('ticket.assignToOther')}</Tooltip>, disabled: true },
            ],
            onClick: ({ key }) => key === 'me' && assign.mutate(),
          }}
        >
          <Button type="text" icon={<UserOutlined />}>
            {ticket.assigneeName ?? t('ticket.unassigned')} <DownOutlined style={{ fontSize: 10 }} />
          </Button>
        </Dropdown>
        <Tag color={statusColor[ticket.status]} style={{ marginInlineEnd: 0 }}>
          {t(`ticket.status.${ticket.status}`)}
        </Tag>
        <span style={{ color: '#8c8c8c' }}>#{ticket.number}</span>
      </Space>
      {!isClosed && (
        <Button type="primary" onClick={() => close.mutate()} loading={close.isPending}>
          {t('ticket.close')}
        </Button>
      )}
    </div>
  )
}
