import { Avatar, Button, ConfigProvider, Layout, Menu, Tooltip } from 'antd'
import type { MenuProps } from 'antd'
import { EditOutlined, LogoutOutlined, MenuFoldOutlined, MenuUnfoldOutlined } from '@ant-design/icons'
import { useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Outlet, useLocation, useNavigate } from 'react-router'
import { useAuth } from '../auth/AuthContext'
import { AliasModal } from '../inbox/AliasModal'
import { useInboxState } from '../inbox/InboxState'
import { findLeaf, menu, visibleTo, type MenuNode } from './menu'

const SIDER_BG = '#2b303a'
const COLLAPSED_KEY = 'relayo:sider-collapsed'
const ACCENT = '#36b57c'

type MenuItem = NonNullable<MenuProps['items']>[number]

/**
 * Каркас админки: тёмная боковая панель (логотип, пользователь, меню с подменю) и содержимое справа.
 * Панель можно свернуть до иконок, состояние запоминается.
 */
export function AppLayout() {
  const { t } = useTranslation()
  const { user, logout } = useAuth()
  const navigate = useNavigate()
  const location = useLocation()
  const [aliasOpen, setAliasOpen] = useState(false)
  const [collapsed, setCollapsed] = useState(() => localStorage.getItem(COLLAPSED_KEY) === '1')

  const toggle = () => {
    localStorage.setItem(COLLAPSED_KEY, collapsed ? '0' : '1')
    setCollapsed(!collapsed)
  }

  const { unreadCount } = useInboxState()
  const current = findLeaf(location.pathname)
  const toItem = (node: MenuNode): MenuItem => ({
    key: node.key,
    // У «Chats» — число новых обращений, как во вкладке браузера.
    label:
      node.key === '/' && unreadCount > 0 ? (
        <span className="menu-label-badge">
          {t(node.label)}
          <span className="menu-badge">{unreadCount}</span>
        </span>
      ) : (
        t(node.label)
      ),
    icon: node.icon && node.accent ? <span style={{ color: ACCENT }}>{node.icon}</span> : node.icon,
    children: node.children?.filter((c) => visibleTo(c, user?.role)).map(toItem),
  })
  const items = menu.filter((n) => visibleTo(n, user?.role)).map(toItem)

  const initials = (user?.fullName ?? '?')
    .split(/\s+/)
    .filter(Boolean)
    .slice(0, 2)
    .map((p) => p[0]!.toUpperCase())
    .join('')

  return (
    <Layout style={{ minHeight: '100%', background: SIDER_BG }}>
      <ConfigProvider
        theme={{
          components: {
            Layout: { siderBg: SIDER_BG },
            Menu: {
              darkItemBg: SIDER_BG,
              darkSubMenuItemBg: '#252a33',
              darkItemColor: '#b1b7c1',
              darkItemSelectedBg: '#20242c',
              darkItemSelectedColor: '#fff',
              darkPopupBg: SIDER_BG,
              itemHeight: 40,
              itemMarginBlock: 0,
              itemMarginInline: 0,
              itemBorderRadius: 0,
              fontSize: 13,
            },
          },
        }}
      >
        <Layout.Sider
          width={220}
          collapsedWidth={64}
          collapsed={collapsed}
          trigger={null}
          style={{ position: 'sticky', top: 0, height: '100vh', overflow: 'auto' }}
        >
          <div
            style={{
              display: 'flex',
              alignItems: 'center',
              justifyContent: collapsed ? 'center' : 'space-between',
              gap: 8,
              padding: collapsed ? '14px 0' : '14px 12px 14px 16px',
              borderBottom: '1px solid rgba(255,255,255,0.08)',
            }}
          >
            {!collapsed && (
              <div style={{ display: 'flex', alignItems: 'center', gap: 8, color: '#fff', fontSize: 18, fontWeight: 600 }}>
                <img src="/favicon.png" alt="" width={28} height={28} />
                {t('app.name')}
              </div>
            )}
            <Button
              type="text"
              aria-label={t('nav.toggleMenu')}
              icon={collapsed ? <MenuUnfoldOutlined /> : <MenuFoldOutlined />}
              onClick={toggle}
              style={{ color: 'rgba(255,255,255,0.85)', border: '1px solid rgba(255,255,255,0.15)' }}
            />
          </div>

          {user && (
            <div className="sider-user" style={collapsed ? { justifyContent: 'center', padding: '14px 0' } : undefined}>
              <Tooltip title={collapsed ? user.fullName : undefined} placement="right">
                <Avatar size={collapsed ? 36 : 44} style={{ background: '#e8424f', flexShrink: 0 }}>
                  {initials}
                </Avatar>
              </Tooltip>
              {!collapsed && (
                <div style={{ minWidth: 0 }}>
                  <div className="sider-user-name" title={user.fullName}>
                    {user.fullName}
                  </div>
                  {/* Псевдоним видят клиенты: без него нельзя отвечать, поэтому он всегда на виду. */}
                  <button
                    type="button"
                    className={`sider-link${user.alias ? '' : ' warning'}`}
                    title={t('alias.title')}
                    onClick={() => setAliasOpen(true)}
                  >
                    {user.alias ? t('user.aliasShort', { alias: user.alias }) : t('user.noAlias')} <EditOutlined />
                  </button>
                  <div>
                    <button type="button" className="sider-link" onClick={() => void logout()}>
                      {t('user.logout')} <LogoutOutlined />
                    </button>
                  </div>
                </div>
              )}
            </div>
          )}

          <Menu
            className="sider-menu"
            theme="dark"
            mode="inline"
            selectedKeys={current ? [current.node.key] : []}
            defaultOpenKeys={current?.parent ? [current.parent.key] : []}
            items={items}
            onClick={(e) => navigate(e.key)}
            style={{ borderInlineEnd: 'none' }}
          />
          {collapsed && user && (
            <Menu
              className="sider-menu"
              theme="dark"
              mode="inline"
              selectable={false}
              items={[{ key: 'logout', icon: <LogoutOutlined />, label: t('user.logout') }]}
              onClick={() => void logout()}
              style={{ borderInlineEnd: 'none' }}
            />
          )}
        </Layout.Sider>
      </ConfigProvider>
      <Layout>
        <Layout.Content style={{ padding: 16, background: '#f0f2f5' }}>
          <Outlet />
        </Layout.Content>
        <AliasModal open={aliasOpen} onClose={() => setAliasOpen(false)} />
      </Layout>
    </Layout>
  )
}
