import { Avatar, Button, Dropdown, Layout, Menu, Space, Typography } from 'antd'
import { InboxOutlined, LogoutOutlined, SettingOutlined, TeamOutlined, UserOutlined } from '@ant-design/icons'
import { useTranslation } from 'react-i18next'
import { Outlet, useLocation, useNavigate } from 'react-router'
import { useAuth } from '../auth/AuthContext'

export function AppLayout() {
  const { t } = useTranslation()
  const { user, logout } = useAuth()
  const navigate = useNavigate()
  const location = useLocation()

  const items = [
    { key: '/', icon: <InboxOutlined />, label: t('nav.inbox') },
    { key: '/clients', icon: <TeamOutlined />, label: t('nav.clients'), disabled: true },
    { key: '/settings', icon: <SettingOutlined />, label: t('nav.settings'), disabled: true },
  ]

  return (
    <Layout style={{ minHeight: '100%' }}>
      <Layout.Sider theme="light" width={220} style={{ borderRight: '1px solid #f0f0f0' }}>
        <Typography.Title level={4} style={{ margin: 16 }}>
          {t('app.name')}
        </Typography.Title>
        <Menu mode="inline" selectedKeys={[location.pathname]} items={items} onClick={(e) => navigate(e.key)} />
      </Layout.Sider>
      <Layout>
        <Layout.Header style={{ background: '#fff', borderBottom: '1px solid #f0f0f0', display: 'flex', justifyContent: 'flex-end', alignItems: 'center' }}>
          {user && (
            <Dropdown
              menu={{
                items: [{ key: 'logout', icon: <LogoutOutlined />, label: t('user.logout'), onClick: () => void logout() }],
              }}
            >
              <Button type="text" style={{ height: 'auto' }}>
                <Space>
                  <Avatar size="small" icon={<UserOutlined />} />
                  <span>
                    {user.fullName}
                    <Typography.Text type="secondary"> · {t(`user.roles.${user.role}`)}</Typography.Text>
                  </span>
                </Space>
              </Button>
            </Dropdown>
          )}
        </Layout.Header>
        <Layout.Content style={{ padding: 24 }}>
          <Outlet />
        </Layout.Content>
      </Layout>
    </Layout>
  )
}
