import { expect, test } from '@playwright/test'

/**
 * E3-P01/P02 no preview, com a API mockada por rota: o formulário envia o pedido com o `X-Listener-Id`, o pedido aparece
 * em "Meus pedidos", e o 429 vira contagem regressiva a partir do `retryAfterSeconds`. O fluxo com o hub de verdade
 * (moderação ao vivo) é o `pedidos-moderacao.spec.ts`, que só roda no stack.
 */
const PEDIDO = {
  id: '6f1c2d3e-0000-4000-8000-000000000001',
  nomeOuvinte: 'Ana',
  tituloMusica: 'Asa Branca',
  artista: 'Luiz Gonzaga',
  mensagem: null,
  status: 'Pendente',
  criadoEmUtc: '2026-10-29T15:00:00Z',
  moderadoEmUtc: null,
  motivoRejeicao: null,
  tocadoEmUtc: null,
  programaId: null,
}

test('o ouvinte pede uma música e o pedido aparece em Meus pedidos', async ({ page }) => {
  const feitos: object[] = []
  let listenerId: string | null = null

  await page.route('**/api/v1/pedidos/meus', (rota) => rota.fulfill({ json: feitos }))
  await page.route('**/api/v1/pedidos', async (rota) => {
    listenerId = rota.request().headers()['x-listener-id'] ?? null
    feitos.push(PEDIDO)
    await rota.fulfill({ status: 201, json: PEDIDO })
  })

  await page.goto('/pedidos')
  await expect(page.getByText('Você ainda não fez nenhum pedido.')).toBeVisible()

  await page.getByLabel('Seu nome', { exact: true }).fill('Ana')
  await page.getByLabel('Música', { exact: true }).fill('Asa Branca')
  await page.getByLabel('Artista', { exact: true }).fill('Luiz Gonzaga')
  await page.getByRole('button', { name: 'Enviar pedido' }).click()

  await expect(page.getByRole('status')).toContainText('Pedido enviado')
  await expect(page.getByRole('listitem').filter({ hasText: 'Asa Branca' })).toContainText(
    'Aguardando o locutor',
  )
  expect(listenerId).toMatch(/^[0-9a-f-]{36}$/)
})

test('429: o botão mostra a contagem regressiva e volta quando ela zera', async ({ page }) => {
  await page.route('**/api/v1/pedidos/meus', (rota) => rota.fulfill({ json: [] }))
  await page.route('**/api/v1/pedidos', (rota) =>
    rota.fulfill({
      status: 429,
      headers: { 'Retry-After': '3' },
      json: { type: '/erros/muitos-pedidos', title: 'x', status: 429, retryAfterSeconds: 3 },
    }),
  )

  await page.goto('/pedidos')
  await page.getByLabel('Seu nome', { exact: true }).fill('Ana')
  await page.getByLabel('Música', { exact: true }).fill('Asa Branca')
  await page.getByLabel('Artista', { exact: true }).fill('Luiz Gonzaga')
  await page.getByRole('button', { name: 'Enviar pedido' }).click()

  await expect(
    page.getByRole('button', { name: /Aguarde 0:0\d para pedir de novo/ }),
  ).toBeDisabled()
  await expect(page.getByRole('button', { name: 'Enviar pedido' })).toBeEnabled({ timeout: 8_000 })
  // O que o ouvinte digitou continua lá: ele só reenvia.
  await expect(page.getByLabel('Seu nome', { exact: true })).toHaveValue('Ana')
})

test('o contador usa string.length: um emoji vale 2 unidades', async ({ page }) => {
  await page.route('**/api/v1/pedidos/meus', (rota) => rota.fulfill({ json: [] }))
  await page.goto('/pedidos')

  await page.getByLabel('Seu nome', { exact: true }).fill('Ana🎵')

  await expect(page.getByText('5/60')).toBeVisible()
})
