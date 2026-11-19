import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { MaisTocadasPage } from './MaisTocadasPage'
import { periodoParaUtc } from './useMaisTocadas'

function json(status: number, body?: unknown) {
  return new Response(body === undefined ? null : JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  })
}

function montarApi(resposta: () => Response) {
  const chamadas: string[] = []
  vi.stubGlobal(
    'fetch',
    vi.fn(async (url: string) => {
      chamadas.push(url.replace('/api/v1', ''))
      return resposta()
    }),
  )
  return chamadas
}

function renderPagina() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  render(
    <QueryClientProvider client={queryClient}>
      <MaisTocadasPage />
    </QueryClientProvider>,
  )
}

describe('periodoParaUtc', () => {
  it('converte dias de Brasília (inclusive) em [de, ate) UTC: o fim é a meia-noite seguinte', () => {
    expect(periodoParaUtc('2026-11-01', '2026-11-08')).toEqual({
      periodo: { de: '2026-11-01T03:00:00Z', ate: '2026-11-09T03:00:00Z' },
    })
  })

  it('aceita 366 dias e recusa 367', () => {
    expect('periodo' in periodoParaUtc('2025-11-08', '2026-11-08')).toBe(true) // 366 dias, hoje inclusive
    const r = periodoParaUtc('2025-11-07', '2026-11-08')
    expect(r).toEqual({ erro: 'O período vai até 366 dias; este tem 367.' })
  })

  it('recusa data final anterior e campo vazio', () => {
    expect(periodoParaUtc('2026-11-08', '2026-11-01')).toEqual({
      erro: 'A data final não pode ser anterior à inicial.',
    })
    expect(periodoParaUtc('', '2026-11-01')).toEqual({ erro: 'Informe as duas datas.' })
  })
})

describe('Mais tocadas (E4-P05)', () => {
  beforeEach(() => {
    vi.useFakeTimers({ toFake: ['Date'] })
    // 8/11/2026 12:00 em Brasília.
    vi.setSystemTime(new Date('2026-11-08T15:00:00Z'))
  })
  afterEach(() => {
    vi.useRealTimers()
    vi.unstubAllGlobals()
  })

  it('padrão: últimos 30 dias, hoje inclusive, e a tabela na ordem que o servidor mandou', async () => {
    const chamadas = montarApi(() =>
      json(200, [
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
      ]),
    )
    renderPagina()

    expect(screen.getByLabelText('De')).toHaveValue('2026-10-10')
    expect(screen.getByLabelText('Até')).toHaveValue('2026-11-08')
    await screen.findByText('Aquarela')
    expect(chamadas).toEqual([
      '/relatorios/mais-tocadas?de=2026-10-10T03%3A00%3A00Z&ate=2026-11-09T03%3A00%3A00Z',
    ])

    const linhas = screen.getAllByRole('row').slice(1)
    expect(
      within(linhas[0])
        .getAllByRole('cell')
        .map((c) => c.textContent),
    ).toEqual(['1', 'Aquarela', 'Toquinho', 'Música', '12'])
    expect(
      within(linhas[1])
        .getAllByRole('cell')
        .map((c) => c.textContent),
    ).toEqual(['2', 'Vinheta da manhã', '—', 'Vinheta', '7'])
  })

  it('título e artista aparecem como texto puro', async () => {
    montarApi(() =>
      json(200, [
        {
          midiaId: 'a',
          titulo: '<img src=x onerror=alert(1)>',
          artista: '<b>x</b>',
          tipoMidia: 'Musica',
          reproducoes: 1,
        },
      ]),
    )
    renderPagina()

    expect(await screen.findByText('<img src=x onerror=alert(1)>')).toBeInTheDocument()
    expect(document.body.querySelector('img, b')).toBeNull()
  })

  it('período acima de 366 dias não chama a API e mostra o aviso', async () => {
    const chamadas = montarApi(() => json(200, []))
    renderPagina()
    await screen.findByText('Nenhuma reprodução neste período.')
    chamadas.length = 0

    fireEvent.change(screen.getByLabelText('De'), { target: { value: '2025-01-01' } })

    expect(await screen.findByRole('alert')).toHaveTextContent(/até 366 dias/)
    expect(chamadas).toEqual([])
  })

  it('mudar a data relê com o novo período', async () => {
    const chamadas = montarApi(() => json(200, []))
    renderPagina()
    await screen.findByText('Nenhuma reprodução neste período.')

    fireEvent.change(screen.getByLabelText('De'), { target: { value: '2026-11-01' } })

    await waitFor(() =>
      expect(chamadas.at(-1)).toBe(
        '/relatorios/mais-tocadas?de=2026-11-01T03%3A00%3A00Z&ate=2026-11-09T03%3A00%3A00Z',
      ),
    )
  })

  it('erro da API mostra a mensagem e deixa tentar de novo', async () => {
    let falha = true
    montarApi(() =>
      falha ? json(503, { type: '/erros/x', title: 'x', status: 503 }) : json(200, []),
    )
    renderPagina()

    expect(await screen.findByRole('alert')).toHaveTextContent(/servidor está ocupado/)
    falha = false
    fireEvent.click(screen.getByRole('button', { name: 'Tentar de novo' }))
    expect(await screen.findByText('Nenhuma reprodução neste período.')).toBeInTheDocument()
  })
})
