import { api } from './client'
import type { TicketStatus } from './tickets'

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
}
