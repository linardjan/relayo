import { defineConfig, loadEnv } from 'vite'
import react from '@vitejs/plugin-react'
import path from 'node:path'

// Переменные берутся из общего .env в корне репозитория (только с префиксом VITE_).
const envDir = path.resolve(__dirname, '../..')

export default defineConfig(({ mode }) => {
  const env = loadEnv(mode, envDir, 'VITE_')
  return {
    envDir,
    plugins: [react()],
    server: {
      port: 5173,
      strictPort: true,
      // В разработке /api проксируется на внутренний сервис, поэтому админка и API
      // работают с одного адреса: CORS не нужен, cookie с refresh-токеном работает как в бою.
      proxy: {
        '/api': { target: env.VITE_API_BASE_URL || 'http://localhost:5100', changeOrigin: false },
        '/hubs': { target: env.VITE_API_BASE_URL || 'http://localhost:5100', changeOrigin: false, ws: true },
      },
    },
  }
})
