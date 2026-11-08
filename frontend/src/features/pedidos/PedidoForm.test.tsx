import { act, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { PedidosPage } from './PedidosPage'

interface Chamada {
  metodo: string
  caminho: string
  corpo: unknown
  cabecalhos: Headers
}

function json(status: number, body?: unknown, headers: Record<string, string> = {}) {
  return new Response(body === undefined ? null : JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json', ...headers },
  })
}

const PEDIDO = {
  id: 'p1',
  nomeOuvinte: 'Ana',
  tituloMusica: 'Asa Branca',
  artista: 'Luiz Gonzaga',
  mensagem: null,
  status: 'Pendente',
  criadoEmUtc: '2026-10-29T12:00:00Z',
  moderadoEmUtc: null,
  motivoRejeicao: null,
  tocadoEmUtc: null,
  programaId: null,
}

/** Servidor falso: `POST /pedidos` responde o que o teste mandar; `GET /pedidos/meus` devolve lista vazia. */
function montarApi(aoPedir: () => Response = () => json(201, PEDIDO)) {
  const chamadas: Chamada[] = []
  const fetchMock = vi.fn(async (url: string, init: RequestInit = {}) => {
    const caminho = url.replace('/api/v1', '')
    const metodo = (init.method ?? 'GET').toUpperCase()
    chamadas.push({
      metodo,
      caminho,
      corpo: init.body ? JSON.parse(init.body as string) : undefined,
      cabecalhos: new Headers(init.headers),
    })
    if (metodo === 'POST' && caminho === '/pedidos') return aoPedir()
    if (caminho === '/pedidos/meus') return json(200, [])
    return json(404, { type: '/erros/x', title: 'x', status: 404 })
  })
  vi.stubGlobal('fetch', fetchMock)
  return { chamadas, pedidos: () => chamadas.filter((c) => c.metodo === 'POST') }
}

function renderPagina() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={queryClient}>
      <PedidosPage />
    </QueryClientProvider>,
  )
}

async function preencher(
  user: ReturnType<typeof userEvent.setup>,
  { nome = 'Ana', musica = 'Asa Branca', artista = 'Luiz Gonzaga', dedicatoria = '' } = {},
) {
  await user.type(screen.getByLabelText('Seu nome'), nome)
  await user.type(screen.getByLabelText('Música'), musica)
  await user.type(screen.getByLabelText('Artista'), artista)
  if (dedicatoria) await user.type(screen.getByLabelText('Dedicatória (opcional)'), dedicatoria)
}

