import { act, render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { criarDedupe, invalidarEstadoDaRadio, registrarEventos } from '../../shared/api/realtimeHub'
import type { PedidoDto } from '../../shared/api/types'
import { MeusPedidos } from './MeusPedidos'

function json(status: number, body?: unknown) {
  return new Response(body === undefined ? null : JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  })
}

function pedido(extras: Partial<PedidoDto> = {}): PedidoDto {
  return {
    id: 'p1',
    nomeOuvinte: 'Ana',
    tituloMusica: 'Asa Branca',
    artista: 'Luiz Gonzaga',
    mensagem: null,
    status: 'Pendente',
    criadoEmUtc: '2026-10-29T15:00:00Z',
    moderadoEmUtc: null,
    motivoRejeicao: null,
    tocadoEmUtc: null,
    programaId: null,
    ...extras,
  }
}

function renderMeus() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  render(
    <QueryClientProvider client={queryClient}>
      <MeusPedidos />
    </QueryClientProvider>,
  )
  return queryClient
}

/** Conexão falsa do hub: guarda os handlers para o teste disparar `PedidoModerado` como o servidor faria. */
function ligarHub(queryClient: QueryClient) {
  const handlers = new Map<string, (dados: unknown) => void>()
  registrarEventos(
    { on: (nome: string, h: (d: unknown) => void) => handlers.set(nome, h) } as never,
    queryClient,
    criarDedupe(),
  )
  return (nome: string, dados: object) => act(async () => handlers.get(nome)!(dados))
}

describe('Meus pedidos (E3-P02)', () => {
  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('lista os pedidos do dispositivo com o status, lendo /pedidos/meus com o X-Listener-Id', async () => {
    const fetchMock = vi.fn(async () =>
      json(200, [pedido(), pedido({ id: 'p2', tituloMusica: 'Detalhes', status: 'Aprovado' })]),
    )
    vi.stubGlobal('fetch', fetchMock)

    renderMeus()

    expect(await screen.findByText('Aguardando o locutor')).toBeInTheDocument()
    expect(screen.getByText('Aprovado')).toBeInTheDocument()
    expect(screen.getByText('Detalhes')).toBeInTheDocument()
    const [url, init] = fetchMock.mock.calls[0] as unknown as [string, RequestInit]
    expect(url).toBe('/api/v1/pedidos/meus')
    expect(new Headers(init.headers).get('X-Listener-Id')).toMatch(/^[0-9a-f-]{36}$/)
  })

  it('sem pedidos, diz que ainda não há nenhum', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => json(200, [])),
    )
    renderMeus()
    expect(await screen.findByText('Você ainda não fez nenhum pedido.')).toBeInTheDocument()
  })

  it('o motivo da rejeição aparece como texto puro (HTML não vira elemento)', async () => {
    const motivo = '<img src=x onerror=alert(1)> já tocamos hoje'
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => json(200, [pedido({ status: 'Rejeitado', motivoRejeicao: motivo })])),
    )

    const { container } = render(
      <QueryClientProvider
        client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}
      >
        <MeusPedidos />
      </QueryClientProvider>,
    )

    expect(await screen.findByText(`Motivo: ${motivo}`)).toBeInTheDocument()
    expect(container.querySelector('img')).toBeNull()
  })

  it('o motivo só aparece em pedido rejeitado', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => json(200, [pedido({ status: 'Expirado', motivoRejeicao: 'resto' })])),
    )
    renderMeus()
    expect(await screen.findByText('Expirou sem resposta')).toBeInTheDocument()
    expect(screen.queryByText(/Motivo/)).toBeNull()
  })

  it('PedidoModerado e depois PedidoTocado atualizam o status sem recarregar', async () => {
    let atual = pedido()
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => json(200, [atual])),
    )
    const queryClient = renderMeus()
    const disparar = ligarHub(queryClient)
    expect(await screen.findByText('Aguardando o locutor')).toBeInTheDocument()

    atual = pedido({ status: 'Aprovado' })
    await disparar('PedidoModerado', { eventoId: '1', pedidoId: 'p1', status: 'Aprovado' })
    expect(await screen.findByText('Aprovado')).toBeInTheDocument()

    atual = pedido({ status: 'Tocado' })
    await disparar('PedidoTocado', { eventoId: '2', pedidoId: 'p1' })
    expect(await screen.findByText('Tocado')).toBeInTheDocument()
    expect(screen.queryByText('Aprovado')).toBeNull()
  })

  it('rejeição pelo hub traz o motivo na hora', async () => {
    let atual = pedido()
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => json(200, [atual])),
    )
    const queryClient = renderMeus()
    const disparar = ligarHub(queryClient)
    await screen.findByText('Aguardando o locutor')

    atual = pedido({ status: 'Rejeitado', motivoRejeicao: 'Fora do estilo da rádio' })
    await disparar('PedidoModerado', {
      eventoId: '3',
      pedidoId: 'p1',
      status: 'Rejeitado',
      motivoRejeicao: 'Fora do estilo da rádio',
    })

    expect(await screen.findByText('Motivo: Fora do estilo da rádio')).toBeInTheDocument()
  })

  it('ao reconectar o hub, ressincroniza por REST o que os eventos perdidos deixaram velho', async () => {
    let atual = pedido()
    const fetchMock = vi.fn(async () => json(200, [atual]))
    vi.stubGlobal('fetch', fetchMock)
    const queryClient = renderMeus()
    await screen.findByText('Aguardando o locutor')

    atual = pedido({ status: 'Aprovado' })
    await act(async () => invalidarEstadoDaRadio(queryClient))

    expect(await screen.findByText('Aprovado')).toBeInTheDocument()
    expect(fetchMock).toHaveBeenCalledTimes(2)
  })

  it('erro de rede mostra o aviso e o "Tentar de novo" relê', async () => {
    const respostas = [
      () => json(500, { type: '/erros/x', title: 'x', status: 500 }),
      () => json(200, [pedido()]),
    ]
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => respostas.shift()!()),
    )
    renderMeus()
    const user = userEvent.setup()

    expect(await screen.findByRole('alert')).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Tentar de novo' }))

    expect(await screen.findByText('Aguardando o locutor')).toBeInTheDocument()
    expect(screen.queryByRole('alert')).toBeNull()
  })
})
