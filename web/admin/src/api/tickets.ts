import { api, type User } from './client'

export type TicketStatus = 'New' | 'InProgress' | 'WaitingClient' | 'Closed'
export type MessageAuthor = 'Client' | 'Operator' | 'System'
export type Channel = 'Widget' | 'Telegram' | 'WhatsApp'
export type TicketSource = 'Relayo' | 'Usedesk'
/** Internal — заметка: её видят только сотрудники. */
export type MessageVisibility = 'Public' | 'Internal'

export interface TicketSummary {
  id: string
  number: number
  status: TicketStatus
  clientId: string
  clientLabel: string
  clientVerified: boolean
  siteCode: string
  assigneeId: string | null
  assigneeName: string | null
  createdAt: string
  lastMessageAt: string
  lastMessagePreview: string
  /** Канал, из которого клиент написал последним. */
  lastClientChannel: Channel
  subject: string | null
  /** Usedesk — тикет перенесён из Usedesk. */
  source: TicketSource
  /** Канал в Usedesk, как он там записан (email, telephony, api…). */
  sourceChannel: string | null
  groupId: string | null
  groupName: string | null
}

export interface GroupOption {
  id: string
  name: string
}

export interface OperatorMessage {
  id: string
  chatId: string
  author: MessageAuthor
  authorName: string | null
  text: string
  createdAt: string
  channel: Channel
  /** Автоответ правила, а не ответ сотрудника. */
  automated?: boolean
  attachments?: Attachment[] | null
  visibility: MessageVisibility
}

export interface Attachment {
  id: string
  fileName: string
  contentType: string
  size: number
  /** Временная ссылка на скачивание (живёт S3_PRESIGNED_URL_MINUTES). */
  url: string | null
}

export interface ChatView {
  id: string
  isVerified: boolean
  createdAt: string
  /** Куда уйдёт ответ: канал, из которого клиент написал последним. */
  replyChannel: Channel
  messages: OperatorMessage[]
  /** Страница сайта, с которой клиент начал писать в виджет. */
  pageUrl: string | null
}

export interface TicketDetails {
  ticket: TicketSummary
  client: {
    id: string
    label: string
    siteCode: string
    externalClientId: string | null
    locale: string | null
    isVerified: boolean
  }
  chats: ChatView[]
}

export interface AgentOption {
  id: string
  fullName: string
  alias: string | null
  status: 'Offline' | 'Online' | 'Break'
}

/** Фильтры раздела «Запросы». assignee: me, none или id сотрудника. */
export interface TicketFilter {
  statuses?: TicketStatus[]
  channel?: Channel
  assignee?: string
  groupId?: string
  siteId?: string
  clientId?: string
  search?: string
  createdFrom?: string
  updatedFrom?: string
}

export const ticketsApi = {
  search: (f: TicketFilter, page: number, pageSize: number) => {
    const params = new URLSearchParams({ page: String(page), pageSize: String(pageSize) })
    for (const s of f.statuses ?? []) params.append('status', s)
    for (const key of ['channel', 'assignee', 'groupId', 'siteId', 'clientId', 'search', 'createdFrom', 'updatedFrom'] as const)
      if (f[key]) params.set(key, f[key])
    return api<{ items: TicketSummary[]; total: number }>(`/api/tickets/search?${params}`)
  },
  agents: () => api<AgentOption[]>('/api/tickets/agents'),
  /** Самый старый свободный запрос; undefined — свободных нет. */
  takeNext: () => api<TicketSummary | undefined>('/api/tickets/take-next', { method: 'POST' }),
  setStatus: (id: string, status: TicketStatus) =>
    api<TicketSummary>(`/api/tickets/${id}/status`, { method: 'PUT', body: JSON.stringify({ status }) }),
  setSubject: (id: string, subject: string | null) =>
    api<TicketSummary>(`/api/tickets/${id}/subject`, { method: 'PUT', body: JSON.stringify({ subject }) }),
  /** null — вернуть в очередь. */
  setAssignee: (id: string, userId: string | null) =>
    api<TicketSummary>(`/api/tickets/${id}/assignee`, { method: 'PUT', body: JSON.stringify({ userId }) }),
  list: (closed: boolean) => api<TicketSummary[]>(`/api/tickets?closed=${closed}`),
  get: (id: string) => api<TicketDetails>(`/api/tickets/${id}`),
  reply: (id: string, chatId: string, text: string, files: File[] = []) => post(`/api/tickets/${id}/messages`, chatId, text, files),
  /** Внутренняя заметка: клиенту не уходит. */
  note: (id: string, chatId: string, text: string, files: File[] = []) => post(`/api/tickets/${id}/notes`, chatId, text, files),
  groups: () => api<GroupOption[]>('/api/tickets/groups'),
  setGroup: (id: string, groupId: string | null) =>
    api<TicketSummary>(`/api/tickets/${id}/group`, { method: 'PUT', body: JSON.stringify({ groupId }) }),
  assign: (id: string) => api<TicketSummary>(`/api/tickets/${id}/assign`, { method: 'POST' }),
  close: (id: string) => api<TicketSummary>(`/api/tickets/${id}/close`, { method: 'POST' }),
  setAlias: (alias: string) => api<User>('/api/me/alias', { method: 'PUT', body: JSON.stringify({ alias }) }),
}

/** Сообщение в тикет: JSON без файлов или multipart с файлами. */
function post(url: string, chatId: string, text: string, files: File[]) {
  if (files.length === 0) return api<OperatorMessage>(url, { method: 'POST', body: JSON.stringify({ chatId, text }) })
  const form = new FormData()
  form.append('chatId', chatId)
  form.append('text', text)
  for (const file of files) form.append('files', file, file.name)
  return api<OperatorMessage>(url, { method: 'POST', body: form })
}
