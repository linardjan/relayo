import { api, type Role } from './client'

export interface SiteKey {
  id: string
  keyId: string
  createdAt: string
  revokedAt: string | null
}

export interface Site {
  id: string
  code: string
  name: string
  allowedOrigins: string[]
  createdAt: string
  keys: SiteKey[]
  widgetCount: number
}

export type IdentificationMode = 'SignedToken' | 'Direct'
export type WidgetPosition = 'Right' | 'Left'

export type LauncherItemType = 'Chat' | 'Telegram' | 'Link'

export interface LauncherItem {
  type: LauncherItemType
  label: string
  url?: string | null
}

/** Внешний вид и поведение виджета на сайте (страница канала). */
export interface WidgetOptions {
  caption: string
  allowAttachments: boolean
  zIndex: number
  hideOnOutsideClick: boolean
  visibility: 'Always' | 'Never'
  buttonDelaySeconds: number
  buttonColor: string | null
  /** Имя файла своей картинки кнопки; меняется загрузкой, в форме его можно только убрать. */
  buttonIcon: string | null
  tooltipMode: 'Never' | 'Always' | 'Hover'
  tooltipText: string
  tooltipBackground: string
  tooltipColor: string
  tooltipTextSize: number
  sound: 'None' | 'Beep'
  operatorName: string | null
  items: LauncherItem[]
}

export interface WidgetInput {
  name: string
  identificationMode: IdentificationMode
  title: string
  greeting: string
  accentColor: string
  position: WidgetPosition
  defaultLocale: string
  telegramBotId: string | null
  options: WidgetOptions
}

export interface Widget extends WidgetInput {
  id: string
  siteId: string
  siteCode: string
  createdAt: string
  /** Ключ в имени файла widget_<ключ>.js */
  publicKey: string
  publishedVersion: number | null
  publishedAt: string | null
  updatedAt: string
  hasUnpublishedChanges: boolean
}

export interface WidgetRow {
  widget: Widget
  embedCode: string
  /** Адрес опубликованного файла в S3; null — хранилище не настроено. */
  fileUrl: string | null
  /** Адрес своей картинки кнопки для предпросмотра. */
  buttonIconUrl: string | null
}

export interface WidgetVersion {
  version: number
  publishedAt: string
  publishedBy: string
  note: string | null
}

export interface TelegramBot {
  id: string
  name: string
  username: string
  siteId: string
  siteCode: string
  isEnabled: boolean
  createdAt: string
  widgetCount: number
}

export interface TelegramBotInput {
  name: string
  siteId: string
  isEnabled: boolean
  /** Пусто при изменении — оставить прежний токен. */
  token?: string
}

export interface StaffUser {
  id: string
  login: string
  fullName: string
  alias: string | null
  role: Role
  source: 'Local' | 'ActiveDirectory'
  isBlocked: boolean
  /** Заблокирован синхронизацией: пропал из папки AD или отключён там. */
  blockedByDirectory: boolean
  createdAt: string
  email: string | null
  position: string | null
  phone: string | null
  locale: string
  status: OperatorStatus
  groupIds: string[]
  defaultGroupId: string | null
}

export type OperatorStatus = 'Offline' | 'Online' | 'Break'

export interface UserProfileInput {
  email?: string | null
  position?: string | null
  phone?: string | null
  locale?: string
  groupIds?: string[]
  defaultGroupId?: string | null
}

export interface AgentGroup {
  id: string
  name: string
  memberCount: number
}

export type LdapSecurity = 'Ldaps' | 'StartTls'
export type DirectoryProvider = 'ActiveDirectory' | 'OpenLdap'

export interface LdapSettingsInput {
  isEnabled: boolean
  host: string
  port: number
  security: LdapSecurity
  verifyCertificate: boolean
  provider: DirectoryProvider
  bindDn: string
  /** Пусто — оставить сохранённый пароль. */
  bindPassword?: string
  usersDn: string
  guidAttribute: string
  loginAttribute: string
  fullNameAttribute: string
  mailAttribute?: string | null
  phoneAttribute?: string | null
  positionAttribute?: string | null
  defaultRole: Role
  syncIntervalMinutes: number
}

export interface LdapSettings extends Omit<LdapSettingsInput, 'bindPassword'> {
  hasBindPassword: boolean
  lastCheckAt: string | null
  lastCheckOk: boolean | null
  lastCheckMessage: string | null
  lastSyncAt: string | null
  lastSyncOk: boolean | null
  lastSyncMessage: string | null
}

