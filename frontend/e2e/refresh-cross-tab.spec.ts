import { expect, test } from '@playwright/test'

declare global {
  interface Window {
    __probe: (expiredToken: string) => Promise<void>
  }
}

test('duas abas com token expirado renovam sem derrubar a sessão (S-M10)', async ({
  context,
  baseURL,
}) => {
  let currentRefreshCookie = 'v1'
  let refreshCallCount = 0
  const usedCookies = new Set<string>()

  await context.route('**/api/v1/auth/refresh', async (route) => {
    refreshCallCount += 1
    const cookieHeader = route.request().headers()['cookie'] ?? ''
    const presented = /wr_refresh=([^;]+)/.exec(cookieHeader)?.[1]

    if (presented !== currentRefreshCookie || usedCookies.has(presented ?? '')) {
      // Cookie errado ou já consumido: simula a detecção de reuso revogando a família.
      await route.fulfill({
        status: 401,
        contentType: 'application/json',
        body: JSON.stringify({ type: '/erros/credenciais', title: 'reuso detectado', status: 401 }),
      })
      return
    }

    usedCookies.add(presented)
    currentRefreshCookie = `v${refreshCallCount + 1}`
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
  })

  await context.route('**/api/v1/probe/protegido', async (route) => {
    const auth = route.request().headers()['authorization']
    if (!auth || auth === 'Bearer expired') {
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
  // Cada aba faz o próprio refresh (memória isolada), mas o lock serializa: nenhuma
  // reusa um cookie já consumido, então nenhuma cai no 401 de "reuso detectado".
  expect(refreshCallCount).toBe(2)
})
