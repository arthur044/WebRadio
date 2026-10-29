import { defineConfig, devices } from '@playwright/test'

// E2E_BASE_URL liga o modo "stack": roda contra o compose já de pé (o job compose-smoke do CI, DoD 2 do 04), sem subir
// o vite preview. O refresh-cross-tab fica de fora de propósito: ele depende da fixture refresh-probe e dos mocks de
// rota, que só existem no build e2e (Guardian H2). Os specs que dependem do backend real (admin-grade, grade-ao-vivo)
// fazem o caminho inverso: só rodam no modo stack (admin-grade, grade-ao-vivo, pedidos-moderacao). Os de tela com a API
// mockada por rota (pedir-musica, mural) só rodam no preview: no stack a API é a de verdade. No stack os specs mexem no mesmo banco, então rodam em série.
const stackURL = process.env.E2E_BASE_URL
// No CI o browser é o Chrome que já vem no runner (E2E_CHANNEL=chrome): nada é baixado, então não há binário sem checksum (05 §9).
const canal = process.env.E2E_CHANNEL ?? 'chromium'

export default defineConfig({
  testDir: './e2e',
  testIgnore: stackURL
    ? /(refresh-cross-tab|pedir-musica|mural)\.spec\.ts$/
    : /(admin-grade|grade-ao-vivo|pedidos-moderacao)\.spec\.ts$/,
  globalSetup: stackURL ? './e2e/global-setup.ts' : undefined,
  fullyParallel: !stackURL,
  workers: stackURL ? 1 : undefined,
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 2 : 0,
  reporter: 'html',
  use: {
    baseURL: stackURL ?? 'http://localhost:4173',
    // No modo stack os specs digitam a senha nova do Admin; o trace gravaria esse valor em test-results/ (Sentinel L3 do #42).
    trace: stackURL ? 'off' : 'on-first-retry',
  },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'], channel: canal } }],
  webServer: stackURL
    ? undefined
    : {
        command: 'npx vite preview --config vite.config.e2e.ts --port 4173',
        url: 'http://localhost:4173',
        reuseExistingServer: !process.env.CI,
      },
})
