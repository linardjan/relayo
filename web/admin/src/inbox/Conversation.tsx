import { Alert, App as AntApp, Button, Empty, Image, Input, Popover, Tabs, Tag, Tooltip } from 'antd'
import { CheckCircleOutlined, EyeInvisibleOutlined, FileOutlined, PaperClipOutlined, RobotOutlined, SmileOutlined, SnippetsOutlined, WarningOutlined } from '@ant-design/icons'
import { Fragment, useEffect, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { ApiError } from '../api/client'
import { ticketsApi, type Attachment, type Channel, type ChatView, type OperatorMessage, type TicketDetails } from '../api/tickets'
import { errorText } from '../settings/errors'
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
          onSend={async (mode, text, files) => {
            try {
              if (mode === 'comment') await ticketsApi.note(ticket.id, chat.id, text, files)
              else await ticketsApi.reply(ticket.id, chat.id, text, files)
              return true
            } catch (e) {
              if (e instanceof ApiError && e.code === 'alias_required') onAliasRequired()
              // Причину (размер, тип файла, недоступно хранилище) сервер сообщает текстом.
              else message.error(errorText(e, t(mode === 'comment' ? 'ticket.noteFailed' : 'ticket.sendFailed')))
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

  // Клиент ждёт ответа, если последнее сообщение — от него. Автоответ правила и заметка ответом не считаются.
  const last = [...chat.messages].reverse().find((m) => m.author !== 'System' && !m.automated && m.visibility !== 'Internal')
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
  const internal = m.visibility === 'Internal'
  const name = fromClient ? clientLabel : (m.authorName ?? t('ticket.operator'))
  return (
    <div className={`msg ${fromClient ? 'from-client' : 'from-operator'}${m.automated ? ' automated' : ''}${internal ? ' internal' : ''}`}>
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
          {internal ? (
            <span className="msg-note-label">
              <EyeInvisibleOutlined /> {t('ticket.note')}
            </span>
          ) : (
            m.channel !== 'Widget' && <ChannelIcon channel={m.channel} />
          )}
        </div>
        <MessageAttachments items={m.attachments} />
        {m.text && <div className="msg-text">{m.text}</div>}
      </div>
    </div>
  )
}

/** Картинки — превью с увеличением, остальное — ссылки на скачивание с размером. */
function MessageAttachments({ items }: { items?: Attachment[] | null }) {
  if (!items?.length) return null
  const images = items.filter((a) => a.url && a.contentType.startsWith('image/'))
  const files = items.filter((a) => !images.includes(a))
  return (
    <div className="msg-attachments">
      {images.length > 0 && (
        <Image.PreviewGroup>
          {images.map((a) => (
            <Image key={a.id} src={a.url!} alt={a.fileName} width={140} height={100} style={{ objectFit: 'cover', borderRadius: 6 }} />
          ))}
        </Image.PreviewGroup>
      )}
      {files.map((a) => (
        <a key={a.id} className="msg-file" href={a.url ?? undefined} target="_blank" rel="noreferrer" download={a.fileName}>
          <FileOutlined /> <span className="msg-file-name">{a.fileName}</span> <span className="msg-file-size">{formatSize(a.size)}</span>
        </a>
      ))}
    </div>
  )
}

function formatSize(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 * 1024) return `${Math.round(bytes / 1024)} KB`
  return `${(bytes / 1024 / 1024).toFixed(1)} MB`
}

const emojis = ['🙂', '😊', '😉', '🙏', '👍', '👌', '👋', '🤝', '✅', '❗', '⏳', '🎉', '😔', '🤔', '💡', '📎', '📞', '✉️', '❤️', '🔥']

/** Не больше файлов в одном сообщении: столько же принимает сервер. */
const MAX_FILES = 10

type ComposerMode = 'message' | 'comment'

function Composer({
  onSend,
  replyChannel,
}: {
  onSend: (mode: ComposerMode, text: string, files: File[]) => Promise<boolean>
  replyChannel: Channel
}) {
  const { t } = useTranslation()
  const { user } = useAuth()
  const [mode, setMode] = useState<ComposerMode>('message')
  const isNote = mode === 'comment'
  const [text, setText] = useState('')
  const [files, setFiles] = useState<File[]>([])
  const [dragging, setDragging] = useState(false)
  const [sending, setSending] = useState(false)
  const [emojiOpen, setEmojiOpen] = useState(false)
  const fileInput = useRef<HTMLInputElement>(null)
  const canSend = !!text.trim() || files.length > 0

  const addFiles = (list: FileList | null) => {
    if (list) setFiles((current) => [...current, ...Array.from(list)].slice(0, MAX_FILES))
  }

  const send = async () => {
    if (!canSend || sending) return
    setSending(true)
    if (await onSend(mode, text, files)) {
      setText('')
      setFiles([])
    }
    setSending(false)
  }

  return (
    <div
      className={`composer${dragging ? ' dragging' : ''}${isNote ? ' note' : ''}`}
      onDragOver={(e) => {
        if (!e.dataTransfer.types.includes('Files')) return
        e.preventDefault()
        setDragging(true)
      }}
      onDragLeave={(e) => {
        if (e.currentTarget === e.target) setDragging(false)
      }}
      onDrop={(e) => {
        e.preventDefault()
        setDragging(false)
        addFiles(e.dataTransfer.files)
      }}
    >
      {dragging && <div className="composer-drop">{t('ticket.dropFiles')}</div>}
      <div className="composer-top">
        <Tabs
          size="small"
          activeKey={mode}
          onChange={(key) => setMode(key as ComposerMode)}
          tabBarStyle={{ marginBottom: 0 }}
          items={[
            { key: 'message', label: t('ticket.tabMessage') },
            { key: 'comment', label: t('ticket.tabComment') },
          ]}
        />
        {/* Куда уйдёт ответ и от чьего имени его увидит клиент; заметку клиент не увидит вовсе. */}
        {isNote ? (
          <div className="composer-channel">
            <EyeInvisibleOutlined />
            {t('ticket.noteVisibility')}
          </div>
        ) : (
          <div className="composer-channel">
            <ChannelIcon channel={replyChannel} />
            {t('ticket.replyVia', { channel: t(`inbox.channel.${replyChannel}`) })}
            {user?.alias && <span className="composer-alias">· {t('ticket.asAlias', { alias: user.alias })}</span>}
          </div>
        )}
      </div>
      <Input.TextArea
        variant="borderless"
        value={text}
        onChange={(e) => setText(e.target.value)}
        autoSize={{ minRows: 2, maxRows: 8 }}
        placeholder={t(isNote ? 'ticket.notePlaceholder' : 'ticket.replyPlaceholder')}
        maxLength={10000}
        onPressEnter={(e) => {
          if (!e.shiftKey) {
            e.preventDefault()
            void send()
          }
        }}
      />
      {files.length > 0 && (
        <div className="composer-files">
          {files.map((f, i) => (
            <Tag key={`${f.name}-${i}`} icon={<PaperClipOutlined />} closable onClose={() => setFiles(files.filter((_, j) => j !== i))}>
              {f.name} · {formatSize(f.size)}
            </Tag>
          ))}
        </div>
      )}
      <div className="composer-bottom">
        <div>
          <Tooltip title={t('ticket.attachHelp')}>
            <Button type="text" icon={<PaperClipOutlined />} aria-label={t('ticket.attach')} onClick={() => fileInput.current?.click()} />
          </Tooltip>
          <input
            ref={fileInput}
            type="file"
            multiple
            style={{ display: 'none' }}
            onChange={(e) => {
              addFiles(e.target.files)
              e.target.value = ''
            }}
          />
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
        <Button type="primary" onClick={send} loading={sending} disabled={!canSend}>
          {t(isNote ? 'ticket.addNote' : 'ticket.send')}
        </Button>
      </div>
    </div>
  )
}
