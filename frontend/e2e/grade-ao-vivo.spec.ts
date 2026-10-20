import { expect, test } from '@playwright/test'
import { criarProgramaAgora, encerrarPrograma } from './helpers/stack'

/**
 * E2-P01 (DoD 1 do Épico 2), contra o stack real: com a grade pública aberta, um programa cuja janela
 * começou vira "Ao vivo" pelo Worker + hub e o selo aparece SEM recarregar a página; ao encerrar, o selo
 * some do mesmo jeito. O marcador em `window` morre se a página recarregar: prova o "sem refresh".
 */
test('o selo Ao Vivo aparece quando o Worker inicia o programa e some ao encerrar, sem recarregar', async ({
  page,
  request,
}) => {
  test.setTimeout(120_000)
  const titulo = `E2E ao vivo ${Date.now()}`
  let programaId: string | null = null

  try {
    await page.goto('/grade')
    await expect(page.getByRole('heading', { name: 'Grade' })).toBeVisible()
    await page.evaluate(() => {
      ;(window as unknown as { __semRecarregar: boolean }).__semRecarregar = true
    })

    programaId = await criarProgramaAgora(request, titulo)

    // O Worker roda a cada 10 s; sobra folga para o evento e a releitura da grade.
    const linha = page.getByRole('listitem').filter({ hasText: titulo })
    await expect(linha.getByTestId('selo-ao-vivo')).toBeVisible({ timeout: 45_000 })

    await encerrarPrograma(request, programaId)
    await expect(linha.getByTestId('selo-ao-vivo')).toBeHidden({ timeout: 30_000 })
    // O programa continua na grade (Finalizado), só o selo saiu.
    await expect(linha).toBeVisible()

    const mesmaPagina = await page.evaluate(
      () => (window as unknown as { __semRecarregar?: boolean }).__semRecarregar === true,
    )
    expect(mesmaPagina).toBe(true)
  } finally {
    // Não deixa um programa ao vivo para os próximos specs (nem para o stack de quem roda localmente).
    if (programaId) await encerrarPrograma(request, programaId)
  }
})
