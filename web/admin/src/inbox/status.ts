import type { TicketStatus } from '../api/tickets'

export const statusColor: Record<TicketStatus, string> = {
  New: 'blue',
  InProgress: 'gold',
  WaitingClient: 'purple',
  Closed: 'default',
}
