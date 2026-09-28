/// <reference types="vitest/config" />
import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'

// https://vite.dev/config/
export default defineConfig({
  plugins: [react(), tailwindcss()],
  build: {
    rollupOptions: {
      // "probe" só existe pro Playwright de e2e/refresh-cross-tab.spec.ts exercitar o
      // httpClient real num browser de verdade (cookies e Web Locks entre abas não dá
      // pra simular em Vitest/jsdom). Removido de dist/ no Dockerfile antes da imagem
      // de produção — não é uma rota do app, ninguém navega até ela por acaso.
      input: {
        main: 'index.html',
        probe: 'e2e/fixtures/refresh-probe.html',
      },
    },
  },
  server: {
    proxy: {
      '/api': 'http://localhost:8080',
      '/hubs': { target: 'ws://localhost:8080', ws: true },
    },
  },
  test: {
    environment: 'jsdom',
    globals: true,
    pool: 'threads',
    fileParallelism: false,
    setupFiles: ['./vitest.setup.ts'],
    css: true,
    coverage: {
      provider: 'v8',
      reporter: ['text', 'html'],
    },
    exclude: ['e2e/**', 'node_modules/**'],
  },
})