const json = (method: string, body?: unknown): RequestInit => ({ method, body: body === undefined ? undefined : JSON.stringify(body) })

export const settingsApi = {
  sites: () => api<Site[]>('/api/settings/sites'),
  createSite: (code: string, name: string, allowedOrigins: string[]) =>
    api<Site>('/api/settings/sites', json('POST', { code, name, allowedOrigins })),
  updateSite: (id: string, name: string, allowedOrigins: string[]) =>
    api<Site>(`/api/settings/sites/${id}`, json('PUT', { name, allowedOrigins })),
  createKey: (siteId: string) => api<{ key: SiteKey; secret: string }>(`/api/settings/sites/${siteId}/keys`, json('POST')),
  revokeKey: (siteId: string, keyId: string) => api<SiteKey>(`/api/settings/sites/${siteId}/keys/${keyId}/revoke`, json('POST')),

  widgets: () => api<WidgetRow[]>('/api/settings/widgets'),
  createWidget: (siteId: string, input: WidgetInput) => api<WidgetRow>('/api/settings/widgets', json('POST', { siteId, ...input })),
  updateWidget: (id: string, input: WidgetInput) => api<WidgetRow>(`/api/settings/widgets/${id}`, json('PUT', input)),
  publishWidget: (id: string) => api<WidgetRow>(`/api/settings/widgets/${id}/publish`, json('POST')),
  widgetVersions: (id: string) => api<WidgetVersion[]>(`/api/settings/widgets/${id}/versions`),
  rollbackWidget: (id: string, version: number) =>
    api<WidgetRow>(`/api/settings/widgets/${id}/versions/${version}/rollback`, json('POST')),
  deleteWidget: (id: string) => api<void>(`/api/settings/widgets/${id}`, json('DELETE')),
  uploadButtonIcon: (id: string, file: File) => {
    const form = new FormData()
    form.append('file', file, file.name)
    return api<WidgetRow>(`/api/settings/widgets/${id}/button-icon`, { method: 'POST', body: form })
  },

  telegramBots: () => api<TelegramBot[]>('/api/settings/telegram-bots'),
  createTelegramBot: (input: TelegramBotInput) => api<TelegramBot>('/api/settings/telegram-bots', json('POST', input)),
  updateTelegramBot: (id: string, input: TelegramBotInput) =>
    api<TelegramBot>(`/api/settings/telegram-bots/${id}`, json('PUT', input)),
  deleteTelegramBot: (id: string) => api<void>(`/api/settings/telegram-bots/${id}`, json('DELETE')),

  channelCounts: () => api<{ widgets: Record<string, number>; telegramBots: Record<string, number> }>('/api/settings/channels/counts'),

  ldap: () => api<LdapSettings>('/api/settings/ldap'),
  saveLdap: (input: LdapSettingsInput) => api<LdapSettings>('/api/settings/ldap', json('PUT', input)),
  checkLdap: () => api<LdapSettings>('/api/settings/ldap/check', json('POST')),
  syncLdap: () => api<LdapSettings>('/api/settings/ldap/sync', json('POST')),

  users: () => api<StaffUser[]>('/api/settings/users'),
  createUser: (input: { login: string; fullName: string; role: Role; alias?: string; password: string; profile?: UserProfileInput }) =>
    api<StaffUser>('/api/settings/users', json('POST', input)),
  updateUser: (
    id: string,
    input: { fullName: string; alias: string | null; role: Role; isBlocked: boolean; profile?: UserProfileInput; password?: string },
  ) => api<StaffUser>(`/api/settings/users/${id}`, json('PUT', input)),
  setUserStatus: (id: string, status: OperatorStatus) => api<StaffUser>(`/api/settings/users/${id}/status`, json('PUT', { status })),
  deleteUser: (id: string) => api<void>(`/api/settings/users/${id}`, json('DELETE')),

  groups: () => api<AgentGroup[]>('/api/settings/groups'),
  createGroup: (name: string) => api<AgentGroup>('/api/settings/groups', json('POST', { name })),
  renameGroup: (id: string, name: string) => api<AgentGroup>(`/api/settings/groups/${id}`, json('PUT', { name })),
  deleteGroup: (id: string) => api<void>(`/api/settings/groups/${id}`, json('DELETE')),
  setPassword: (id: string, password: string) => api<void>(`/api/settings/users/${id}/password`, json('POST', { password })),
}
