import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from 'react'
import * as client from '../api/client'
import type { User } from '../api/client'

type Status = 'loading' | 'authenticated' | 'anonymous'

interface AuthState {
  status: Status
  user: User | null
  login: (login: string, password: string) => Promise<void>
  logout: () => Promise<void>
  updateUser: (user: User) => void
}

const AuthContext = createContext<AuthState | null>(null)

export function AuthProvider({ children }: { children: ReactNode }) {
  const [status, setStatus] = useState<Status>('loading')
  const [user, setUser] = useState<User | null>(null)

  const setAnonymous = useCallback(() => {
    setUser(null)
    setStatus('anonymous')
  }, [])

  // При открытии страницы пробуем восстановить сессию по refresh-cookie.
  useEffect(() => {
    client.onSessionExpired(setAnonymous)
    client
      .refreshSession()
      .then((result) => {
        if (result) {
          setUser(result.user)
          setStatus('authenticated')
        } else {
          setAnonymous()
        }
      })
      .catch(setAnonymous)
  }, [setAnonymous])

  const login = useCallback(async (loginName: string, password: string) => {
    const result = await client.login(loginName, password)
    setUser(result.user)
    setStatus('authenticated')
  }, [])

  const logout = useCallback(async () => {
    try {
      await client.logout()
    } finally {
      setAnonymous()
    }
  }, [setAnonymous])

  const value = useMemo(() => ({ status, user, login, logout, updateUser: setUser }), [status, user, login, logout])
  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>
}

// eslint-disable-next-line react-refresh/only-export-components
export function useAuth(): AuthState {
  const context = useContext(AuthContext)
  if (!context) throw new Error('useAuth must be used inside AuthProvider')
  return context
}
