import { readFileSync } from 'node:fs'
import { expect, test, type Page } from '@playwright/test'

/**
 * E4-P01 a E4-P03 no preview, com a API mockada por rota: pré-escuta com `<audio>` próprio, envio com a policy e a fila
 * (429 com contagem regressiva) e o 412 da edição. O envio de verdade (o status mudando de Pendente para Aprovado sem
 * recarregar) é o do compose-smoke, contra o stack (DoD 1 do Épico 4).
 */
const MP3 = readFileSync('e2e/fixtures/radio.mp3')

const ADMIN = {
  id: '11111111-1111-4111-8111-111111111111',
  nome: 'Ada Admin',
  email: 'ada@example.com',
  role: 'Admin',
  deveTrocarSenha: false,
}

const MIDIA = {
  id: 'aaaaaaaa-0000-4000-8000-000000000001',
  nomeOriginal: 'aquarela.mp3',
  titulo: 'Aquarela',
  artista: 'Toquinho',
  tipoMidia: 'Musica',
  mimeType: 'audio/mpeg',
  tamanhoBytes: 3_000_000,
  duracaoSegundos: 185,
  statusSanitizacao: 'Aprovado',
  motivoRejeicao: null,
  dataUploadUtc: '2026-11-08T15:00:00Z',
  ativo: true,
  enviadoPorNome: 'Ada Admin',
}

async function entrarComoAdmin(page: Page) {
  await page.route('**/api/v1/auth/refresh', (rota) =>
    rota.fulfill({
      json: {
        accessToken: 'token-de-teste',
        expiraEmUtc: '2099-01-01T00:00:00Z',
        deveTrocarSenha: false,
        usuario: ADMIN,
      },
    }),
  )
}

test('a pré-escuta toca num <audio> próprio da tela, com a URL pré-assinada da mesma origem', async ({
  page,
}) => {
  let pediuAudio = false
  await entrarComoAdmin(page)
  await page.route(/\/api\/v1\/midias\?/, (rota) =>
    rota.fulfill({ json: { itens: [MIDIA], total: 1, pagina: 1, tamanho: 20 } }),
  )
  await page.route(/\/api\/v1\/midias\/[^/]+\/preview\?formato=url/, (rota) =>
    rota.fulfill({
      json: {
        url: `/storage/midia/2026/${MIDIA.id}.mp3?X-Amz-Signature=abc`,
        expiraEmUtc: '2099-01-01T00:00:00Z',
      },
    }),
  )
  await page.route('**/storage/midia/**', (rota) => {
    pediuAudio = true
    return rota.fulfill({ status: 200, contentType: 'audio/mpeg', body: MP3 })
  })

  await page.goto('/biblioteca')
  await page.getByRole('button', { name: 'Pré-escutar Aquarela' }).click()

  const audio = page.getByLabel('Pré-escuta de Aquarela')
  await expect(audio).toHaveAttribute('src', /\/storage\/midia\/2026\/.+X-Amz-Signature=abc$/)
  await expect.poll(() => pediuAudio).toBe(true)
  // O player global (rádio ao vivo) é outro elemento, montado e sem a URL da pré-escuta.
  await expect(page.getByTestId('radio-audio')).toBeAttached()
  await expect(page.getByTestId('radio-audio')).not.toHaveAttribute('src', /storage\/midia/)
})

test('o envio pede a policy, manda o arquivo ao storage e conclui; o 429 vira contagem e retoma', async ({
  page,
}) => {
  const ordem: string[] = []
  let tentativas = 0
  await entrarComoAdmin(page)
  await page.route(/\/api\/v1\/midias\?/, (rota) =>
    rota.fulfill({ json: { itens: [], total: 0, pagina: 1, tamanho: 20 } }),
  )
  await page.route('**/api/v1/midias/uploads', (rota) => {
    tentativas++
    if (tentativas === 1) {
      return rota.fulfill({
        status: 429,
        json: { type: '/erros/limite-envios', title: 'x', status: 429, retryAfterSeconds: 3 },
      })
    }
    ordem.push('policy')
    return rota.fulfill({
      status: 201,
      json: {
        midiaId: MIDIA.id,
        upload: {
          url: `${new URL(page.url()).origin}/storage/quarentena`,
          campos: { key: `2026/${MIDIA.id}`, policy: 'p', 'x-amz-signature': 's' },
          expiraEmUtc: '2099-01-01T00:00:00Z',
        },
      },
    })
  })
  await page.route('**/storage/quarentena', (rota) => {
    ordem.push('storage')
    const corpo = rota.request().postData() ?? ''
    // Os campos da policy vêm antes do arquivo (o S3 ignora o que vem depois do `file`).
    expect(corpo.indexOf('name="key"')).toBeLessThan(corpo.indexOf('name="file"'))
    return rota.fulfill({ status: 204 })
  })
  await page.route('**/api/v1/midias/*/concluir', (rota) => {
    ordem.push('concluir')
    return rota.fulfill({ status: 202 })
  })

  await page.goto('/biblioteca')
  const envio = page.getByRole('region', { name: 'Enviar áudio' })
  await envio.getByLabel(/^Título/).fill('Aquarela')
  await envio.getByLabel(/^Arquivo/).setInputFiles({
    name: 'aquarela.mp3',
    mimeType: 'audio/mpeg',
    buffer: MP3,
  })
  await envio.getByRole('button', { name: 'Enviar' }).click()

  await expect(envio.getByText(/Tenta de novo em 0:0\d/)).toBeVisible()
  await expect(envio.getByText(/Enviado\. A análise começou/)).toBeVisible({ timeout: 10_000 })
  expect(ordem).toEqual(['policy', 'storage', 'concluir'])
})

test('412 na edição: avisa, relê e o formulário volta com os dados de agora', async ({ page }) => {
  let salvou = 0
  await entrarComoAdmin(page)
  await page.route(/\/api\/v1\/midias\?/, (rota) =>
    rota.fulfill({ json: { itens: [MIDIA], total: 1, pagina: 1, tamanho: 20 } }),
  )
  await page.route(`**/api/v1/midias/${MIDIA.id}`, (rota) => {
    const metodo = rota.request().method()
    if (metodo === 'GET') {
      const atual = salvou === 0 ? MIDIA : { ...MIDIA, titulo: 'Título de outra pessoa' }
      return rota.fulfill({ json: atual, headers: { ETag: salvou === 0 ? '"v1"' : '"v2"' } })
    }
    salvou++
    return rota.fulfill({
      status: 412,
      json: { type: '/erros/versao-desatualizada', title: 'x', status: 412 },
    })
  })

  await page.goto('/biblioteca')
  await page.getByRole('button', { name: 'Editar Aquarela' }).click()
  const dialogo = page.getByRole('dialog')
  await dialogo.getByLabel(/^Título/).fill('Meu título')
  await dialogo.getByRole('button', { name: 'Salvar' }).click()

  await expect(page.getByText(/foi alterado por outra pessoa/)).toBeVisible()
  await expect(dialogo.getByLabel(/^Título/)).toHaveValue('Título de outra pessoa')
})
