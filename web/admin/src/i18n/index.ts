import i18n from 'i18next'
import { initReactI18next } from 'react-i18next'
import en from './locales/en.json'

// Чтобы добавить язык: положить locales/<код>.json с теми же ключами
// и добавить его в resources.
export const resources = {
  en: { translation: en },
} as const

i18n.use(initReactI18next).init({
  resources,
  lng: import.meta.env.VITE_DEFAULT_LOCALE || 'en',
  fallbackLng: 'en',
  interpolation: { escapeValue: false },
})

export default i18n
