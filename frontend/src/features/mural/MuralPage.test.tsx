import { render, screen } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { MuralPage } from './MuralPage'

function jsonResponse(status: number, body: unknown) {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  })
}

function renderMural() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={queryClient}>
      <MuralPage />
    </QueryClientProvider>,
  )
}

describe('MuralPage', () => {
  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('lista as divulgações ativas devolvidas por GET /divulgacoes/ativas', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async (url: string) => {
        expect(url).toContain('/divulgacoes/ativas')
        return jsonResponse(200, [
          {
            id: '1',
            titulo: 'Show ao vivo sexta',
            mensagem: 'Não perca',
            imagemUrl: null,
            linkDestino: null,
            prioridade: 10,
            inicioExibicaoUtc: null,
            fimExibicaoUtc: null,
            ativo: true,
          },
        ])
      }),
    )

    renderMural()

    expect(await screen.findByText('Show ao vivo sexta')).toBeInTheDocument()
    expect(screen.getByText('Não perca')).toBeInTheDocument()
  })

  it('não renderiza link nem imagem com esquema perigoso (javascript:/data:)', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () =>
        jsonResponse(200, [
          {
            id: '1',
            titulo: 'Maliciosa',
            mensagem: 'x',
            imagemUrl: 'data:image/svg+xml,<svg onload=alert(1)>',
            linkDestino: 'javascript:alert(1)',
            prioridade: 1,
            inicioExibicaoUtc: null,
            fimExibicaoUtc: null,
            ativo: true,
          },
          {
            id: '2',
            titulo: 'Legítima',
            mensagem: 'y',
            imagemUrl: 'https://cdn.exemplo.com/a.png',
            linkDestino: 'https://exemplo.com/p',
            prioridade: 1,
            inicioExibicaoUtc: null,
            fimExibicaoUtc: null,
            ativo: true,
          },
        ]),
      ),
    )

    const { container } = renderMural()

    expect(await screen.findByText('Maliciosa')).toBeInTheDocument()
    const links = container.querySelectorAll('a')
    expect(links).toHaveLength(1)
    expect(links[0].getAttribute('href')).toBe('https://exemplo.com/p')
    const imagens = container.querySelectorAll('img')
    expect(imagens).toHaveLength(1)
    expect(imagens[0].getAttribute('src')).toBe('https://cdn.exemplo.com/a.png')
  })

  it('mostra aviso quando não há divulgações ativas', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => jsonResponse(200, [])),
    )

    renderMural()

    expect(await screen.findByText('Nenhuma divulgação ativa no momento.')).toBeInTheDocument()
  })

  it('mostra erro quando a API falha', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () =>
        jsonResponse(500, { type: '/erros/desconhecido', title: 'erro', status: 500 }),
      ),
    )

    renderMural()

    expect(await screen.findByRole('alert')).toHaveTextContent(
      'Não foi possível carregar o mural agora.',
    )
  })
})
