import { ApiError } from '../api/client'

/** Текст ошибки с сервера (он на английском и понятен администратору) или общий. */
export function errorText(error: unknown, fallback: string): string {
  return error instanceof ApiError && error.status < 500 && error.message ? error.message : fallback
}
