import { api } from './client'
import type { IdentificationMode } from './settings'

/** Подключение к базе Usedesk (MySQL). Пароль не отдаётся — только признак, что он сохранён. */
export interface UsedeskConnection {
  host: string
  port: number
  database: string
  username: string
  hasPassword: boolean
  companyId: number
  lastCheckAt: string | null
  lastCheckOk: boolean | null
  lastCheckMessage: string | null
}

export interface UsedeskConnectionInput {
  host: string
  port: number
  database: string
  username: string
  /** Пусто — оставить сохранённый. */
  password?: string
  companyId: number
}

export type UsedeskImportKind = 'Groups' | 'Users' | 'Email' | 'Telegram' | 'WhatsApp' | 'Widgets'

export interface UsedeskPreviewRow {
  id: number
  name: string
  details: string
  deleted: boolean
  /** Уже перенесено: id в Relayo. */
  importedId: string | null
  /** Почему перенести нельзя. */
  problem: string | null
  notes: string[]
}

export interface UsedeskPreview {
  groups: UsedeskPreviewRow[]
  users: UsedeskPreviewRow[]
  email: UsedeskPreviewRow[]
  telegram: UsedeskPreviewRow[]
  whatsApp: UsedeskPreviewRow[]
  widgets: UsedeskPreviewRow[]
  warnings: string[]
}

export interface UsedeskImportResult {
  imported: number
  skipped: { id: number; name: string; reason: string }[]
  /** Скольким перенесённым сотрудникам добавлены группы. */
  groupsAssigned: number
}

const json = (method: string, body?: unknown): RequestInit => ({ method, body: body === undefined ? undefined : JSON.stringify(body) })

export const usedeskApi = {
  /** null — подключение ещё не задано (TanStack Query не принимает undefined). */
  connection: async () => (await api<UsedeskConnection | undefined>('/api/settings/usedesk/connection')) ?? null,
  saveConnection: (input: UsedeskConnectionInput) => api<UsedeskConnection>('/api/settings/usedesk/connection', json('PUT', input)),
  check: () => api<UsedeskConnection>('/api/settings/usedesk/connection/check', json('POST')),
  forget: () => api<void>('/api/settings/usedesk/connection', json('DELETE')),
  preview: () => api<UsedeskPreview>('/api/settings/usedesk/preview'),
  import: (kind: UsedeskImportKind, ids: number[], siteId: string | undefined, identificationMode?: IdentificationMode) =>
    api<UsedeskImportResult>('/api/settings/usedesk/import', json('POST', { kind, ids, siteId, identificationMode })),
}
