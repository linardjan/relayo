import { api, type User } from './client'

export type TicketStatus = 'New' | 'InProgress' | 'WaitingClient' | 'Closed'
export type MessageAuthor = 'Client' | 'Operator' | 'System'
export type Channel = 'Widget' | 'Telegram' | 'WhatsApp'

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
}

export interface ChatView {
  id: string
  isVerified: boolean
  createdAt: string
  /** Куда уйдёт ответ: канал, из которого клиент написал последним. */
  replyChannel: Channel
  messages: OperatorMessage[]
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

export const ticketsApi = {
  list: (closed: boolean) => api<TicketSummary[]>(`/api/tickets?closed=${closed}`),
  get: (id: string) => api<TicketDetails>(`/api/tickets/${id}`),
  reply: (id: string, chatId: string, text: string) =>
    api<OperatorMessage>(`/api/tickets/${id}/messages`, { method: 'POST', body: JSON.stringify({ chatId, text }) }),
  assign: (id: string) => api<TicketSummary>(`/api/tickets/${id}/assign`, { method: 'POST' }),
  close: (id: string) => api<TicketSummary>(`/api/tickets/${id}/close`, { method: 'POST' }),
  setAlias: (alias: string) => api<User>('/api/me/alias', { method: 'PUT', body: JSON.stringify({ alias }) }),
}
