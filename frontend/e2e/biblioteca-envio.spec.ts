import { readFileSync } from 'node:fs'
import { expect, test, type Page } from '@playwright/test'
import { dadosDoAdmin, dadosDoLocutor } from './helpers/stack'

/**
 * DoD 1 do Épico 4, contra o stack real (nginx, API, MinIO, Worker, sanitizer e banco de verdade): um Locutor envia um
 * MP3 pela tela da biblioteca (policy da API → `POST` multipart ao `/storage/quarentena` pelo nginx → `concluir`), vê o
 * status passar de "Na fila de análise" para "Aprovada" **sem recarregar** (`MidiaSanitizada` pelo hub) e a pré-escuta
 * toca: `GET /midias/{id}/preview?formato=url` → 200 com a URL → o `<audio>` recebe `audio/mpeg` do `/storage/midia/`
 * (contratos do `02` §6). O marcador em `window` morre se a página recarregar.
 *
 * Só roda quando o backend do D2 existe: sem as rotas de `/midias` (404 na sondagem), o teste é pulado com o motivo no
 * relatório. Depois que o D2 entra na main a sondagem dá 200 e o teste passa a valer sozinho, sem mexer aqui.
 *
 * O `<audio>` pede o arquivo com `Range`, e o storage responde 206 a isso; a spec do DoD diz "200", e aqui aceita-se 200
 * ou 206, o que importa é o `audio/mpeg` vindo de `/storage/midia/` pela mesma origem. A mídia fica na biblioteca de
 * propósito: o DoD 2 (pedido tocado pelo playout) liga o pedido a ela.
 */
const MP3 = readFileSync('e2e/fixtures/radio.mp3')

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

test.describe('envio de mídia (E4-P01, E4-P02, DoD 1)', () => {
  test.beforeEach(async ({ request }) => {
    // Sondagem sem efeito colateral: a listagem com o token do Admin semeado. Só o 404 (rota inexistente) pula; 401, 403
    // ou 5xx são falha de verdade e não podem sumir atrás de um skip.
    const sondagem = await request.get('/api/v1/midias?pagina=1&tamanho=1', {
      headers: { Authorization: `Bearer ${dadosDoAdmin().token}` },
    })
    test.skip(
      sondagem.status() === 404,
      'O backend do D2 (GET /midias) ainda não está neste stack; o DoD 1 espera o D2 na main.',
    )
    expect(sondagem.status(), 'GET /midias na sondagem').toBe(200)
  })

  test('o Locutor envia um MP3, o status chega a Aprovada sem recarregar e a pré-escuta toca', async ({
    page,
  }) => {
    test.setTimeout(240_000)
    const locutor = dadosDoLocutor()
    const titulo = `E2E envio ${Date.now()}`

    // --- login do Locutor semeado (um login só, como no pedidos-moderacao: o /auth/login aceita 5 por minuto por IP)
    await page.goto('/login')
    await page.getByLabel('E-mail').fill(locutor.email)
    await page.getByLabel('Senha').fill(locutor.senha)
    await page.getByRole('button', { name: 'Entrar' }).click()
    // Sem esperar a sessão, o clique no menu correria na frente do login e a rota guardada voltaria para /login.
    await expect(page.getByRole('button', { name: 'Sair' })).toBeVisible()
    await page.getByRole('link', { name: 'Biblioteca' }).click()
    await expect(page.getByRole('heading', { name: 'Enviar áudio' })).toBeVisible()
    await marcarSemRecarregar(page)

    // --- envio: policy pela API, arquivo ao storage pelo nginx (mesma origem), concluir
    const policy = page.waitForResponse(
      (r) => r.url().endsWith('/api/v1/midias/uploads') && r.request().method() === 'POST',
    )
    const aoStorage = page.waitForResponse(
      (r) => new URL(r.url()).pathname === '/storage/quarentena' && r.request().method() === 'POST',
    )
    const concluir = page.waitForResponse(
      (r) => /\/api\/v1\/midias\/[^/]+\/concluir$/.test(r.url()) && r.request().method() === 'POST',
    )

    const envio = page.getByRole('region', { name: 'Enviar áudio' })
    await envio.getByLabel(/^Título/).fill(titulo)
    await envio.getByLabel('Artista', { exact: true }).fill('Artista E2E')
    await envio
      .getByLabel(/^Arquivo/)
      .setInputFiles({ name: 'e2e-envio.mp3', mimeType: 'audio/mpeg', buffer: MP3 })
    await envio.getByRole('button', { name: 'Enviar' }).click()

    const respostaDaPolicy = await policy
    expect(respostaDaPolicy.status()).toBe(201)
    const { midiaId, upload } = (await respostaDaPolicy.json()) as {
      midiaId: string
      upload: { url: string }
    }
    expect(new URL(upload.url).origin).toBe(new URL(page.url()).origin)
    expect((await aoStorage).status()).toBe(201)
    expect((await concluir).status()).toBe(202)

    // --- o status muda sem recarregar: Pendente (ou Em análise) e depois Aprovada, pelo evento MidiaSanitizada
    const item = page.getByRole('listitem').filter({ hasText: titulo })
    await expect(item).toContainText(/Na fila de análise|Em análise/, { timeout: 30_000 })
    await expect(item).toContainText('Aprovada', { timeout: 150_000 })

    // --- pré-escuta: a API devolve a URL em JSON e o <audio> recebe audio/mpeg do /storage/midia/
    const previewApi = page.waitForResponse(
      (r) =>
        r.url().includes(`/api/v1/midias/${midiaId}/preview?formato=url`) &&
        r.request().method() === 'GET',
    )
    const audioDoStorage = page.waitForResponse((r) =>
      new URL(r.url()).pathname.startsWith('/storage/midia/'),
    )
    await item.getByRole('button', { name: `Pré-escutar ${titulo}` }).click()

    const respostaDoPreview = await previewApi
    expect(respostaDoPreview.status()).toBe(200)
    const { url } = (await respostaDoPreview.json()) as { url: string }
    expect(new URL(url, page.url()).origin).toBe(new URL(page.url()).origin)

    const respostaDoAudio = await audioDoStorage
    expect([200, 206]).toContain(respostaDoAudio.status())
    expect(respostaDoAudio.headers()['content-type']).toContain('audio/mpeg')

    // O elemento da tela chegou a decodificar o arquivo (metadados lidos, sem erro de mídia).
    const audio = page.getByLabel(`Pré-escuta de ${titulo}`)
    await expect
      .poll(() => audio.evaluate((el: HTMLAudioElement) => el.readyState), { timeout: 15_000 })
      .toBeGreaterThanOrEqual(1)
    expect(await audio.evaluate((el: HTMLAudioElement) => el.error === null)).toBe(true)
    // O player global da rádio segue sendo outro elemento, sem a URL da pré-escuta.
    await expect(page.getByTestId('radio-audio')).not.toHaveAttribute('src', /storage\/midia/)

    expect(await seguiuNaMesmaPagina(page)).toBe(true)
  })
})
