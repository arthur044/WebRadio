import { render, screen } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { GradePage } from './GradePage'

function jsonResponse(status: number, body: unknown) {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  })
}

function renderGrade() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={queryClient}>
      <GradePage />
    </QueryClientProvider>,
  )
}

describe('GradePage', () => {
  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('busca a semana atual com deUtc/ateUtc e agrupa os programas por dia', async () => {
    let urlChamada = ''
    vi.stubGlobal(
      'fetch',
      vi.fn(async (url: string) => {
        urlChamada = url
        return jsonResponse(200, [
          {
            id: '1',
            titulo: 'Café da Manhã',
            descricao: null,
            locutor: { id: 'l1', nome: 'Ana' },
            inicioUtc: '2026-01-12T12:00:00Z',
            fimUtc: '2026-01-12T13:00:00Z',
            status: 'Agendado',
          },
          {
            id: '2',
            titulo: 'Tarde Rock',
            descricao: null,
            locutor: { id: 'l2', nome: 'Beto' },
            inicioUtc: '2026-01-13T18:00:00Z',
            fimUtc: '2026-01-13T20:00:00Z',
            status: 'AoVivo',
          },
        ])
      }),
    )

    renderGrade()

    expect(await screen.findByText('Café da Manhã')).toBeInTheDocument()
    expect(screen.getByText('Tarde Rock')).toBeInTheDocument()
    expect(screen.getByText('Ana')).toBeInTheDocument()
    expect(screen.getByText('Ao vivo')).toBeInTheDocument()
    expect(urlChamada).toContain('/programas?deUtc=')
    expect(urlChamada).toContain('ateUtc=')
  })

  it('mostra aviso quando não há programas na semana', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => jsonResponse(200, [])),
    )

    renderGrade()

    expect(await screen.findByText('Nenhum programa agendado esta semana.')).toBeInTheDocument()
  })

  it('mostra erro quando a API falha', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () =>
        jsonResponse(500, { type: '/erros/desconhecido', title: 'erro', status: 500 }),
      ),
    )

    renderGrade()

    expect(await screen.findByRole('alert')).toHaveTextContent(
      'Não foi possível carregar a grade agora.',
    )
  })
})
