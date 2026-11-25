import { useEffect, useRef } from 'react'
import { HubConnectionBuilder, LogLevel } from '@microsoft/signalr'
import { useQueryClient } from '@tanstack/react-query'
import { getAccessToken, refreshSession } from '../api/client'
import type { OperatorMessage, TicketDetails, TicketSummary } from '../api/tickets'

/** Тикет изменился: прежнее состояние из списка (если было) и новое. */
export type TicketChangedHandler = (previous: TicketSummary | undefined, next: TicketSummary) => void

/**
 * Realtime для операторов: новые сообщения и изменения тикетов обновляют кэш запросов.
 * Подключение живёт, пока открыта админка, и переподключается без ограничения попыток.
 */
export function useOperatorHub(onTicketChanged?: TicketChangedHandler) {
  const queryClient = useQueryClient()
  const handler = useRef(onTicketChanged)
  handler.current = onTicketChanged

  useEffect(() => {
    const hub = new HubConnectionBuilder()
      .withUrl('/hubs/operators', {
        accessTokenFactory: async () => getAccessToken() ?? (await refreshSession())?.accessToken ?? '',
      })
      // Стандартная политика сдаётся после 4 попыток, и админка молча перестаёт получать события.
      .withAutomaticReconnect({ nextRetryDelayInMilliseconds: (ctx) => Math.min(30_000, [0, 2_000, 5_000, 10_000][ctx.previousRetryCount] ?? 30_000) })
      .configureLogging(LogLevel.Warning)
      .build()

    hub.on('ticketChanged', (ticket: TicketSummary) => {
      const previous = queryClient.getQueryData<TicketSummary[]>(['tickets', false])?.find((t) => t.id === ticket.id)
      handler.current?.(previous, ticket)
      void queryClient.invalidateQueries({ queryKey: ['tickets'] })
      queryClient.setQueryData<TicketDetails>(['ticket', ticket.id], (old) => (old ? { ...old, ticket } : old))
    })

    hub.on('messageCreated', ({ ticketId, message }: { ticketId: string; message: OperatorMessage }) => {
      const detail = queryClient.getQueryData<TicketDetails>(['ticket', ticketId])
      if (!detail) return
      const chat = detail.chats.find((c) => c.id === message.chatId)
      if (!chat) {
        // Новый чат в тикете: проще перечитать тикет целиком.
        void queryClient.invalidateQueries({ queryKey: ['ticket', ticketId] })
        return
      }
      if (chat.messages.some((m) => m.id === message.id)) return
      queryClient.setQueryData<TicketDetails>(['ticket', ticketId], {
        ...detail,
        chats: detail.chats.map((c) =>
          c.id === chat.id
            ? {
                ...c,
                messages: [...c.messages, message],
                // Ответ уходит в канал, из которого клиент написал последним.
                replyChannel: message.author === 'Client' ? message.channel : c.replyChannel,
              }
            : c,
        ),
      })
    })

    // После переподключения могли пропустить события.
    hub.onreconnected(() => void queryClient.invalidateQueries())
    // Автоматическое переподключение работает только после первого успешного подключения,
    // поэтому первое подключение повторяем сами (например, пока перезапускается сервис).
    let stopped = false
    let retry: ReturnType<typeof setTimeout> | undefined
    const start = () =>
      hub.start().then(
        // События между загрузкой списка и подключением могли пройти мимо: перечитываем тикеты.
        () => void queryClient.invalidateQueries({ predicate: (q) => q.queryKey[0] === 'tickets' || q.queryKey[0] === 'ticket' }),
        (error) => {
          if (stopped) return
          console.warn('Realtime connection failed, retrying in 5 s', error)
          retry = setTimeout(start, 5_000)
        },
      )
    void start()
    return () => {
      stopped = true
      clearTimeout(retry)
      void hub.stop()
    }
  }, [queryClient])
}
