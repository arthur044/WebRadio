import { expect, test } from '@playwright/test'
import {
  cancelarPrograma,
  criarProgramaAgendado,
  criarProgramaAgora,
  encerrarPrograma,
} from './helpers/stack'

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

/** Segunda-feira (YYYY-MM-DD) da semana de Brasília (UTC-3 fixo) que contém o instante. */
function segundaDeBrasilia(instante: Date): string {
  const b = new Date(instante.getTime() - 3 * 3_600_000)
  b.setUTCDate(b.getUTCDate() - ((b.getUTCDay() + 6) % 7))
  return b.toISOString().slice(0, 10)
}

/**
 * E2-P01 (DoD 1 do Épico 2), o lado do cancelar: um programa `Agendado` aparece na grade pública e, ao ser
 * cancelado pela API, sai dela pelo hub (a grade pública não lista cancelados) SEM recarregar a página.
 */
test('o programa agendado some da grade pública quando é cancelado, sem recarregar', async ({
  page,
  request,
}) => {
  test.setTimeout(60_000)
  const titulo = `E2E cancelar ${Date.now()}`
  let programaId: string | null = null

  try {
    // Criado antes de abrir a página; começa daqui a 2 h, então o Worker não o inicia durante o teste.
    const criado = await criarProgramaAgendado(request, titulo)
    programaId = criado.id

    // A semana do início pode não ser a atual (domingo à noite): abre a certa, que é a que o hub recarrega.
    await page.goto(`/grade?semana=${segundaDeBrasilia(criado.inicio)}`)
    const linha = page.getByRole('listitem').filter({ hasText: titulo })
    await expect(linha).toBeVisible()
    await page.evaluate(() => {
      ;(window as unknown as { __semRecarregar: boolean }).__semRecarregar = true
    })

    await cancelarPrograma(request, programaId)

    await expect(linha).toBeHidden({ timeout: 30_000 })
    const mesmaPagina = await page.evaluate(
      () => (window as unknown as { __semRecarregar?: boolean }).__semRecarregar === true,
    )
    expect(mesmaPagina).toBe(true)
  } finally {
    // Idempotente: se o teste falhou antes do cancelar, não deixa o programa ocupando a janela.
    if (programaId) await cancelarPrograma(request, programaId)
  }
})
