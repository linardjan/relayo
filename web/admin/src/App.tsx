import { Spin } from 'antd'
import { Navigate, Route, Routes, useLocation } from 'react-router'
import type { ReactNode } from 'react'
import { useAuth } from './auth/AuthContext'
import { InboxStateProvider } from './inbox/InboxState'
import { AppLayout } from './layout/AppLayout'
import { ClientPage } from './pages/ClientPage'
import { ClientsPage } from './pages/ClientsPage'
import { InboxPage } from './pages/InboxPage'
import { LoginPage } from './pages/LoginPage'
import { AgentPage } from './pages/agents/AgentPage'
import { AgentsPage } from './pages/agents/AgentsPage'
import { ChannelsPage } from './pages/channels/ChannelsPage'
import { TelegramBotPage } from './pages/channels/TelegramBotPage'
import { WhatsAppChannelPage } from './pages/channels/WhatsAppChannelPage'
import { WidgetPage } from './pages/channels/WidgetPage'
import { ComingSoonPage } from './pages/ComingSoonPage'
import { RulePage } from './pages/automation/RulePage'
import { RulesPage } from './pages/automation/RulesPage'
import { LdapPage } from './pages/settings/LdapPage'
import { SitePage } from './pages/settings/SitePage'
import { leaves } from './layout/menu'
import { SitesPage } from './pages/settings/SitesPage'

function RequireAuth({ children }: { children: ReactNode }) {
  const { status } = useAuth()
  const location = useLocation()

  if (status === 'loading')
    return (
      <div style={{ minHeight: '100%', display: 'grid', placeItems: 'center' }}>
        <Spin size="large" />
      </div>
    )
  if (status === 'anonymous') return <Navigate to="/login" replace state={{ from: location.pathname }} />
  return <>{children}</>
}

/** Раздел только для администратора: остальным он не виден, а прямой переход ведёт в Inbox. */
function RequireAdmin({ children }: { children: ReactNode }) {
  const { user } = useAuth()
  return user?.role === 'Admin' ? <>{children}</> : <Navigate to="/" replace />
}

export default function App() {
  return (
    <Routes>
      <Route path="/login" element={<LoginPage />} />
      <Route
        element={
          <RequireAuth>
            <InboxStateProvider>
              <AppLayout />
            </InboxStateProvider>
          </RequireAuth>
        }
      >
        <Route index element={<InboxPage />} />
        <Route path="tickets/:ticketId" element={<InboxPage />} />
        <Route path="clients" element={<ClientsPage />} />
        <Route path="clients/:clientId" element={<ClientPage />} />
        {/* Разделы администратора */}
        <Route path="agents" element={<RequireAdmin><AgentsPage /></RequireAdmin>} />
        <Route path="agents/new" element={<RequireAdmin><AgentPage /></RequireAdmin>} />
        <Route path="agents/:agentId" element={<RequireAdmin><AgentPage /></RequireAdmin>} />
        <Route path="channels" element={<RequireAdmin><ChannelsPage /></RequireAdmin>} />
        <Route path="channels/widgets/new" element={<RequireAdmin><WidgetPage /></RequireAdmin>} />
        <Route path="channels/widgets/:widgetId" element={<RequireAdmin><WidgetPage /></RequireAdmin>} />
        <Route path="channels/telegram/new" element={<RequireAdmin><TelegramBotPage /></RequireAdmin>} />
        <Route path="channels/telegram/:botId" element={<RequireAdmin><TelegramBotPage /></RequireAdmin>} />
        <Route path="channels/whatsapp/new" element={<RequireAdmin><WhatsAppChannelPage /></RequireAdmin>} />
        <Route path="channels/whatsapp/:channelId" element={<RequireAdmin><WhatsAppChannelPage /></RequireAdmin>} />
        <Route path="settings" element={<Navigate to="/settings/sites" replace />} />
        <Route path="settings/sites" element={<RequireAdmin><SitesPage /></RequireAdmin>} />
        <Route path="settings/sites/new" element={<RequireAdmin><SitePage /></RequireAdmin>} />
        <Route path="settings/sites/:siteId" element={<RequireAdmin><SitePage /></RequireAdmin>} />
        <Route path="settings/ldap" element={<RequireAdmin><LdapPage /></RequireAdmin>} />
        <Route path="automation/rules" element={<RequireAdmin><RulesPage /></RequireAdmin>} />
        <Route path="automation/rules/new" element={<RequireAdmin><RulePage /></RequireAdmin>} />
        <Route path="automation/rules/:ruleId" element={<RequireAdmin><RulePage /></RequireAdmin>} />

        {/* Разделы, которых ещё нет: заглушки из конфига меню */}
        {leaves()
          .filter(({ node }) => !node.ready)
          .map(({ node, parent }) => {
            const page = <ComingSoonPage node={node} parent={parent} />
            const adminOnly = (node.roles ?? parent?.roles)?.length === 1
            return <Route key={node.key} path={node.key.slice(1)} element={adminOnly ? <RequireAdmin>{page}</RequireAdmin> : page} />
          })}
      </Route>
      <Route path="*" element={<Navigate to="/" replace />} />
    </Routes>
  )
}
