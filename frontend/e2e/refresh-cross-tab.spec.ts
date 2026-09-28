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
 * Guardian, rodada 3 (cenário c): o timeout de qualquer uma das barreiras acima
 * esconde a AUSÊNCIA de sobreposição, não só a presença de lock. Se a aba B chegar
 * bem depois de ALIGN_TIMEOUT_MS (rede lenta, máquina sob carga, um teste mal
 * escrito), as duas barreiras liberam por timeout, A completa sozinha, B repete o
 * mesmo caminho sozinha depois, e `max === 1` sai idêntico ao caso "lock funcionou" —
 * mesmo SEM lock nenhum. Por isso cada barreira agora registra `releasedBy`: só conta
 * como prova quando o probe foi liberado por 'arrival' (a largada realmente alinhou
 * as duas abas nesta execução). Combinado com isso, o refresh também precisa liberar
 * pelo motivo certo: 'timeout' com lock (prova que a 2ª nunca chegou enquanto a 1ª
 * estava presa) e 'arrival' sem lock (prova que as duas se sobrepuseram de verdade).
 */
const ALIGN_TIMEOUT_MS = 5000

type ReleasedBy = 'arrival' | 'timeout' | null

function alignedBarrier(timeoutMs: number) {
  let arrivals = 0
  let decided: ReleasedBy = null
  let release: (() => void) | null = null
  const gate = new Promise<void>((resolve) => {
    release = resolve
  })

  return {
    /** Chame na chegada de cada requisição; resolve quando a 2ª chega OU o timeout estoura. */
    async arrive(): Promise<void> {
      arrivals += 1
      if (arrivals >= 2) {
        // Bug corrigido aqui (rodada 3, 1ª tentativa): sem chamar `release()`, o 1º
        // chamador nunca era acordado pela 2ª chegada — ficava preso esperando o
        // `timeoutMs` INTEIRO sempre, mesmo com `decided` já gravado como 'arrival'.
        // Isso criava uma corrida artificial bem na borda do timeout entre quem
        // "esperou o próprio timeout" (1º) e quem "não esperou nada" (2º) — daí a
        // falha quase determinística no controle negativo, não era jitter de host.
        decided ??= 'arrival'
        release?.()
        return
      }
      await Promise.race([gate, new Promise<void>((resolve) => setTimeout(resolve, timeoutMs))])
      decided ??= 'timeout'
    },
    /**
     * Guardian, rodada 3: qual dos dois motivos liberou a PRIMEIRA chamada presa.
     * `null` só acontece se `arrive()` nunca terminou (impossível aqui — o timeout
     * sempre dispara). `'timeout'` sem uma 2ª chegada real é exatamente o cenário (c):
     * a largada não alinhou nesta execução, e o resultado não prova nada.
     */
    get releasedBy(): ReleasedBy {
      return decided
    },
  }
}

function instrumentMocks(context: BrowserContext) {
  let currentRefreshCookie = 'v1'
  const usedCookies = new Set<string>()
  let refreshInFlight = 0
  let maxConcurrentRefresh = 0
  let rotationCounter = 1

  const probeBarrier = alignedBarrier(ALIGN_TIMEOUT_MS)
  const refreshBarrier = alignedBarrier(ALIGN_TIMEOUT_MS)

  const instrumentation = {
    getMaxConcurrentRefresh: () => maxConcurrentRefresh,
    getProbeReleasedBy: () => probeBarrier.releasedBy,
    getRefreshReleasedBy: () => refreshBarrier.releasedBy,
  }

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
  const { getMaxConcurrentRefresh, getProbeReleasedBy, getRefreshReleasedBy } =
    instrumentMocks(context)
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
  // Guardian, rodada 3: a largada precisa ter alinhado de verdade nesta execução —
  // senão o max===1 abaixo não prova nada (cenário c).
  expect(getProbeReleasedBy()).toBe('arrival')
  // E o refresh só pode ter liberado por TIMEOUT: se tivesse liberado por 'arrival',
  // seria porque a 2ª aba chegou enquanto a 1ª ainda estava presa — ou seja, o lock
  // falhou em serializar as duas.
  expect(getRefreshReleasedBy()).toBe('timeout')
  // A prova real do S-M10: mesmo com a largada alinhada, o lock nunca deixou 2
  // refresh em voo ao mesmo tempo.
  expect(getMaxConcurrentRefresh()).toBe(1)
})

