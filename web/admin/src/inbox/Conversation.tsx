import { Alert, App as AntApp, Button, Empty, Input, Popover, Tabs, Tooltip } from 'antd'
import { CheckCircleOutlined, PaperClipOutlined, RobotOutlined, SmileOutlined, SnippetsOutlined, WarningOutlined } from '@ant-design/icons'
import { Fragment, useEffect, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { ApiError } from '../api/client'
import { ticketsApi, type Channel, type ChatView, type OperatorMessage, type TicketDetails } from '../api/tickets'
import { useAuth } from '../auth/AuthContext'
import { ChannelIcon, PersonAvatar, formatTime, useDayLabel, useDuration, useNow } from './visuals'

/** Переписка тикета: чаты (если их несколько), сообщения и поле ответа. */
export function Conversation({ details, onAliasRequired }: { details: TicketDetails; onAliasRequired: () => void }) {
  const { t } = useTranslation()
  const { message } = AntApp.useApp()
  const { ticket, client, chats } = details
  const [activeChat, setActiveChat] = useState<string>()
  const chatId = activeChat ?? chats[chats.length - 1]?.id
  const chat = chats.find((c) => c.id === chatId)
  const isClosed = ticket.status === 'Closed'

  return (
    <div style={{ display: 'flex', flexDirection: 'column', height: '100%', minWidth: 0 }}>
      {chats.length > 1 && (
        <Tabs
          size="small"
          activeKey={chatId}
          onChange={setActiveChat}
          style={{ padding: '0 16px', background: '#fff' }}
          tabBarStyle={{ marginBottom: 0 }}
          items={chats.map((c, i) => ({
            key: c.id,
            label: (
              <span>
                {c.isVerified ? <CheckCircleOutlined style={{ color: '#52c41a' }} /> : <WarningOutlined style={{ color: '#faad14' }} />}{' '}
                {t('ticket.chat', { n: i + 1 })}
              </span>
            ),
          }))}
        />
      )}
      {chat && !chat.isVerified && client.externalClientId && (
        <Alert type="warning" showIcon banner title={t('ticket.unverifiedChat')} />
      )}

      <Messages chat={chat} clientLabel={client.label} clientSeed={client.id} awaitingReply={!isClosed} />

      {chat && !isClosed && (
        <Composer
          replyChannel={chat.replyChannel}
          onSend={async (text) => {
            try {
              await ticketsApi.reply(ticket.id, chat.id, text)
              return true
            } catch (e) {
              if (e instanceof ApiError && e.code === 'alias_required') onAliasRequired()
              else message.error(t('ticket.sendFailed'))
              return false
            }
          }}
        />
      )}
      {isClosed && <Alert type="info" showIcon style={{ margin: 16 }} title={t('ticket.closedHelp')} />}
    </div>
  )
}

function Messages({ chat, clientLabel, clientSeed, awaitingReply }: { chat?: ChatView; clientLabel: string; clientSeed: string; awaitingReply: boolean }) {
  const { t } = useTranslation()
  const ref = useRef<HTMLDivElement>(null)
  const dayLabel = useDayLabel()
  const duration = useDuration()
  const now = useNow()
  useEffect(() => {
    ref.current?.scrollTo({ top: ref.current.scrollHeight })
  }, [chat?.id, chat?.messages.length])

  if (!chat) return <Empty style={{ flex: 1, paddingTop: 96 }} />

  // Клиент ждёт ответа, если последнее сообщение — от него. Автоответ правила ответом человека не считается.
  const last = [...chat.messages].reverse().find((m) => m.author !== 'System' && !m.automated)
  const waitingSince = awaitingReply && last?.author === 'Client' ? last.createdAt : null

  return (
    <div ref={ref} className="conversation">
      {waitingSince && (
        <div className="awaiting-reply">
          {t('ticket.awaitingReply')}
          <span className="awaiting-line" />
          <span style={{ color: '#ff4d4f' }}>{duration(waitingSince, now)}</span>
        </div>
      )}
      {chat.messages.map((m, i) => {
        const prev = chat.messages[i - 1]
        const newDay = !prev || new Date(prev.createdAt).toDateString() !== new Date(m.createdAt).toDateString()
        return (
          <Fragment key={m.id}>
            {newDay && (
              <div className="day-separator">
                <span>{dayLabel(m.createdAt)}</span>
              </div>
            )}
            <MessageItem message={m} clientLabel={clientLabel} clientSeed={clientSeed} />
          </Fragment>
        )
      })}
    </div>
  )
}

function MessageItem({ message: m, clientLabel, clientSeed }: { message: OperatorMessage; clientLabel: string; clientSeed: string }) {
  const { t } = useTranslation()
  if (m.author === 'System') {
    return (
      <div className="msg-system">
        {m.channel !== 'Widget' && <ChannelIcon channel={m.channel} />} {m.text} · {formatTime(m.createdAt)}
      </div>
    )
  }

  const fromClient = m.author === 'Client'
  const name = fromClient ? clientLabel : (m.authorName ?? t('ticket.operator'))
  return (
    <div className={`msg ${fromClient ? 'from-client' : 'from-operator'}${m.automated ? ' automated' : ''}`}>
      {m.automated ? (
        <span className="msg-robot">
          <RobotOutlined />
        </span>
      ) : (
        <PersonAvatar name={name} seed={fromClient ? clientSeed : name} size={32} />
      )}
      <div className="msg-card">
        <div className="msg-head">
          <span className="msg-name">{name}</span>
          <span className="msg-time">{formatTime(m.createdAt)}</span>
          {m.channel !== 'Widget' && <ChannelIcon channel={m.channel} />}
        </div>
        <div className="msg-text">{m.text}</div>
      </div>
    </div>
  )
}

const emojis = ['🙂', '😊', '😉', '🙏', '👍', '👌', '👋', '🤝', '✅', '❗', '⏳', '🎉', '😔', '🤔', '💡', '📎', '📞', '✉️', '❤️', '🔥']

function Composer({ onSend, replyChannel }: { onSend: (text: string) => Promise<boolean>; replyChannel: Channel }) {
  const { t } = useTranslation()
  const { user } = useAuth()
  const [text, setText] = useState('')
  const [sending, setSending] = useState(false)
  const [emojiOpen, setEmojiOpen] = useState(false)

  const send = async () => {
    if (!text.trim() || sending) return
    setSending(true)
    if (await onSend(text)) setText('')
    setSending(false)
  }

  return (
    <div className="composer">
      <div className="composer-top">
        <Tabs
          size="small"
          activeKey="message"
          tabBarStyle={{ marginBottom: 0 }}
          items={[
            { key: 'message', label: t('ticket.tabMessage') },
            { key: 'comment', label: <Tooltip title={t('common.comingSoon')}>{t('ticket.tabComment')}</Tooltip>, disabled: true },
          ]}
        />
        {/* Куда уйдёт ответ и от чьего имени его увидит клиент. */}
        <div className="composer-channel">
          <ChannelIcon channel={replyChannel} />
          {t('ticket.replyVia', { channel: t(`inbox.channel.${replyChannel}`) })}
          {user?.alias && <span className="composer-alias">· {t('ticket.asAlias', { alias: user.alias })}</span>}
        </div>
      </div>
      <Input.TextArea
        variant="borderless"
        value={text}
        onChange={(e) => setText(e.target.value)}
        autoSize={{ minRows: 2, maxRows: 8 }}
        placeholder={t('ticket.replyPlaceholder')}
        maxLength={10000}
        onPressEnter={(e) => {
          if (!e.shiftKey) {
            e.preventDefault()
            void send()
          }
        }}
      />
      <div className="composer-bottom">
        <div>
          <Tooltip title={`${t('ticket.attach')} · ${t('common.comingSoon')}`}>
            <Button type="text" icon={<PaperClipOutlined />} disabled />
          </Tooltip>
          <Tooltip title={`${t('ticket.templates')} · ${t('common.comingSoon')}`}>
            <Button type="text" icon={<SnippetsOutlined />} disabled />
          </Tooltip>
          <Popover
            open={emojiOpen}
            onOpenChange={setEmojiOpen}
            trigger="click"
            content={
              <div style={{ display: 'grid', gridTemplateColumns: 'repeat(5, 32px)', gap: 4 }}>
                {emojis.map((e) => (
                  <Button
                    key={e}
                    type="text"
                    style={{ fontSize: 18, padding: 0 }}
                    onClick={() => {
                      setText((v) => v + e)
                      setEmojiOpen(false)
                    }}
                  >
                    {e}
                  </Button>
                ))}
              </div>
            }
          >
            <Button type="text" icon={<SmileOutlined />} aria-label={t('ticket.emoji')} />
          </Popover>
        </div>
        <Button type="primary" onClick={send} loading={sending} disabled={!text.trim()}>
          {t('ticket.send')}
        </Button>
      </div>
    </div>
  )
}
