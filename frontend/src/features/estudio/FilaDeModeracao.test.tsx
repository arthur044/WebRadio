import { act, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { criarDedupe, registrarEventos } from '../../shared/api/realtimeHub'
import type { PedidoDto } from '../../shared/api/types'
import { FilaDeModeracao } from './FilaDeModeracao'

function json(status: number, body?: unknown) {
  return new Response(body === undefined ? null : JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  })
}

function pedido(id: string, extras: Partial<PedidoDto> = {}): PedidoDto {
  return {
    id,
    nomeOuvinte: 'Ana',
    tituloMusica: `Música ${id}`,
    artista: 'Artista',
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

interface Chamada {
  metodo: string
  caminho: string
  corpo: unknown
}

/** Servidor falso com estado: aprovar/rejeitar/tocar movem o pedido de lista, como o servidor faria. */
function montarApi(inicial: { pendentes?: PedidoDto[]; aprovados?: PedidoDto[] } = {}) {
  const estado = {
    pendentes: inicial.pendentes ?? [],
    aprovados: inicial.aprovados ?? [],
    total: undefined as number | undefined,
    // Resposta forçada para a próxima ação de moderação (409, 403, 400...).
    proximaAcao: null as (() => Response) | null,
  }
  const chamadas: Chamada[] = []

  vi.stubGlobal(
    'fetch',
    vi.fn(async (url: string, init: RequestInit = {}) => {
      const caminho = url.replace('/api/v1', '')
      const metodo = (init.method ?? 'GET').toUpperCase()
      chamadas.push({
        metodo,
        caminho,
        corpo: init.body ? JSON.parse(init.body as string) : undefined,
      })

      if (metodo === 'GET' && caminho.startsWith('/pedidos?')) {
        const lista = caminho.includes('status=Aprovado') ? estado.aprovados : estado.pendentes
        return json(200, {
          itens: lista,
          total: estado.total ?? lista.length,
          pagina: 1,
          tamanho: 100,
        })
      }
      const acao = /^\/pedidos\/([^/]+)\/(aprovar|rejeitar|marcar-tocado)$/.exec(caminho)
      if (metodo === 'POST' && acao) {
        if (estado.proximaAcao) {
          const resposta = estado.proximaAcao()
          estado.proximaAcao = null
          return resposta
        }
        const [, id, nome] = acao
        const alvo = [...estado.pendentes, ...estado.aprovados].find((p) => p.id === id)!
        estado.pendentes = estado.pendentes.filter((p) => p.id !== id)
        estado.aprovados = estado.aprovados.filter((p) => p.id !== id)
        if (nome === 'aprovar') estado.aprovados.push({ ...alvo, status: 'Aprovado' })
        return json(200, alvo)
      }
      return json(404, { type: '/erros/x', title: 'x', status: 404 })
    }),
  )
  return { estado, chamadas, acoes: () => chamadas.filter((c) => c.metodo === 'POST') }
}

function renderFila() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  const handlers = new Map<string, (dados: unknown) => void>()
  registrarEventos(
    { on: (nome: string, h: (d: unknown) => void) => handlers.set(nome, h) } as never,
    queryClient,
    criarDedupe(),
  )
  render(
    <QueryClientProvider client={queryClient}>
      <FilaDeModeracao />
    </QueryClientProvider>,
  )
  return (nome: string, dados: object) => act(async () => handlers.get(nome)!(dados))
}

describe('Fila de moderação (E3-P03)', () => {
  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('lista os pendentes na ordem do servidor e os aprovados, lendo as duas consultas do contrato', async () => {
    const api = montarApi({
      pendentes: [pedido('a', { mensagem: 'Para a turma' }), pedido('b', { nomeOuvinte: 'Bia' })],
      aprovados: [pedido('c', { status: 'Aprovado' })],
    })
    renderFila()

    await waitFor(() => expect(screen.getAllByRole('listitem')).toHaveLength(3))
    const itens = screen.getAllByRole('listitem')
    expect(within(itens[0]).getByText('Música a')).toBeInTheDocument()
    expect(within(itens[0]).getByText(/Para a turma/)).toBeInTheDocument()
    expect(within(itens[1]).getByText(/De Bia/)).toBeInTheDocument()
    const leituras = api.chamadas.map((c) => c.caminho)
    expect(leituras).toContain('/pedidos?status=Pendente&pagina=1&tamanho=100')
    expect(leituras).toContain('/pedidos?status=Aprovado&pagina=1&tamanho=100')
  })

  it('nome, música e dedicatória aparecem como texto puro (HTML não vira elemento)', async () => {
    montarApi({
      pendentes: [
        pedido('a', {
          nomeOuvinte: '<b>Negrito</b>',
          tituloMusica: '<img src=x onerror=alert(1)>',
          mensagem: '<script>alert(1)</script>',
        }),
      ],
    })
    renderFila()

    expect(await screen.findByText('<img src=x onerror=alert(1)>')).toBeInTheDocument()
    expect(screen.getByText(/<b>Negrito<\/b>/)).toBeInTheDocument()
    expect(document.body.querySelector('img, b, script')).toBeNull()
  })

  it('sem pedidos, mostra os dois avisos de lista vazia', async () => {
    montarApi()
    renderFila()
    expect(await screen.findByText('Nenhum pedido aguardando.')).toBeInTheDocument()
    expect(screen.getByText('Nenhum pedido aprovado nas últimas 24 horas.')).toBeInTheDocument()
  })

  it('avisa quando há mais pendentes do que a página mostra', async () => {
    const api = montarApi({ pendentes: [pedido('a')] })
    api.estado.total = 130
    renderFila()
    expect(await screen.findByText(/Mostrando 1 de 130/)).toBeInTheDocument()
  })

  it('aprovar manda POST /aprovar com {} e o pedido passa para os aprovados', async () => {
    const api = montarApi({ pendentes: [pedido('a')] })
    renderFila()
    const user = userEvent.setup()

    await user.click(await screen.findByRole('button', { name: 'Aprovar Música a' }))

    await screen.findByRole('button', { name: 'Marcar Música a como tocado' })
    expect(screen.queryByRole('button', { name: 'Aprovar Música a' })).toBeNull()
    expect(api.acoes()).toEqual([{ metodo: 'POST', caminho: '/pedidos/a/aprovar', corpo: {} }])
  })

  it('marcar como tocado tira o pedido da lista de aprovados', async () => {
    const api = montarApi({ aprovados: [pedido('c', { status: 'Aprovado' })] })
    renderFila()
    const user = userEvent.setup()

    await user.click(await screen.findByRole('button', { name: 'Marcar Música c como tocado' }))

    await screen.findByText('Nenhum pedido aprovado nas últimas 24 horas.')
    expect(api.acoes()).toEqual([
      { metodo: 'POST', caminho: '/pedidos/c/marcar-tocado', corpo: undefined },
    ])
  })

  describe('rejeitar', () => {
    it('abre o diálogo com o foco no motivo; vazio e acima de 280 não enviam; cancelar devolve o foco', async () => {
      const api = montarApi({ pendentes: [pedido('a')] })
      renderFila()
      const user = userEvent.setup()

      await user.click(await screen.findByRole('button', { name: 'Rejeitar Música a' }))
      const campo = screen.getByLabelText(/Motivo da rejeição/)
      expect(campo).toHaveFocus()
      expect(campo.tagName).toBe('INPUT')

      await user.click(screen.getByRole('button', { name: 'Confirmar rejeição' }))
      expect(screen.getByText('Informe o motivo da rejeição.')).toBeInTheDocument()

      await user.click(campo)
      await user.paste('x'.repeat(281))
      await user.click(screen.getByRole('button', { name: 'Confirmar rejeição' }))
      expect(screen.getByText('281/280')).toBeInTheDocument()
      expect(screen.getByText(/aceita no máximo 280 caracteres/)).toBeInTheDocument()
      expect(api.acoes()).toHaveLength(0)

      await user.click(screen.getByRole('button', { name: 'Cancelar' }))
      expect(screen.queryByRole('dialog')).toBeNull()
      expect(screen.getByRole('button', { name: 'Rejeitar Música a' })).toHaveFocus()
    })

    it('envia o motivo (sem espaços nas pontas) e o pedido sai da fila', async () => {
      const api = montarApi({ pendentes: [pedido('a')] })
      renderFila()
      const user = userEvent.setup()

      await user.click(await screen.findByRole('button', { name: 'Rejeitar Música a' }))
      await user.type(screen.getByLabelText(/Motivo da rejeição/), '  Já tocamos hoje ')
      await user.click(screen.getByRole('button', { name: 'Confirmar rejeição' }))

      await screen.findByText('Nenhum pedido aguardando.')
      expect(api.acoes()).toEqual([
        { metodo: 'POST', caminho: '/pedidos/a/rejeitar', corpo: { motivo: 'Já tocamos hoje' } },
      ])
    })

    it('400 do servidor no motivo aparece no diálogo, que continua aberto', async () => {
      const api = montarApi({ pendentes: [pedido('a')] })
      api.estado.proximaAcao = () =>
        json(400, {
          type: '/erros/validacao',
          title: 'x',
          status: 400,
          errors: { Motivo: ['O motivo tem caracteres que não são aceitos.'] },
        })
      renderFila()
      const user = userEvent.setup()

      await user.click(await screen.findByRole('button', { name: 'Rejeitar Música a' }))
      await user.type(screen.getByLabelText(/Motivo da rejeição/), 'motivo')
      await user.click(screen.getByRole('button', { name: 'Confirmar rejeição' }))

      expect(
        await screen.findByText('O motivo tem caracteres que não são aceitos.'),
      ).toBeInTheDocument()
      expect(screen.getByRole('dialog')).toBeInTheDocument()
    })
  })

  it('409 pedido-ja-moderado: avisa que outro locutor moderou e tira o item', async () => {
    const api = montarApi({ pendentes: [pedido('a'), pedido('b')] })
    api.estado.proximaAcao = () => {
      // O outro locutor já moderou: o servidor não tem mais o pedido na fila.
      api.estado.pendentes = api.estado.pendentes.filter((p) => p.id !== 'a')
      return json(409, { type: '/erros/pedido-ja-moderado', title: 'x', status: 409 })
    }
    renderFila()
    const user = userEvent.setup()

    await user.click(await screen.findByRole('button', { name: 'Aprovar Música a' }))

    expect(await screen.findByRole('status')).toHaveTextContent(
      'Outro locutor já moderou este pedido.',
    )
    await waitFor(() => expect(screen.queryByText('Música a')).toBeNull())
    // O outro pedido segue na fila.
    expect(screen.getByText('Música b')).toBeInTheDocument()
  })

  it('outro erro (403) fica no item e o pedido continua na fila', async () => {
    const api = montarApi({ pendentes: [pedido('a')] })
    api.estado.proximaAcao = () => json(403, undefined)
    renderFila()
    const user = userEvent.setup()

    await user.click(await screen.findByRole('button', { name: 'Aprovar Música a' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('Você não tem permissão')
    expect(screen.getByText('Música a')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Aprovar Música a' })).toBeEnabled()
  })

  it('PedidoCriado pelo hub faz o pedido novo aparecer sem recarregar', async () => {
    const api = montarApi({ pendentes: [pedido('a')] })
    const disparar = renderFila()
    await screen.findByText('Música a')

    api.estado.pendentes = [pedido('a'), pedido('b')]
    await disparar('PedidoCriado', { eventoId: '1', ...pedido('b') })

    expect(await screen.findByText('Música b')).toBeInTheDocument()
  })

  it('PedidoModerado de outro locutor tira o pedido da fila deste', async () => {
    const api = montarApi({ pendentes: [pedido('a'), pedido('b')] })
    const disparar = renderFila()
    await screen.findByText('Música a')

    api.estado.pendentes = [pedido('b')]
    api.estado.aprovados = [pedido('a', { status: 'Aprovado' })]
    await disparar('PedidoModerado', { eventoId: '2', pedidoId: 'a', status: 'Aprovado' })

    expect(
      await screen.findByRole('button', { name: 'Marcar Música a como tocado' }),
    ).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Aprovar Música a' })).toBeNull()
  })
})
