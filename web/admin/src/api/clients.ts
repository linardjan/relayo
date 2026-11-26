import { api } from './client'
import type { Channel, TicketStatus } from './tickets'

export interface ClientSummary {
  id: string
  label: string
  siteId: string
  siteCode: string
  externalClientId: string | null
  locale: string | null
  isVerified: boolean
  ticketCount: number
  openTicketCount: number
  lastActivityAt: string | null
  createdAt: string
  displayName: string | null
  position: string | null
  /** Заметка для сотрудников: клиент её не видит. */
  note: string | null
  isVip: boolean
  isSpammer: boolean
  /** Временная ссылка на картинку (закрытый бакет). */
  avatarUrl: string | null
}

/** Карточка клиента, которую заполняют сотрудники. Пустая строка — «не указано». */
export interface ClientProfileInput {
  name: string | null
  locale: string | null
  position: string | null
  note: string | null
  isVip: boolean
  isSpammer: boolean
}

export interface ClientDetails {
  client: ClientSummary
  identities: { id: string; type: 'Visitor' | 'Telegram' | 'WhatsApp'; displayName: string | null; isVerified: boolean; createdAt: string; lastSeenAt: string }[]
  tickets: {
    id: string
    number: number
    status: TicketStatus
    assigneeName: string | null
    createdAt: string
    closedAt: string | null
    lastMessageAt: string
    lastMessagePreview: string
    chats: { id: string; isVerified: boolean; createdAt: string; lastMessageAt: string; messageCount: number }[]
    /** Тема (письма или перенесённого из Usedesk тикета). */
    subject: string | null
    /** Канал, из которого клиент писал последним. */
    channel: Channel
  }[]
}

export interface ClientQuery {
  search: string
  siteId?: string
  anonymous: boolean
  page: number
  pageSize: number
}

export const clientsApi = {
  search: (q: ClientQuery) => {
    const params = new URLSearchParams({ page: String(q.page), pageSize: String(q.pageSize), anonymous: String(q.anonymous) })
    if (q.search) params.set('search', q.search)
    if (q.siteId) params.set('siteId', q.siteId)
    return api<{ items: ClientSummary[]; total: number }>(`/api/clients?${params}`)
  },
  get: (id: string) => api<ClientDetails>(`/api/clients/${id}`),
  /** Сайты для выбора при создании клиента — доступны всем сотрудникам. */
  sites: () => api<{ id: string; code: string; name: string }[]>('/api/clients/sites'),
  create: (siteId: string, externalClientId: string | null, profile: ClientProfileInput) =>
    api<ClientDetails>('/api/clients', json('POST', { siteId, externalClientId, profile })),
  update: (id: string, profile: ClientProfileInput) => api<ClientDetails>(`/api/clients/${id}`, json('PUT', profile)),
  setAvatar: (id: string, file: File) => {
    const form = new FormData()
    form.append('file', file)
    return api<ClientDetails>(`/api/clients/${id}/avatar`, { method: 'POST', body: form })
  },
  removeAvatar: (id: string) => api<ClientDetails>(`/api/clients/${id}/avatar`, json('DELETE')),
  remove: (id: string) => api<void>(`/api/clients/${id}`, json('DELETE')),
}

const json = (method: string, body?: unknown): RequestInit => ({ method, body: body === undefined ? undefined : JSON.stringify(body) })
