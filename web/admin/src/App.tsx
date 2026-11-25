import { Spin } from 'antd'
import { Navigate, Route, Routes, useLocation } from 'react-router'
import type { ReactNode } from 'react'
import { useAuth } from './auth/AuthContext'
import { AppLayout } from './layout/AppLayout'
import { InboxPage } from './pages/InboxPage'
import { LoginPage } from './pages/LoginPage'

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

export default function App() {
  return (
    <Routes>
      <Route path="/login" element={<LoginPage />} />
      <Route
        element={
          <RequireAuth>
            <AppLayout />
          </RequireAuth>
        }
      >
        <Route index element={<InboxPage />} />
      </Route>
      <Route path="*" element={<Navigate to="/" replace />} />
    </Routes>
  )
}