describe('Pedir Música (E3-P01)', () => {
  beforeEach(() => {
    localStorage.clear()
  })

  afterEach(() => {
    vi.useRealTimers()
    vi.restoreAllMocks()
  })

  it('os quatro campos são de linha única (input, nunca textarea)', () => {
    montarApi()
    const { container } = renderPagina()

    for (const rotulo of ['Seu nome', 'Música', 'Artista', 'Dedicatória (opcional)']) {
      const campo = screen.getByLabelText(rotulo)
      expect(campo.tagName).toBe('INPUT')
      expect(campo).toHaveAttribute('type', 'text')
    }
    expect(container.querySelector('textarea')).toBeNull()
  })

  it('o contador conta em string.length (UTF-16): um emoji vale 2', async () => {
    montarApi()
    renderPagina()
    const user = userEvent.setup()

    await user.type(screen.getByLabelText('Seu nome'), 'Ana🎵')

    // 'Ana' = 3, '🎵' = 2 unidades UTF-16 (o `nvarchar` do servidor conta igual).
    expect(screen.getByText('5/60')).toBeInTheDocument()
  })

  it('o contador mede depois do NFC, como o servidor: "é" decomposto vale 1', async () => {
    montarApi()
    renderPagina()
    const user = userEvent.setup()

    await user.type(screen.getByLabelText('Seu nome'), 'José')

    expect(screen.getByText('4/60')).toBeInTheDocument()
  })

  it('o contador ignora o espaço nas pontas, como o servidor (Trim antes de medir)', async () => {
    const api = montarApi()
    renderPagina()
    const user = userEvent.setup()

    await preencher(user, { nome: `${'a'.repeat(60)} ` })
    expect(screen.getByText('60/60')).toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: 'Enviar pedido' }))

    expect(screen.queryByText(/aceita no máximo 60 caracteres/i)).toBeNull()
    await waitFor(() => expect(api.pedidos()).toHaveLength(1))
  })

  it('texto acima do limite não é enviado e o campo diz o limite', async () => {
    const api = montarApi()
    renderPagina()
    const user = userEvent.setup()

    // 31 emoji = 62 unidades: passa de 60 mesmo com só 31 "caracteres" na tela.
    await preencher(user, { nome: '🎵'.repeat(31) })
    await user.click(screen.getByRole('button', { name: 'Enviar pedido' }))

    expect(screen.getByText('62/60')).toBeInTheDocument()
    expect(screen.getByText(/aceita no máximo 60 caracteres/i)).toBeInTheDocument()
    expect(screen.getByLabelText('Seu nome')).toHaveAttribute('aria-invalid', 'true')
    expect(api.pedidos()).toHaveLength(0)
  })

  it('campo obrigatório vazio não é enviado', async () => {
    const api = montarApi()
    renderPagina()
    const user = userEvent.setup()

    await user.type(screen.getByLabelText('Seu nome'), '   ')
    await user.click(screen.getByRole('button', { name: 'Enviar pedido' }))

    expect(screen.getAllByText('Preencha este campo.')).toHaveLength(3)
    expect(api.pedidos()).toHaveLength(0)
  })

  it('envia o pedido com o X-Listener-Id, sem espaços nas pontas e dedicatória vazia como null', async () => {
    const api = montarApi()
    renderPagina()
    const user = userEvent.setup()

    await preencher(user, { nome: '  Ana ', musica: 'Asa Branca ' })
    await user.click(screen.getByRole('button', { name: 'Enviar pedido' }))

    expect(await screen.findByRole('status')).toHaveTextContent('Pedido enviado')
    const [pedido] = api.pedidos()
    expect(pedido.caminho).toBe('/pedidos')
    expect(pedido.corpo).toEqual({
      nomeOuvinte: 'Ana',
      tituloMusica: 'Asa Branca',
      artista: 'Luiz Gonzaga',
      mensagem: null,
    })
    expect(pedido.cabecalhos.get('X-Listener-Id')).toMatch(/^[0-9a-f-]{36}$/)
    // O formulário volta vazio para o próximo pedido.
    expect(screen.getByLabelText('Seu nome')).toHaveValue('')
  })

  it('manda a dedicatória quando preenchida', async () => {
    const api = montarApi()
    renderPagina()
    const user = userEvent.setup()

    await preencher(user, { dedicatoria: 'Para a turma da escola' })
    await user.click(screen.getByRole('button', { name: 'Enviar pedido' }))

    await screen.findByRole('status')
    expect(api.pedidos()[0].corpo).toMatchObject({ mensagem: 'Para a turma da escola' })
  })

  it('400 de validação do servidor aparece no campo', async () => {
    montarApi(() =>
      json(400, {
        type: '/erros/validacao',
        title: 'x',
        status: 400,
        errors: { NomeOuvinte: ['O nome tem caracteres que não são aceitos.'] },
      }),
    )
    renderPagina()
    const user = userEvent.setup()

    await preencher(user)
    await user.click(screen.getByRole('button', { name: 'Enviar pedido' }))

    expect(
      await screen.findByText('O nome tem caracteres que não são aceitos.'),
    ).toBeInTheDocument()
    expect(screen.getByRole('alert')).toHaveTextContent('Corrija os campos destacados')
    expect(screen.getByLabelText('Seu nome')).toHaveAttribute('aria-invalid', 'true')
  })

  it('429: contagem regressiva a partir do retryAfterSeconds, e o botão volta ao zerar', async () => {
    montarApi(() =>
      json(
        429,
        { type: '/erros/muitos-pedidos', title: 'x', status: 429, retryAfterSeconds: 90 },
        { 'Retry-After': '90' },
      ),
    )
    renderPagina()
    const user = userEvent.setup()

    await preencher(user)
    // Só os timers do componente: o resto (promises do fetch) segue em tempo real.
    vi.useFakeTimers({ toFake: ['setInterval', 'clearInterval', 'Date'] })
    await act(async () => {
      screen.getByRole('button', { name: 'Enviar pedido' }).click()
    })

    const botao = await screen.findByRole('button', { name: /Aguarde 1:30 para pedir de novo/ })
    expect(botao).toBeDisabled()
    expect(screen.getByRole('alert')).toHaveTextContent('Muitas tentativas')

    act(() => {
      vi.advanceTimersByTime(30_000)
    })
    expect(screen.getByRole('button', { name: /Aguarde 1:00/ })).toBeDisabled()

    act(() => {
      vi.advanceTimersByTime(60_000)
    })
    expect(screen.getByRole('button', { name: 'Enviar pedido' })).toBeEnabled()
    // O texto digitado é mantido: o ouvinte só reenvia.
    expect(screen.getByLabelText('Seu nome')).toHaveValue('Ana')
    expect(screen.queryByRole('alert')).toBeNull()
  })

  it('o botão fica desabilitado durante o envio (sem pedido em dobro)', async () => {
    let liberar: (r: Response) => void = () => {}
    const api = montarApi()
    vi.stubGlobal(
      'fetch',
      vi.fn(async (url: string, init: RequestInit = {}) => {
        if ((init.method ?? 'GET') === 'POST') {
          api.chamadas.push({
            metodo: 'POST',
            caminho: url,
            corpo: undefined,
            cabecalhos: new Headers(),
          })
          return new Promise<Response>((resolve) => {
            liberar = resolve
          })
        }
        return json(200, [])
      }),
    )
    renderPagina()
    const user = userEvent.setup()

    await preencher(user)
    const botao = screen.getByRole('button', { name: 'Enviar pedido' })
    await user.click(botao)
    expect(botao).toBeDisabled()
    await user.click(botao)
    expect(api.pedidos()).toHaveLength(1)

    await act(async () => liberar(json(201, PEDIDO)))
    expect(await screen.findByRole('status')).toBeInTheDocument()
  })
})
