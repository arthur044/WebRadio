import { expect, test } from '@playwright/test'

/**
 * E3-P05 (DoD 7 do Épico 3), no preview com a API mockada: o Mural mostra as divulgações na ordem que o servidor mandou
 * (prioridade e depois id), a mensagem como texto puro (HTML não vira elemento nem executa) e o link com
 * `rel="noopener noreferrer"`.
 */
const base = {
  mensagem: 'm',
  imagemChave: null,
  imagemUrl: null,
  linkDestino: null,
  inicioExibicaoUtc: null,
  fimExibicaoUtc: null,
  ativo: true,
}

test('o Mural mostra só o que a API mandou, em ordem, com texto puro e link seguro', async ({
  page,
}) => {
  const dialogos: string[] = []
  page.on('dialog', (d) => {
    dialogos.push(d.message())
    void d.dismiss()
  })

  await page.route('**/api/v1/divulgacoes/ativas', (rota) =>
    rota.fulfill({
      json: [
        {
          ...base,
          id: 'b',
          titulo: 'Alta prioridade',
          prioridade: 90,
          mensagem: 'Linha 1\n<img src=x onerror=alert(1)>\n<script>alert(2)</script>',
          linkDestino: 'https://exemplo.com/show',
          imagemChave: 'divulgacoes/0123456789abcdef0123456789abcdef.webp',
          imagemUrl: '/storage/publico/divulgacoes/0123456789abcdef0123456789abcdef.webp',
        },
        { ...base, id: 'a', titulo: 'Média A', prioridade: 50 },
        { ...base, id: 'c', titulo: 'Média C', prioridade: 50 },
      ],
    }),
  )
  // 1x1 PNG: a imagem carrega sem depender do storage.
  await page.route('**/storage/publico/**', (rota) =>
    rota.fulfill({
      contentType: 'image/png',
      body: Buffer.from(
        'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==',
        'base64',
      ),
    }),
  )

  await page.goto('/')

  const titulos = page.getByRole('heading', { level: 2 })
  await expect(titulos).toHaveText(['Alta prioridade', 'Média A', 'Média C'])

  // O HTML da mensagem aparece como texto, e nada dele virou elemento.
  const cartao = page.getByRole('article').filter({ hasText: 'Alta prioridade' })
  await expect(cartao).toContainText('<img src=x onerror=alert(1)>')
  await expect(cartao).toContainText('<script>alert(2)</script>')
  await expect(cartao.locator('script')).toHaveCount(0)
  await expect(cartao.locator('img')).toHaveCount(1) // só a imagem da divulgação

  const imagem = page.getByAltText('Alta prioridade')
  await expect(imagem).toHaveAttribute('loading', 'lazy')

  const link = page.getByRole('link').filter({ hasText: 'Alta prioridade' })
  await expect(link).toHaveAttribute('href', 'https://exemplo.com/show')
  await expect(link).toHaveAttribute('target', '_blank')
  await expect(link).toHaveAttribute('rel', 'noopener noreferrer')

  expect(dialogos).toEqual([])
})
