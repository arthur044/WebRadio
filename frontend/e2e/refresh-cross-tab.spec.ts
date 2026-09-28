import { expect, test, type BrowserContext } from '@playwright/test'

declare global {
  interface Window {
    __probe: (expiredToken: string) => Promise<void>
  }
}

const REFRESH_DELAY_MS = 600

/**
 * Instrumenta um mock de /auth/refresh + /probe/protegido com um atraso artificial
 * de REFRESH_DELAY_MS no handler de /auth/refresh, ANTES de responder. Isso abre uma
 * janela larga e determinística pra segunda chamada chegar enquanto a primeira ainda
 * está "em voo" do lado do servidor — sem essa janela, o round-trip real (alguns ms)
 * faz uma chamada terminar antes da outra nem começar, e um teste sem atraso "passa"
 * mesmo sem lock nenhum (foi exatamente o que o Guardian reproduziu, e o que uma
 * primeira tentativa com barreira na chegada da requisição não conseguiu forçar).
 * `maxConcurrentRefresh` é o que prova se duas chamadas ficaram em voo ao mesmo tempo.
 */
function instrumentMocks(context: BrowserContext) {
  let currentRefreshCookie = 'v1'
  const usedCookies = new Set<string>()
  let refreshInFlight = 0
  let maxConcurrentRefresh = 0
  let rotationCounter = 1

  const instrumentation = {
    getMaxConcurrentRefresh: () => maxConcurrentRefresh,
  }

  void context.route('**/api/v1/auth/refresh', async (route) => {
    refreshInFlight += 1
    maxConcurrentRefresh = Math.max(maxConcurrentRefresh, refreshInFlight)

    await new Promise((resolve) => setTimeout(resolve, REFRESH_DELAY_MS))

    const cookieHeader = route.request().headers()['cookie'] ?? ''
    const presented = /wr_refresh=([^;]+)/.exec(cookieHeader)?.[1]

    if (presented !== currentRefreshCookie || usedCookies.has(presented ?? '')) {
      refreshInFlight -= 1
      await route.fulfill({
        status: 401,
        contentType: 'application/json',
        body: JSON.stringify({ type: '/erros/credenciais', title: 'reuso detectado', status: 401 }),
      })
      return
    }

    usedCookies.add(presented)
    rotationCounter += 1
    currentRefreshCookie = `v${rotationCounter}`
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      headers: { 'Set-Cookie': `wr_refresh=${currentRefreshCookie}; Path=/; HttpOnly` },
      body: JSON.stringify({
        accessToken: `token-${currentRefreshCookie}`,
        expiraEmUtc: new Date().toISOString(),
        deveTrocarSenha: false,
        usuario: { id: '1', nome: 'Ouvinte Teste', email: 't@t.com', role: 'Ouvinte', deveTrocarSenha: false },
      }),
    })
    refreshInFlight -= 1
  })

  void context.route('**/api/v1/probe/protegido', async (route) => {
    const auth = route.request().headers()['authorization']
    if (!auth || auth === 'Bearer expired') {
      await route.fulfill({
        status: 401,
        contentType: 'application/json',
        body: JSON.stringify({ type: '/erros/credenciais', title: 'expirado', status: 401 }),
      })
      return
    }
    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ ok: true }) })
  })

  return instrumentation
}

test('duas abas com token expirado renovam sem derrubar a sessão (S-M10)', async ({ context, baseURL }) => {
  const { getMaxConcurrentRefresh } = instrumentMocks(context)
  await context.addCookies([{ name: 'wr_refresh', value: 'v1', url: baseURL! }])

  const pageA = await context.newPage()
  const pageB = await context.newPage()
  await pageA.goto('/e2e/fixtures/refresh-probe.html')
  await pageB.goto('/e2e/fixtures/refresh-probe.html')
  await expect(pageA.locator('#ready')).toHaveText('ready')
  await expect(pageB.locator('#ready')).toHaveText('ready')

  await Promise.all([
    pageA.evaluate(() => window.__probe('expired')),
    pageB.evaluate(() => window.__probe('expired')),
  ])

  const resultA = await pageA.locator('#result').textContent()
  const resultB = await pageB.locator('#result').textContent()

  expect(resultA).toMatch(/^ok:/)
  expect(resultB).toMatch(/^ok:/)
  // A prova real do S-M10: mesmo com a janela de corrida escancarada (atraso de
  // REFRESH_DELAY_MS acima), o lock nunca deixou 2 refresh em voo ao mesmo tempo.
  expect(getMaxConcurrentRefresh()).toBe(1)
})

test('controle negativo: sem o lock, a mesma bateria acusa a corrida (prova que o teste acima tem poder)', async ({
  context,
  baseURL,
}) => {
  const { getMaxConcurrentRefresh } = instrumentMocks(context)
  await context.addCookies([{ name: 'wr_refresh', value: 'v1', url: baseURL! }])

  // Só neste teste: substitui o lock real por um pass-through, simulando "e se o
  // navigator.locks não existisse". Roda antes de qualquer script da página.
  await context.addInitScript(() => {
    ;(window as unknown as { __lockOverrideApplied?: boolean }).__lockOverrideApplied = true
    navigator.locks.request = ((_name: string, cb: () => Promise<unknown>) => cb()) as typeof navigator.locks.request
  })

  const pageA = await context.newPage()
  const pageB = await context.newPage()
  await pageA.goto('/e2e/fixtures/refresh-probe.html')
  await pageB.goto('/e2e/fixtures/refresh-probe.html')
  await expect(pageA.locator('#ready')).toHaveText('ready')
  await expect(pageB.locator('#ready')).toHaveText('ready')

  // Confirma que o override do addInitScript realmente rodou nas duas páginas antes
  // de confiar no resultado — sem isso, um teste "verde" no positivo não prova nada.
  const overrideA = await pageA.evaluate(
    () => (window as unknown as { __lockOverrideApplied?: boolean }).__lockOverrideApplied,
  )
  const overrideB = await pageB.evaluate(
    () => (window as unknown as { __lockOverrideApplied?: boolean }).__lockOverrideApplied,
  )
  expect(overrideA).toBe(true)
  expect(overrideB).toBe(true)

  await Promise.all([
    pageA.evaluate(() => window.__probe('expired')),
    pageB.evaluate(() => window.__probe('expired')),
  ])

  // Sem serialização nenhuma, as duas abas mandam o /auth/refresh em voo ao mesmo
  // tempo de propósito (mesma janela de corrida do teste acima) — isto TEM que reprovar.
  expect(getMaxConcurrentRefresh()).toBeGreaterThan(1)
})
