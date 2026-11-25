import {
  ApiOutlined,
  BarChartOutlined,
  IdcardOutlined,
  MailOutlined,
  MessageOutlined,
  QuestionCircleOutlined,
  SearchOutlined,
  SettingOutlined,
  SwapOutlined,
  TagOutlined,
  ThunderboltOutlined,
  UserOutlined,
} from '@ant-design/icons'
import type { ReactNode } from 'react'
import type { Role } from '../api/client'

export interface MenuNode {
  /** Адрес страницы; для группы с подменю — условный ключ. */
  key: string
  /** Ключ перевода. */
  label: string
  icon?: ReactNode
  /** Кому виден пункт. Пусто — всем. */
  roles?: Role[]
  /** Раздел уже работает. Иначе по адресу открывается заглушка «Coming soon». */
  ready?: boolean
  /** Зелёная иконка: разделы, где идёт работа с обращениями. */
  accent?: boolean
  children?: MenuNode[]
}

const admin: Role[] = ['Admin']
const managers: Role[] = ['Admin', 'Supervisor']

/**
 * Меню админки. Порядок и группы — как в Usedesk, к которому привыкли операторы.
 * Разделы, которых ещё нет, видны в меню и открывают заглушку: так видно, где они появятся.
 */
export const menu: MenuNode[] = [
  { key: '/search', label: 'nav.search', icon: <SearchOutlined /> },
  { key: '/', label: 'nav.inbox', icon: <MessageOutlined />, ready: true, accent: true },
  { key: '/requests', label: 'nav.requests', icon: <MailOutlined />, accent: true },
  { key: '/tags', label: 'nav.tags', icon: <TagOutlined /> },
  {
    key: 'group:clients',
    label: 'nav.clients',
    icon: <IdcardOutlined />,
    children: [
      { key: '/clients', label: 'nav.clientList', ready: true },
      { key: '/clients/new', label: 'nav.clientCreate' },
      { key: '/companies', label: 'nav.companies' },
    ],
  },
  { key: '/agents', label: 'nav.agents', icon: <UserOutlined />, roles: admin, ready: true },
  { key: '/knowledge', label: 'nav.knowledge', icon: <QuestionCircleOutlined /> },
  {
    key: 'group:reports',
    label: 'nav.reports',
    icon: <BarChartOutlined />,
    roles: managers,
    children: [
      { key: '/reports/main', label: 'nav.reportsMain' },
      { key: '/reports/chats', label: 'nav.reportsChats' },
      { key: '/reports/agents', label: 'nav.reportsAgents' },
      { key: '/reports/csi', label: 'nav.reportsCsi' },
      { key: '/reports/sla', label: 'nav.reportsSla' },
      { key: '/reports/fields', label: 'nav.reportsFields' },
    ],
  },
  { key: '/channels', label: 'nav.channels', icon: <ApiOutlined />, roles: admin, ready: true },
  {
    key: 'group:automation',
    label: 'nav.automation',
    icon: <ThunderboltOutlined />,
    roles: admin,
    children: [
      { key: '/automation/templates', label: 'nav.templates' },
      { key: '/automation/rules', label: 'nav.rules', ready: true },
      { key: '/automation/run', label: 'nav.runRules' },
    ],
  },
  { key: '/extensions', label: 'nav.extensions', icon: <SwapOutlined />, roles: admin },
  {
    key: 'group:settings',
    label: 'nav.settings',
    icon: <SettingOutlined />,
    roles: admin,
    children: [
      { key: '/settings/sites', label: 'nav.sites', ready: true },
      { key: '/settings/company', label: 'nav.company' },
      { key: '/settings/office-hours', label: 'nav.officeHours' },
      { key: '/settings/lists', label: 'nav.lists' },
      { key: '/settings/csi', label: 'nav.csi' },
      { key: '/settings/sla', label: 'nav.sla' },
      { key: '/settings/fields', label: 'nav.fields' },
      { key: '/settings/ldap', label: 'nav.ldap', ready: true },
      { key: '/settings/blocks', label: 'nav.blocks' },
      { key: '/settings/security', label: 'nav.security' },
    ],
  },
]

export const visibleTo = (node: MenuNode, role: Role | undefined) => !node.roles || (!!role && node.roles.includes(role))

/** Все конечные пункты вместе с группой, в которой они лежат. */
export function leaves(nodes = menu): { node: MenuNode; parent?: MenuNode }[] {
  return nodes.flatMap((n) => (n.children ? n.children.map((c) => ({ node: c, parent: n })) : [{ node: n }]))
}

/** Пункт меню для адреса: самый длинный подходящий путь (/settings/sites/123 → Sites). */
export function findLeaf(pathname: string) {
  const path = pathname.startsWith('/tickets') ? '/' : pathname
  return leaves()
    .filter(({ node }) => node.key === path || (node.key !== '/' && path.startsWith(node.key + '/')))
    .sort((a, b) => b.node.key.length - a.node.key.length)[0]
}
