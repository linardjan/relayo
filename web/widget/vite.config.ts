import { defineConfig } from 'vite'
import preact from '@preact/preset-vite'
import path from 'node:path'

// Виджет собирается в один файл core.js, который раздаёт публичный сервис:
// src/Relayo.Public/wwwroot/widget/core.js. Позже его будет публиковать конструктор виджетов в S3.
export default defineConfig({
  plugins: [preact()],
  build: {
    outDir: path.resolve(__dirname, '../../src/Relayo.Public/wwwroot/widget'),
    emptyOutDir: true,
    target: 'es2020',
    lib: {
      entry: path.resolve(__dirname, 'src/main.tsx'),
      name: 'RelayoWidget',
      formats: ['iife'],
      fileName: () => 'core.js',
    },
  },
})
