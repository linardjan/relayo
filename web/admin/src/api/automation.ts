import { api } from './client'

export interface RuleTrigger {
  type: string
  minutes?: number | null
}

export interface RuleCondition {
  field: string
  operator: string
  value: string | null
}

export interface RuleAction {
  type: string
  value?: string | null
}

export interface RuleDefinition {
  whenAll: RuleTrigger[]
  whenAny: RuleTrigger[]
  whatAll: RuleCondition[]
  whatAny: RuleCondition[]
  actions: RuleAction[]
}

export interface RuleInput {
  name: string
  description: string | null
  isEnabled: boolean
  isManual: boolean
  useWordForms: boolean
  ignoreHtml: boolean
  stopProcessing: boolean
  definition: RuleDefinition
}

export interface Rule extends RuleInput {
  id: string
  createdAt: string
  updatedAt: string
  revisions: { id: string; userName: string; summary: string; createdAt: string }[]
}

export interface RuleSummary {
  id: string
  name: string
  isEnabled: boolean
  isManual: boolean
  position: number
  updatedAt: string
}

const json = (method: string, body?: unknown): RequestInit => ({ method, body: body === undefined ? undefined : JSON.stringify(body) })

export const rulesApi = {
  list: () => api<RuleSummary[]>('/api/automation/rules'),
  get: (id: string) => api<Rule>(`/api/automation/rules/${id}`),
  create: (input: RuleInput) => api<Rule>('/api/automation/rules', json('POST', input)),
  update: (id: string, input: RuleInput) => api<Rule>(`/api/automation/rules/${id}`, json('PUT', input)),
  remove: (id: string) => api<void>(`/api/automation/rules/${id}`, json('DELETE')),
  reorder: (order: string[]) => api<RuleSummary[]>('/api/automation/rules/order', json('PUT', order)),
  log: (days: number) =>
    api<{ createdAt: string; ruleName: string; ticketNumber: number; trigger: string; applied: boolean; details: string }[]>(
      `/api/automation/rules/log?days=${days}`,
    ),
}

// Справочник событий, условий и действий. Совпадает с RuleCatalog на сервере.
export type ValueKind = 'None' | 'Text' | 'ChannelType' | 'Channel' | 'Site' | 'Status' | 'User' | 'Boolean'

export const triggerTypes: { type: string; hasMinutes: boolean }[] = [
  { type: 'client_message', hasMinutes: false },
  { type: 'ticket_created', hasMinutes: false },
  { type: 'status_changed', hasMinutes: false },
  { type: 'operator_replied', hasMinutes: false },
  { type: 'ticket_assigned', hasMinutes: false },
  { type: 'ticket_closed', hasMinutes: false },
  { type: 'out_of_hours_message', hasMinutes: false },
  { type: 'returning_client', hasMinutes: false },
  { type: 'no_operator_reply', hasMinutes: true },
  { type: 'no_client_reply', hasMinutes: true },
]

const equality = ['eq', 'neq']
export const conditionFields: { field: string; operators: string[]; value: ValueKind }[] = [
  { field: 'channel_type', operators: equality, value: 'ChannelType' },
  { field: 'channel', operators: equality, value: 'Channel' },
  { field: 'site', operators: equality, value: 'Site' },
  { field: 'status', operators: equality, value: 'Status' },
  { field: 'assignee', operators: equality, value: 'User' },
  { field: 'client_verified', operators: ['eq'], value: 'Boolean' },
  { field: 'message_text', operators: ['contains', 'not_contains', 'eq', 'regex'], value: 'Text' },
  { field: 'client_locale', operators: equality, value: 'Text' },
]

export const actionTypes: { type: string; value: ValueKind }[] = [
  { type: 'send_message', value: 'Text' },
  { type: 'set_status', value: 'Status' },
  { type: 'assign', value: 'User' },
  { type: 'unassign', value: 'None' },
  { type: 'close_ticket', value: 'None' },
]
