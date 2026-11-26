// HTTP-клиент API админки.
// Access-токен хранится только в памяти: после перезагрузки страницы сессия
// восстанавливается через refresh-токен из httpOnly cookie.

export type Role = 'Admin' | 'Supervisor' | 'Operator'

export interface User {
  id: string
  login: string
  fullName: string
  alias: string | null
  role: Role
  source: 'Local' | 'ActiveDirectory'
}

export interface AuthResponse {
  accessToken: string
  accessTokenExpiresAt: string
  user: User
}

export class ApiError extends Error {
  readonly status: number
  readonly code: string | undefined

  constructor(status: number, message: string, code?: string) {
    super(message)
    this.status = status
    this.code = code
  }
}

let accessToken: string | null = null
let refreshInFlight: Promise<AuthResponse | null> | null = null
let onSessionLost: (() => void) | null = null

export function setAccessToken(token: string | null) {
  accessToken = token
}

export function getAccessToken(): string | null {
  return accessToken
}

/** Вызывается, когда сессию не удалось продлить: пора показать форму входа. */
export function onSessionExpired(handler: () => void) {
  onSessionLost = handler
}

async function post<T>(url: string, body?: unknown): Promise<T> {
  const response = await fetch(url, {
    method: 'POST',
    headers: body === undefined ? undefined : { 'content-type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
    credentials: 'same-origin',
  })
  if (!response.ok) {
    let code: string | undefined
    let title = response.statusText
    try {
      const problem = await response.json()
      code = problem.code
      title = problem.title ?? title
    } catch {
      // тело не JSON
    }
    throw new ApiError(response.status, title, code)
  }
  return (response.status === 204 ? undefined : await response.json()) as T
}

const delay = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms))

async function doRefresh(): Promise<AuthResponse | null> {
  // Две вкладки могут обновить сессию одновременно. Тогда вторая получит 401,
  // но новый refresh-токен к этому моменту уже лежит в общей cookie: повторяем один раз.
  for (let attempt = 0; attempt < 2; attempt++) {
    try {
      const result = await post<AuthResponse>('/api/auth/refresh')
      setAccessToken(result.accessToken)
      return result
    } catch (error) {
      if (!(error instanceof ApiError) || error.status !== 401) throw error
      if (attempt === 0) await delay(300)
    }
  }
  setAccessToken(null)
  return null
}

/** Продлевает сессию. Одновременные вызовы внутри вкладки объединяются в один запрос. */
export function refreshSession(): Promise<AuthResponse | null> {
  refreshInFlight ??= doRefresh().finally(() => {
    refreshInFlight = null
  })
  return refreshInFlight
}

export async function login(loginName: string, password: string): Promise<AuthResponse> {
  const result = await post<AuthResponse>('/api/auth/login', { login: loginName, password })
  setAccessToken(result.accessToken)
  return result
}

export async function logout(): Promise<void> {
  try {
    await post<void>('/api/auth/logout')
  } finally {
    setAccessToken(null)
  }
}

/** Запрос к API с access-токеном. При 401 один раз продлевает сессию и повторяет запрос. */
export async function api<T>(url: string, init: RequestInit = {}): Promise<T> {
  const send = () =>
    fetch(url, {
      ...init,
      headers: {
        // FormData (файлы) браузер оформляет сам, с границей частей в заголовке.
        ...(typeof init.body === 'string' ? { 'content-type': 'application/json' } : {}),
        ...init.headers,
        ...(accessToken ? { authorization: `Bearer ${accessToken}` } : {}),
      },
      credentials: 'same-origin',
    })

  let response = await send()
  if (response.status === 401) {
    const refreshed = await refreshSession()
    if (!refreshed) {
      onSessionLost?.()
      throw new ApiError(401, 'Session expired')
    }
    response = await send()
  }
  if (!response.ok) {
    let code: string | undefined
    let title = response.statusText
    try {
      const problem = await response.json()
      code = problem.code
      title = problem.title ?? title
    } catch {
      // тело не JSON
    }
    throw new ApiError(response.status, title, code)
  }
  return (response.status === 204 ? undefined : await response.json()) as T
}
