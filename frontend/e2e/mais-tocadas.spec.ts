import { expect, test } from '@playwright/test'

/** E4-P05 no preview, com a API mockada: período padrão de 30 dias, tabela na ordem do servidor e o teto de 366 dias. */
test('o Admin vê as mais tocadas dos últimos 30 dias e o período acima de 366 dias não chama a API', async ({
  page,
}) => {
  const consultas: string[] = []
  await page.route('**/api/v1/auth/refresh', (rota) =>
    rota.fulfill({
      json: {
        accessToken: 'token-de-teste',
        expiraEmUtc: '2099-01-01T00:00:00Z',
        deveTrocarSenha: false,
        usuario: {
          id: '11111111-1111-4111-8111-111111111111',
          nome: 'Ada Admin',
          email: 'ada@example.com',
          role: 'Admin',
          deveTrocarSenha: false,
        },
      },
    }),
  )
  await page.route(/\/api\/v1\/relatorios\/mais-tocadas\?/, (rota) => {
    consultas.push(rota.request().url())
    return rota.fulfill({
      json: [
        {
          midiaId: 'a',
          titulo: 'Aquarela',
          artista: 'Toquinho',
          tipoMidia: 'Musica',
          reproducoes: 12,
        },
        {
          midiaId: 'b',
          titulo: 'Vinheta da manhã',
          artista: null,
          tipoMidia: 'Vinheta',
          reproducoes: 7,
        },
      ],
    })
  })

  await page.goto('/admin/mais-tocadas')

  await expect(page.getByRole('row', { name: /Aquarela Toquinho Música 12/ })).toBeVisible()
  await expect(page.getByRole('row', { name: /Vinheta da manhã — Vinheta 7/ })).toBeVisible()
  expect(consultas).toHaveLength(1)
  const url = new URL(consultas[0])
  const dias =
    (Date.parse(url.searchParams.get('ate')!) - Date.parse(url.searchParams.get('de')!)) /
    86_400_000
  expect(dias).toBe(30)

  await page.getByLabel('De').fill('2020-01-01')
  await expect(page.getByRole('alert')).toContainText('até 366 dias')
  expect(consultas).toHaveLength(1)
})
