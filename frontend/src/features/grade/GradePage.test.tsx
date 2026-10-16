import { act, render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter, useLocation } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { criarDedupe, registrarEventos } from '../../shared/api/realtimeHub'
import { GradePage } from './GradePage'

function jsonResponse(status: number, body: unknown) {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  })
}

function programa(extras: Partial<Record<string, unknown>> = {}) {
  return {
    id: '1',
    titulo: 'Café da Manhã',
    descricao: null,
    locutor: { id: 'l1', nome: 'Ana' },
    inicioUtc: '2026-01-14T12:00:00Z',
    fimUtc: '2026-01-14T13:00:00Z',
    status: 'Agendado',
    ...extras,
  }
}

function LocalizacaoAtual() {
  const { search } = useLocation()
  return <output data-testid="busca">{search}</output>
}

function renderGrade(rota = '/grade') {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  const utils = render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={[rota]}>
        <GradePage />
        <LocalizacaoAtual />
      </MemoryRouter>
    </QueryClientProvider>,
  )
  return { ...utils, queryClient }
}

describe('GradePage', () => {
  beforeEach(() => {
    // Quarta-feira 2026-01-14 11:00 em Brasília: a semana atual é 12/01 a 18/01.
    vi.useFakeTimers({ toFake: ['Date'] })
    vi.setSystemTime(new Date('2026-01-14T14:00:00Z'))
  })

  afterEach(() => {
    vi.useRealTimers()
    vi.restoreAllMocks()
  })

  it('busca a semana atual com deUtc/ateUtc e agrupa os programas por dia', async () => {
    let urlChamada = ''
    vi.stubGlobal(
      'fetch',
      vi.fn(async (url: string) => {
        urlChamada = url
        return jsonResponse(200, [
          programa(),
          programa({
            id: '2',
            titulo: 'Tarde Rock',
            locutor: { id: 'l2', nome: 'Beto' },
            inicioUtc: '2026-01-15T18:00:00Z',
            fimUtc: '2026-01-15T20:00:00Z',
            status: 'AoVivo',
          }),
        ])
      }),
    )

    renderGrade()

    expect(await screen.findByText('Café da Manhã')).toBeInTheDocument()
    expect(screen.getByText('Tarde Rock')).toBeInTheDocument()
    expect(screen.getByText('Ana')).toBeInTheDocument()
    expect(screen.getByText('Ao vivo')).toBeInTheDocument()
    expect(urlChamada).toContain('deUtc=2026-01-12T03%3A00%3A00.000Z')
    expect(urlChamada).toContain('ateUtc=2026-01-19T03%3A00%3A00.000Z')
    // A grade pública nunca pede os cancelados.
    expect(urlChamada).not.toContain('incluirCancelados')
  })

  it('mostra o intervalo da semana e marca o dia de hoje', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => jsonResponse(200, [programa()])),
    )

    renderGrade()

    expect(await screen.findByText('12/01 a 18/01')).toBeInTheDocument()
    expect(await screen.findByText('hoje')).toBeInTheDocument()
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

  it('navega entre semanas pelo ?semana= e busca o intervalo da semana escolhida', async () => {
    const urls: string[] = []
    vi.stubGlobal(
      'fetch',
      vi.fn(async (url: string) => {
        urls.push(url)
        return jsonResponse(200, [])
      }),
    )
    const user = userEvent.setup()

    renderGrade()
    await screen.findByText('Nenhum programa agendado esta semana.')

    await user.click(screen.getByRole('button', { name: 'Próxima semana' }))
    expect(await screen.findByText('19/01 a 25/01')).toBeInTheDocument()
    expect(screen.getByTestId('busca')).toHaveTextContent('?semana=2026-01-19')
    expect(urls.at(-1)).toContain('deUtc=2026-01-19T03%3A00%3A00.000Z')
    expect(await screen.findByText('Nenhum programa nesta semana.')).toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: 'Semana anterior' }))
    await user.click(screen.getByRole('button', { name: 'Semana anterior' }))
    expect(await screen.findByText('05/01 a 11/01')).toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: 'Semana atual' }))
    expect(await screen.findByText('12/01 a 18/01')).toBeInTheDocument()
    // Voltar para a semana atual limpa o parâmetro: a URL canônica não carrega data.
    expect(screen.getByTestId('busca')).toHaveTextContent(/^$/)
  })

  it('"Semana atual" fica desabilitado na semana atual', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => jsonResponse(200, [])),
    )

    renderGrade()
    await screen.findByText('Nenhum programa agendado esta semana.')

    expect(screen.getByRole('button', { name: 'Semana atual' })).toBeDisabled()
  })

  it.each(['abc', '2026-01-13', '2026-02-30', '12/01/2026'])(
    '?semana=%s inválido ou que não é segunda cai na semana atual',
    async (invalida) => {
      vi.stubGlobal(
        'fetch',
        vi.fn(async () => jsonResponse(200, [])),
      )

      renderGrade(`/grade?semana=${encodeURIComponent(invalida)}`)

      expect(await screen.findByText('12/01 a 18/01')).toBeInTheDocument()
    },
  )

  it('o selo Ao Vivo aparece e some pelos eventos do hub, sem recarregar a página', async () => {
    let status = 'Agendado'
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => jsonResponse(200, [programa({ status })])),
    )
    const { queryClient } = renderGrade()

    // O hub real (SignalR) entrega o evento ao handler registrado por registrarEventos.
    const handlers = new Map<string, (dados: unknown) => void>()
    registrarEventos(
      { on: (nome: string, h: (d: unknown) => void) => handlers.set(nome, h) } as never,
      queryClient,
      criarDedupe(),
    )
    const linha = (await screen.findByText('Café da Manhã')).closest('li')!
    expect(within(linha).queryByText('Ao vivo')).not.toBeInTheDocument()

    // O Worker virou o programa para AoVivo.
    status = 'AoVivo'
    await act(async () => {
      handlers.get('ProgramaStatusAlterado')!({
        eventoId: '10',
        ocorridoEmUtc: '2026-01-14T14:00:10Z',
        programaId: '1',
        titulo: 'Café da Manhã',
        status: 'AoVivo',
        locutorNome: 'Ana',
      })
    })
    expect(await within(linha).findByText('Ao vivo')).toBeInTheDocument()

    // O locutor encerrou: o selo some, na mesma linha, sem remontar a página.
    status = 'Finalizado'
    await act(async () => {
      handlers.get('ProgramaStatusAlterado')!({
        eventoId: '11',
        ocorridoEmUtc: '2026-01-14T14:20:00Z',
        programaId: '1',
        titulo: 'Café da Manhã',
        status: 'Finalizado',
        locutorNome: 'Ana',
      })
    })
    await vi.waitFor(() => expect(within(linha).queryByText('Ao vivo')).not.toBeInTheDocument())
    expect(linha).toBeInTheDocument()
  })
})
