// Настройки шлюза из переменных окружения. Ошибки собираются все сразу, как в сервисах .NET.

export interface GatewayConfig {
  port: number
  /** Общий ключ: им шлюз проверяет вызовы Internal и подписывает свои вызовы в Internal. */
  key: string
  /** Где лежат ключи сессий WhatsApp (по папке на канал). Это секретные данные: доступ к номеру. */
  sessionsDir: string
  /** Адрес API внутреннего сервиса, куда шлюз передаёт входящие сообщения. */
  internalUrl: string
  /** Файлы больше этого размера не скачиваются: оператор увидит пометку. */
  maxFileBytes: number
  logLevel: string
}

export function loadConfig(env: NodeJS.ProcessEnv = process.env): GatewayConfig {
  const errors: string[] = []
  const int = (name: string, fallback: number) => {
    const raw = env[name]
    if (!raw) return fallback
    const value = Number(raw)
    if (!Number.isInteger(value) || value <= 0) errors.push(`${name} must be a positive integer, got '${raw}'`)
    return value
  }

  const key = env.WHATSAPP_GATEWAY_KEY ?? ''
  if (key.length < 32) errors.push('WHATSAPP_GATEWAY_KEY must be at least 32 characters (generate with: openssl rand -base64 32)')

  const internalUrl = env.WHATSAPP_INTERNAL_URL ?? `http://localhost:${env.INTERNAL_HTTP_PORT ?? 5100}`
  try {
    new URL(internalUrl)
  } catch {
    errors.push(`WHATSAPP_INTERNAL_URL must be an absolute URL, got '${internalUrl}'`)
  }

  const config: GatewayConfig = {
    port: int('WHATSAPP_GATEWAY_PORT', 5400),
    key,
    sessionsDir: env.WHATSAPP_SESSIONS_DIR || './sessions',
    internalUrl: internalUrl.replace(/\/+$/, ''),
    // По умолчанию — как лимит вложений во внутреннем сервисе.
    maxFileBytes: int('ATTACHMENTS_MAX_SIZE_MB', 20) * 1024 * 1024,
    logLevel: env.WHATSAPP_LOG_LEVEL || 'info',
  }
  if (errors.length > 0) throw new Error('Invalid configuration:\n  ' + errors.join('\n  '))
  return config
}