test('controle negativo: sem o lock, a mesma bateria acusa a corrida (prova que o teste acima tem poder)', async ({
  context,
  baseURL,
}) => {
  test.setTimeout(60_000)
  const { getMaxConcurrentRefresh, getProbeReleasedBy, getRefreshReleasedBy } =
    instrumentMocks(context)
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

  // Guardian, rodada 3: a largada precisa ter alinhado de verdade — senão o
  // max>1 abaixo poderia ser só sorte de outra corrida, não prova do controle.
  expect(getProbeReleasedBy()).toBe('arrival')
  // Sem lock, a 2ª /auth/refresh TEM que chegar enquanto a 1ª ainda está presa —
  // 'timeout' aqui seria o cenário (c): nenhuma sobreposição de verdade aconteceu.
  expect(getRefreshReleasedBy()).toBe('arrival')
  // Sem serialização nenhuma, a barreira (a) alinha as duas chegadas ao probe e a
  // barreira (b) alinha as duas chegadas ao refresh — então as duas SEMPRE ficam em
  // voo ao mesmo tempo aqui, não por sorte de timing. Isto TEM que reprovar.
  expect(getMaxConcurrentRefresh()).toBeGreaterThan(1)
})

test('cenário (c) do Guardian: sem lock, mas a aba B chega bem depois do timeout — a barreira acusa a falta de sobreposição em vez de fingir um lock', async ({
  context,
  baseURL,
}) => {
  test.setTimeout(60_000)
  const { getMaxConcurrentRefresh, getProbeReleasedBy, getRefreshReleasedBy } =
    instrumentMocks(context)
  await context.addCookies([{ name: 'wr_refresh', value: 'v1', url: baseURL! }])

  // Mesmo override do controle negativo: sem lock nenhum.
  await context.addInitScript(() => {
    navigator.locks.request = ((_name: string, cb: () => Promise<unknown>) =>
      cb()) as typeof navigator.locks.request
  })

  const pageA = await context.newPage()
  const pageB = await context.newPage()
  await pageA.goto('/e2e/fixtures/refresh-probe.html')
  await pageB.goto('/e2e/fixtures/refresh-probe.html')
  await expect(pageA.locator('#ready')).toHaveText('ready', { timeout: 20_000 })
  await expect(pageB.locator('#ready')).toHaveText('ready', { timeout: 20_000 })

  // A prova do Guardian: A dispara e B só chama o probe 12s depois — bem além dos
  // ALIGN_TIMEOUT_MS (5s) das duas barreiras. Sem a checagem de releasedBy, isto
  // produzia max===1 IDÊNTICO ao caso "o lock funcionou", mesmo sem lock nenhum: as
  // duas barreiras liberam por timeout, B chega bem depois com o cookie já rotacionado
  // por A, e as duas terminam "ok".
  const B_DELAY_MS = 12_000
  const aDone = pageA.evaluate(() => window.__probe('expired'))
  await new Promise((resolve) => setTimeout(resolve, B_DELAY_MS))
  const bDone = pageB.evaluate(() => window.__probe('expired'))
  await Promise.all([aDone, bDone])

  // A largada NÃO alinhou nesta execução — a única coisa que ALIGN_TIMEOUT_MS provê
  // aqui é uma rede de segurança pra não travar o teste pra sempre, não uma prova de
  // corrida. É exatamente essa distinção que faltava no cenário (c).
  expect(getProbeReleasedBy()).toBe('timeout')
  expect(getRefreshReleasedBy()).toBe('timeout')
  // Documenta o que o Guardian mediu: sem lock e sem sobreposição real, max fica em 1
  // — igual ao teste com lock. É por isso que os dois testes de cima agora exigem
  // releasedBy === 'arrival' na barreira do probe: sem essa checagem, essa mesma
  // execução teria "provado" o controle negativo (ou mascarado uma falha do lock) por
  // motivo nenhum.
  expect(getMaxConcurrentRefresh()).toBe(1)
})
