import { expect, test, type BrowserContext } from '@playwright/test'

declare global {
  interface Window {
    __probe: (expiredToken: string) => Promise<void>
  }
}

/**
 * Guardian, rodada 2: um atraso fixo no /auth/refresh não prova nada de forma
 * confiável — numa execução rápida as duas abas nunca se cruzam dentro da janela
 * (o controle negativo então "passa" mesmo sem lock), e numa máquina lenta ou runner
 * compartilhado o atraso não é largo o bastante. Troca por duas barreiras
 * determinísticas, sem depender de velocidade:
 *
 * (a) largada alinhada no /probe/protegido: cada 401 fica preso até a 2ª aba também
 *     ter pedido o probe (ou até ALIGN_TIMEOUT_MS, caso uma das abas nunca chegue —
 *     rede quebrada, teste indo falhar de outro jeito). As duas abas disparam o
 *     refresh quase no mesmo instante, com ou sem lock.
 * (b) a 1ª /auth/refresh fica presa até a 2ª chegar OU até ALIGN_TIMEOUT_MS:
 *     - SEM lock, a 2ª chega quase junto (por causa da barreira (a)) e libera as
 *       duas imediatamente → max concorrente = 2, garantido, não por sorte de timing.
 *     - COM lock, a 2ª nunca chega enquanto a 1ª está presa (a aba B fica esperando o
 *       navigator.locks do browser, e só chama fetch depois que a 1ª lock é liberada,
 *       ou seja, depois que a 1ª /auth/refresh já respondeu). A 1ª só é liberada pelo
 *       timeout → max concorrente = 1, e o positivo prova o lock de verdade.
 *
 * `maxConcurrentRefresh` continua sendo o que prova se duas chamadas ficaram em voo
 * ao mesmo tempo — só a forma de abrir a janela de corrida mudou.
 */
const ALIGN_TIMEOUT_MS = 5000

function alignedBarrier(timeoutMs: number) {
  let arrivals = 0
  let release: (() => void) | null = null
  const gate = new Promise<void>((resolve) => {
    release = resolve
  })

  return {
    /** Chame na chegada de cada requisição; resolve quando a 2ª chega OU o timeout estoura. */
    async arrive(): Promise<void> {
      arrivals += 1
      if (arrivals >= 2) {
        release?.()
        return
      }
      await Promise.race([gate, new Promise((resolve) => setTimeout(resolve, timeoutMs))])
    },
  }
}

function instrumentMocks(context: BrowserContext) {
  let currentRefreshCookie = 'v1'
  const usedCookies = new Set<string>()
  let refreshInFlight = 0
  let maxConcurrentRefresh = 0
  let rotationCounter = 1

  const instrumentation = {
    getMaxConcurrentRefresh: () => maxConcurrentRefresh,
  }

  const probeBarrier = alignedBarrier(ALIGN_TIMEOUT_MS)
  const refreshBarrier = alignedBarrier(ALIGN_TIMEOUT_MS)

  void context.route('**/api/v1/auth/refresh', async (route) => {
    refreshInFlight += 1
    maxConcurrentRefresh = Math.max(maxConcurrentRefresh, refreshInFlight)

    await refreshBarrier.arrive()

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
        usuario: {
          id: '1',
          nome: 'Ouvinte Teste',
          email: 't@t.com',
          role: 'Ouvinte',
          deveTrocarSenha: false,
        },
      }),
    })
    refreshInFlight -= 1
  })

  void context.route('**/api/v1/probe/protegido', async (route) => {
    const auth = route.request().headers()['authorization']
    if (!auth || auth === 'Bearer expired') {
      await probeBarrier.arrive()
      await route.fulfill({
        status: 401,
        contentType: 'application/json',
        body: JSON.stringify({ type: '/erros/credenciais', title: 'expirado', status: 401 }),
      })
      return
    }
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ ok: true }),
    })
  })

  return instrumentation
}

test('duas abas com token expirado renovam sem derrubar a sessão (S-M10)', async ({
  context,
  baseURL,
}) => {
  // Com lock, este teste SEMPRE espera o ALIGN_TIMEOUT_MS inteiro na barreira do
  // refresh (a 2ª aba nunca chega enquanto a 1ª está presa) — soma com a barreira do
  // probe e a carga de 2 páginas, então o teste padrão de 30s fica curto demais numa
  // máquina sob pressão de memória.
  test.setTimeout(60_000)
  const { getMaxConcurrentRefresh } = instrumentMocks(context)
  await context.addCookies([{ name: 'wr_refresh', value: 'v1', url: baseURL! }])

  const pageA = await context.newPage()
  const pageB = await context.newPage()
  await pageA.goto('/e2e/fixtures/refresh-probe.html')
  await pageB.goto('/e2e/fixtures/refresh-probe.html')
  await expect(pageA.locator('#ready')).toHaveText('ready', { timeout: 20_000 })
  await expect(pageB.locator('#ready')).toHaveText('ready', { timeout: 20_000 })

  await Promise.all([
    pageA.evaluate(() => window.__probe('expired')),
    pageB.evaluate(() => window.__probe('expired')),
  ])

  const resultA = await pageA.locator('#result').textContent()
  const resultB = await pageB.locator('#result').textContent()

  expect(resultA).toMatch(/^ok:/)
  expect(resultB).toMatch(/^ok:/)
  // A prova real do S-M10: mesmo com a largada alinhada (barreira acima), o lock
  // nunca deixou 2 refresh em voo ao mesmo tempo.
  expect(getMaxConcurrentRefresh()).toBe(1)
})

test('controle negativo: sem o lock, a mesma bateria acusa a corrida (prova que o teste acima tem poder)', async ({
  context,
  baseURL,
}) => {
  test.setTimeout(60_000)
  const { getMaxConcurrentRefresh } = instrumentMocks(context)
  await context.addCookies([{ name: 'wr_refresh', value: 'v1', url: baseURL! }])

  // Só neste teste: substitui o lock real por um pass-through, simulando "e se o
  // navigator.locks não existisse". Roda antes de qualquer script da página.
  await context.addInitScript(() => {
    ;(window as unknown as { __lockOverrideApplied?: boolean }).__lockOverrideApplied = true
    navigator.locks.request = ((_name: string, cb: () => Promise<unknown>) =>
      cb()) as typeof navigator.locks.request
  })

  const pageA = await context.newPage()
  const pageB = await context.newPage()
  await pageA.goto('/e2e/fixtures/refresh-probe.html')
  await pageB.goto('/e2e/fixtures/refresh-probe.html')
  await expect(pageA.locator('#ready')).toHaveText('ready', { timeout: 20_000 })
  await expect(pageB.locator('#ready')).toHaveText('ready', { timeout: 20_000 })

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

  // Sem serialização nenhuma, a barreira (a) alinha as duas chegadas ao probe e a
  // barreira (b) alinha as duas chegadas ao refresh — então as duas SEMPRE ficam em
  // voo ao mesmo tempo aqui, não por sorte de timing. Isto TEM que reprovar.
  expect(getMaxConcurrentRefresh()).toBeGreaterThan(1)
})
