/// <reference types="vitest/config" />
import { defineConfig, loadEnv, type Plugin } from 'vite'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'

const NOME_PADRAO = 'WebRadio'

function nomeDaRadio(modo: string): string {
  return loadEnv(modo, process.cwd(), 'VITE_').VITE_NOME_RADIO?.trim() || NOME_PADRAO
}

function escapar(texto: string): string {
  return texto.replace(/[&<>"']/g, (c) => `&#${c.charCodeAt(0)};`)
}

// E5-P01: `<title>`, `meta description` e favicon levam o nome da rádio da variável de build `VITE_NOME_RADIO` (o mesmo
// valor que a SPA lê em `shared/lib/nomeRadio.ts`). O favicon é um SVG com a inicial do nome, gerado no build.
function identidadeDaRadio(): Plugin {
  let nome = NOME_PADRAO
  const faviconSvg = () =>
    `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 64 64"><rect width="64" height="64" rx="14" fill="#2f6fd6"/>` +
    `<text x="32" y="45" font-family="system-ui,sans-serif" font-size="40" font-weight="700" text-anchor="middle" fill="#fff">` +
    `${escapar(Array.from(nome)[0]!.toUpperCase())}</text></svg>`
  return {
    name: 'identidade-da-radio',
    config: (_, { mode }) => {
      nome = nomeDaRadio(mode)
    },
    transformIndexHtml: (html) =>
      html
        .replaceAll('%NOME_RADIO%', escapar(nome))
        .replaceAll(
          '%DESCRICAO_RADIO%',
          escapar(`Ouça a ${nome} ao vivo, veja a grade e peça sua música.`),
        ),
    configureServer: (server) => {
      server.middlewares.use('/favicon.svg', (_req, res) => {
        res.setHeader('Content-Type', 'image/svg+xml')
        res.end(faviconSvg())
      })
    },
    generateBundle() {
      this.emitFile({ type: 'asset', fileName: 'favicon.svg', source: faviconSvg() })
    },
  }
}

// Build de produção — single entry só, sem nada de teste (Guardian H2). O
// vite.config.e2e.ts é quem adiciona a fixture do Playwright.
export default defineConfig({
  plugins: [react(), tailwindcss(), identidadeDaRadio()],
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
