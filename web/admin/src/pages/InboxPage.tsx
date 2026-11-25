import { Button, Empty, Spin, Tooltip } from 'antd'
import { LeftOutlined, RightOutlined, SoundOutlined, MutedOutlined } from '@ant-design/icons'
import { useQuery } from '@tanstack/react-query'
import { useEffect, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { useNavigate, useParams } from 'react-router'
import { ticketsApi } from '../api/tickets'
import { AliasModal } from '../inbox/AliasModal'
import { ClientPanel } from '../inbox/ClientPanel'
import { Conversation } from '../inbox/Conversation'
import { TicketActions } from '../inbox/TicketActions'
import { useInboxState } from '../inbox/InboxState'
import { TicketList } from '../inbox/TicketList'

const PANEL_KEY = 'relayo:client-panel-hidden'

/**
 * Рабочее место оператора в три колонки: список тикетов, переписка, информация о клиенте.
 * Сверху общая панель: заголовок раздела и действия с выбранным тикетом.
 */
export function InboxPage() {
  const { t } = useTranslation()
  const { ticketId } = useParams()
  const navigate = useNavigate()
  const [aliasOpen, setAliasOpen] = useState(false)
  const [panelHidden, setPanelHidden] = useState(() => localStorage.getItem(PANEL_KEY) === '1')
  const { markSeen, muted, setMuted } = useInboxState()

  const ticket = useQuery({
    queryKey: ['ticket', ticketId],
    queryFn: () => ticketsApi.get(ticketId!),
    enabled: !!ticketId,
    // Ссылки на вложения временные: пока тикет открыт, обновляем их заранее.
    refetchInterval: 10 * 60_000,
  })
  const details = ticket.data

  // Открытый тикет просмотрен, в том числе новые сообщения, пришедшие пока он открыт.
  useEffect(() => {
    if (details) markSeen(details.ticket.id, details.ticket.lastMessageAt)
  }, [details, markSeen])

  const togglePanel = () => {
    localStorage.setItem(PANEL_KEY, panelHidden ? '0' : '1')
    setPanelHidden(!panelHidden)
  }

  return (
    <div className="inbox">
      <div className="inbox-topbar">
        <div className="inbox-title">
          {t('inbox.title')}
          <Tooltip title={muted ? t('inbox.soundOn') : t('inbox.soundOff')}>
            <Button
              type="text"
              size="small"
              icon={muted ? <MutedOutlined /> : <SoundOutlined />}
              onClick={() => setMuted(!muted)}
              aria-label={muted ? t('inbox.soundOn') : t('inbox.soundOff')}
              style={{ marginLeft: 8, color: muted ? '#bfbfbf' : '#8c8c8c' }}
            />
          </Tooltip>
        </div>
        {details && <TicketActions details={details} />}
      </div>
      <div className="inbox-body">
        <div className="inbox-list">
          <TicketList selectedId={ticketId} onSelect={(id) => navigate(`/tickets/${id}`)} />
        </div>
        <div className="inbox-center">
          {!ticketId ? (
            <Empty style={{ marginTop: 96 }} description={t('inbox.selectTicket')} />
          ) : ticket.isLoading ? (
            <Spin style={{ display: 'block', margin: 48 }} />
          ) : !details ? (
            <Empty style={{ marginTop: 96 }} description={t('ticket.loadFailed')} />
          ) : (
            <Conversation key={details.ticket.id} details={details} onAliasRequired={() => setAliasOpen(true)} />
          )}
        </div>
        {details && (
          <div className={`inbox-panel${panelHidden ? ' hidden' : ''}`}>
            <Tooltip title={panelHidden ? t('inbox.panel.show') : t('inbox.panel.hide')} placement="left">
              <Button
                className="inbox-panel-toggle"
                size="small"
                icon={panelHidden ? <LeftOutlined /> : <RightOutlined />}
                onClick={togglePanel}
                aria-label={panelHidden ? t('inbox.panel.show') : t('inbox.panel.hide')}
              />
            </Tooltip>
            {!panelHidden && (
              <div className="inbox-panel-scroll">
                <ClientPanel details={details} />
              </div>
            )}
          </div>
        )}
      </div>
      <AliasModal open={aliasOpen} onClose={() => setAliasOpen(false)} />
    </div>
  )
}
