import { render } from 'preact'
import { App } from './App'
import { Controller } from './controller'
import { styles } from './styles'
import type { WidgetConfig } from './api'

// Код вставки на сайт — одна строка на опубликованный файл виджета в S3:
//
//   <script async src="https://<адрес файлов>/widget_<ключ>.js"></script>
//
// Файл содержит настройки виджета и загрузчик, который подключает это ядро с атрибутами
// data-relayo-widget, data-relayo-api и data-relayo-config. Без S3 ядро можно подключить напрямую:
//
//   <script async src="https://<публичный адрес>/widget/core.js" data-relayo-widget="<id виджета>"></script>
//
// Скрипт грузится асинхронно. Методы Relayo.* появляются после загрузки, тогда же на window
// срабатывает событие "relayo:ready". Сайт, который вызывает методы сразу при открытии страницы, делает так:
//
//   function withRelayo(fn) {
//     if (window.Relayo && window.Relayo.open) fn(window.Relayo)
//     else window.addEventListener('relayo:ready', function () { fn(window.Relayo) }, { once: true })
//   }
//   withRelayo(function (r) { r.identify(token) })
//
// Если на странице есть старая заглушка с очередью window.Relayo.q, накопленные вызовы тоже выполнятся.

type Method = keyof Pick<Controller, 'identify' | 'setClientId' | 'setProduct' | 'setLocale' | 'open' | 'close' | 'logout' | 'on'>

const methods: Method[] = ['identify', 'setClientId', 'setProduct', 'setLocale', 'open', 'close', 'logout', 'on']

declare global {
  interface Window {
    Relayo?: { q?: [Method, unknown[]][] } & Partial<Record<Method, (...args: unknown[]) => unknown>>
  }
}

function boot(script: HTMLScriptElement | null) {
  const widgetId = script?.dataset.relayoWidget
  if (!script || !widgetId) {
    console.error('[Relayo] data-relayo-widget attribute is missing on the widget script')
    return
  }
  if (document.querySelector('[data-relayo-root]')) return // уже подключён

  // Файл виджета из S3 (widget_<ключ>.js) передаёт адрес API и настройки; при прямом подключении core.js
  // API находится там же, откуда загружен скрипт, а настройки запрашиваются по API.
  const baseUrl = script.dataset.relayoApi || new URL(script.src).origin
  const controller = new Controller(baseUrl, widgetId, readConfig(script))

  // Публичные методы вместо заглушек, затем — накопленные вызовы.
  const api = (window.Relayo ??= {})
  const queued = api.q ?? []
  for (const m of methods) {
    api[m] = (...args: unknown[]) => (controller[m] as (...a: unknown[]) => unknown)(...args)
  }
  api.q = []

  const host = document.createElement('div')
  host.setAttribute('data-relayo-root', '')
  document.body.appendChild(host)
  const shadow = host.attachShadow({ mode: 'open' })
  const style = document.createElement('style')
  const mount = document.createElement('div')
  shadow.append(style, mount)

  const applyStyles = () => {
    style.textContent = styles(controller.state.config?.accentColor ?? '#1677ff')
  }
  applyStyles()
  controller.subscribe(applyStyles)
  render(<App controller={controller} />, mount)

  for (const [method, args] of queued) {
    if (methods.includes(method)) api[method]!(...args)
  }
  // Методы готовы: сайт может их вызывать.
  window.dispatchEvent(new Event('relayo:ready'))
  void controller.init()
}

function readConfig(script: HTMLScriptElement): WidgetConfig | null {
  try {
    return script.dataset.relayoConfig ? (JSON.parse(script.dataset.relayoConfig) as WidgetConfig) : null
  } catch {
    console.warn('[Relayo] Widget settings in the widget file are damaged, loading them from the server')
    return null
  }
}

// currentScript доступен только во время выполнения скрипта: запоминаем его сразу.
const currentScript =
  (document.currentScript as HTMLScriptElement | null) ??
  document.querySelector<HTMLScriptElement>('script[data-relayo-widget]')

if (document.readyState === 'loading') {
  document.addEventListener('DOMContentLoaded', () => boot(currentScript))
} else {
  boot(currentScript)
}
