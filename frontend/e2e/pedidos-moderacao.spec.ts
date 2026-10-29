import { expect, test, type Page } from '@playwright/test'
import { dadosDoLocutor, limparThrottleDePedidos } from './helpers/stack'

/**
 * DoD 1 do Épico 3, contra o stack real (hub, Worker e banco de verdade). O ouvinte A pede uma música e a tela de
 * moderação aberta pelo locutor a mostra SEM recarregar; ao aprovar e depois marcar tocado, "Meus pedidos" do ouvinte A
 * muda para "Aprovado" e "Tocado" sem recarregar. O ouvinte B (outro `listenerId`: outro contexto do browser) tem o pedido
 * rejeitado e vê o motivo, também sem recarregar. O marcador em `window` morre se a página recarregar.
 *
 * São 2 pedidos por tentativa: o limite por IP é 3 em 5 min, então as chaves `wr:throttle:pedido:*` são apagadas antes de
 * cada teste (E3-F08) para os `retries: 2` do CI não estourarem.
 *
 * Quem modera é o Locutor semeado (`locutor1@example.com`), não o Admin: assim a policy `PodeModerar`, o grupo `locutores`
 * do hub e o link "Estúdio" do papel Locutor passam pelo stack real. É um login só, o mesmo número de antes: o `/auth/login`
 * aceita 5 por minuto por IP e os outros specs de stack já usam parte do limite.
 */
async function marcarSemRecarregar(page: Page) {
  await page.evaluate(() => {
    ;(window as unknown as { __semRecarregar: boolean }).__semRecarregar = true
  })
}

async function seguiuNaMesmaPagina(page: Page) {
  return page.evaluate(
    () => (window as unknown as { __semRecarregar?: boolean }).__semRecarregar === true,
  )
}

async function pedir(page: Page, musica: string) {
  await page.getByLabel('Seu nome').fill('Ouvinte E2E')
  await page.getByLabel('Música').fill(musica)
  await page.getByLabel('Artista').fill('Artista E2E')
  await page.getByRole('button', { name: 'Enviar pedido' }).click()
  await expect(page.getByRole('status')).toContainText('Pedido enviado')
}

test('o pedido chega à moderação, e a decisão chega ao ouvinte, sem recarregar', async ({
  browser,
}) => {
  test.setTimeout(120_000)
  limparThrottleDePedidos()
  const locutorSeed = dadosDoLocutor()
  const sufixo = Date.now()
  const musicaA = `E2E aprovada ${sufixo}`
  const musicaB = `E2E rejeitada ${sufixo}`

  const locutorCtx = await browser.newContext()
  const ouvinteACtx = await browser.newContext()
  const ouvinteBCtx = await browser.newContext()
  try {
    // --- locutor: entra e deixa a fila aberta antes de qualquer pedido
    const locutor = await locutorCtx.newPage()
    await locutor.goto('/login')
    await locutor.getByLabel('E-mail').fill(locutorSeed.email)
    await locutor.getByLabel('Senha').fill(locutorSeed.senha)
    await locutor.getByRole('button', { name: 'Entrar' }).click()
    // O link "Estúdio" é público: sem esperar a sessão (o "Sair" só aparece depois do login), o clique correria na frente do
    // login e a rota guardada mandaria de volta para /login.
    await expect(locutor.getByRole('button', { name: 'Sair' })).toBeVisible()
    await locutor.getByRole('link', { name: 'Estúdio' }).click()
    await expect(locutor.getByRole('heading', { name: 'Pedidos de música' })).toBeVisible()
    await marcarSemRecarregar(locutor)

    // --- ouvinte A pede; a fila do locutor mostra sem recarregar
    const ouvinteA = await ouvinteACtx.newPage()
    await ouvinteA.goto('/pedidos')
    await marcarSemRecarregar(ouvinteA)
    await pedir(ouvinteA, musicaA)
    const itemA = ouvinteA.getByRole('listitem').filter({ hasText: musicaA })
    await expect(itemA).toContainText('Aguardando o locutor')

    const naFila = locutor.getByRole('listitem').filter({ hasText: musicaA })
    await expect(naFila).toBeVisible({ timeout: 30_000 })

    // --- aprovar e depois marcar tocado: "Meus pedidos" do ouvinte A acompanha
    await naFila.getByRole('button', { name: `Aprovar ${musicaA}` }).click()
    await expect(itemA).toContainText('Aprovado', { timeout: 30_000 })
    await locutor.getByRole('button', { name: `Marcar ${musicaA} como tocado` }).click()
    await expect(itemA).toContainText('Tocado', { timeout: 30_000 })

    // --- ouvinte B (outro listenerId) tem o pedido rejeitado e vê o motivo
    const ouvinteB = await ouvinteBCtx.newPage()
    await ouvinteB.goto('/pedidos')
    await marcarSemRecarregar(ouvinteB)
    await pedir(ouvinteB, musicaB)
    const itemB = ouvinteB.getByRole('listitem').filter({ hasText: musicaB })

    const naFilaB = locutor.getByRole('listitem').filter({ hasText: musicaB })
    await expect(naFilaB).toBeVisible({ timeout: 30_000 })
    await naFilaB.getByRole('button', { name: `Rejeitar ${musicaB}` }).click()
    await locutor.getByLabel(/Motivo da rejeição/).fill('Fora do estilo da rádio')
    await locutor.getByRole('button', { name: 'Confirmar rejeição' }).click()
    await expect(itemB).toContainText('Rejeitado', { timeout: 30_000 })
    await expect(itemB).toContainText('Motivo: Fora do estilo da rádio')

    expect(await seguiuNaMesmaPagina(locutor)).toBe(true)
    expect(await seguiuNaMesmaPagina(ouvinteA)).toBe(true)
    expect(await seguiuNaMesmaPagina(ouvinteB)).toBe(true)
  } finally {
    await locutorCtx.close()
    await ouvinteACtx.close()
    await ouvinteBCtx.close()
  }
})
