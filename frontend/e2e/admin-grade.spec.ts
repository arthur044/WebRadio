import { expect, test } from '@playwright/test'
import {
  chaveDataBrasilia,
  chaveSegundaDaSemanaBrasilia,
  somarDias,
} from '../src/shared/lib/timezone'
import { dadosDoAdmin } from './helpers/stack'

/**
 * E2-P02 contra o backend real (ETag/If-Match, UTC, 409 com programaConflitanteId): o Admin entra pela tela,
 * cria um programa amanhã de madrugada (fora das janelas do seed e do spec do ao vivo), edita, vê o 409 dizer
 * QUEM conflita e cancela. No fim nada fica Agendado no stack.
 */
test('Admin cria, edita (If-Match), vê o conflito de horário e cancela um programa pela tela', async ({
  page,
}, testInfo) => {
  test.setTimeout(120_000)
  const admin = dadosDoAdmin()
  const titulo = `E2E grade ${Date.now()}`
  const tituloEditado = `${titulo} (editado)`
  const amanha = somarDias(chaveDataBrasilia(new Date().toISOString()), 1)
  // Um retry usa outro horário: a tentativa anterior pode ter deixado um programa Agendado.
  const hora = String(3 + testInfo.retry * 2).padStart(2, '0')
  const horaSeguinte = String(Number(hora) + 1).padStart(2, '0')

  await page.goto('/login')
  await page.getByLabel('E-mail').fill(admin.email)
  await page.getByLabel('Senha').fill(admin.senha)
  await page.getByRole('button', { name: 'Entrar' }).click()
  // Navegação dentro do app (sem recarregar): a sessão vive só em memória.
  await page.getByRole('link', { name: 'Admin', exact: true }).click()
  await expect(page.getByRole('heading', { name: 'Administrar grade' })).toBeVisible()

  // Amanhã pode cair na semana seguinte (hoje é domingo): vai para a semana certa antes de criar.
  const amanhaMeioDia = new Date(`${amanha}T15:00:00Z`)
  if (chaveSegundaDaSemanaBrasilia(amanhaMeioDia) !== chaveSegundaDaSemanaBrasilia(new Date())) {
    await page.getByRole('button', { name: 'Próxima semana' }).click()
  }

  async function abrirNovo(inicio: string, fim: string, titulo: string) {
    await page.getByRole('button', { name: 'Novo programa' }).click()
    const form = page.getByRole('form', { name: 'Novo programa' })
    await form.getByLabel('Título').fill(titulo)
    await form.getByLabel('Locutor').selectOption({ index: 1 })
    await form.getByLabel(/^Início/).fill(inicio)
    await form.getByLabel(/^Fim/).fill(fim)
    return form
  }

  // --- criar
  let form = await abrirNovo(`${amanha}T${hora}:00`, `${amanha}T${horaSeguinte}:00`, titulo)
  await form.getByRole('button', { name: 'Criar programa' }).click()
  await expect(page.getByRole('status')).toHaveText('Programa criado.')
  const linha = page.getByRole('listitem').filter({ hasText: titulo })
  await expect(linha).toBeVisible()
  await expect(linha).toContainText(`${hora}:00`)

  // --- conflito: meia hora dentro do programa recém-criado; o 409 tem que nomear o programa
  form = await abrirNovo(
    `${amanha}T${hora}:30`,
    `${amanha}T${horaSeguinte}:30`,
    `${titulo} conflitante`,
  )
  await form.getByRole('button', { name: 'Criar programa' }).click()
  await expect(form.getByRole('alert')).toContainText('já está ocupado')
  await expect(form.getByTestId('conflito-grade')).toContainText(titulo)
  await form.getByRole('button', { name: 'Descartar' }).click()

  // --- editar: o PUT leva o ETag lido no GET do programa
  await linha.getByRole('button', { name: `Editar ${titulo}` }).click()
  const edicao = page.getByRole('form', { name: 'Editar programa' })
  await expect(edicao.getByLabel('Título')).toHaveValue(titulo)
  await edicao.getByLabel('Título').fill(tituloEditado)
  const put = page.waitForRequest(
    (r) => r.method() === 'PUT' && r.url().includes('/api/v1/programas/'),
  )
  await edicao.getByRole('button', { name: 'Salvar alterações' }).click()
  expect((await put).headers()['if-match']).toMatch(/^"[A-Za-z0-9+/]+=*"$/)
  await expect(page.getByRole('status')).toHaveText('Programa atualizado.')
  const linhaEditada = page.getByRole('listitem').filter({ hasText: tituloEditado })
  await expect(linhaEditada).toBeVisible()

  // --- cancelar
  await linhaEditada.getByRole('button', { name: `Cancelar ${tituloEditado}` }).click()
  await page.getByRole('button', { name: 'Confirmar cancelamento' }).click()
  await expect(page.getByRole('status')).toHaveText('Programa cancelado.')
  await expect(linhaEditada).toContainText('Cancelado')
})
