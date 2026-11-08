import { act, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { useAuthStore } from '../../shared/api/authStore'
import { criarDedupe, invalidarEstadoDaRadio, registrarEventos } from '../../shared/api/realtimeHub'
import type { MidiaDto } from '../../shared/api/types'
import { BibliotecaPage } from './BibliotecaPage'
import { useEnvios } from './enviosStore'

function json(status: number, body?: unknown, headers: Record<string, string> = {}) {
  return new Response(body === undefined ? null : JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json', ...headers },
  })
}

function midia(id: string, extras: Partial<MidiaDto> = {}): MidiaDto {
  return {
    id,
    nomeOriginal: `${id}.mp3`,
    titulo: `Faixa ${id}`,
    artista: 'Artista',
    tipoMidia: 'Musica',
    mimeType: 'audio/mpeg',
    tamanhoBytes: 3 * 1024 * 1024,
    duracaoSegundos: 185,
    statusSanitizacao: 'Aprovado',
    motivoRejeicao: null,
    dataUploadUtc: '2026-11-08T15:00:00Z',
    ativo: true,
    enviadoPorNome: 'Lia Locutora',
    ...extras,
  }
}

interface Chamada {
  metodo: string
  caminho: string
  corpo: unknown
  cabecalhos: Headers
}

function montarApi(inicial: MidiaDto[] = []) {
  const estado = {
    midias: inicial,
    total: undefined as number | undefined,
    etag: '"v1"',
    // Resposta forçada para a próxima escrita (PATCH/DELETE).
    proximaEscrita: null as (() => Response) | null,
    deleteStatus: 204,
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
        cabecalhos: new Headers(init.headers),
      })
      if (metodo === 'GET' && caminho.startsWith('/midias?')) {
        return json(200, {
          itens: estado.midias,
          total: estado.total ?? estado.midias.length,
          pagina: 1,
          tamanho: 20,
        })
      }
      const preview = /^\/midias\/([^/]+)\/preview\?formato=url$/.exec(caminho)
      if (metodo === 'GET' && preview) {
        return json(200, {
          url: `/storage/midia/2026/${preview[1]}.mp3?X-Amz-Signature=abc`,
          expiraEmUtc: '2026-11-08T15:05:00Z',
        })
      }
      const um = /^\/midias\/([^/?]+)$/.exec(caminho)
      if (um) {
        const alvo = estado.midias.find((m) => m.id === um[1])
        if (metodo === 'GET') {
          return alvo
            ? json(200, alvo, { ETag: estado.etag })
            : json(404, { type: '/erros/nao-encontrado', title: 'x', status: 404 })
        }
        if (estado.proximaEscrita) {
          const r = estado.proximaEscrita()
          estado.proximaEscrita = null
          return r
        }
        if (metodo === 'PATCH') return json(200, { ...alvo, ...JSON.parse(init.body as string) })
        if (metodo === 'DELETE') {
          return estado.deleteStatus === 204 ? json(204) : json(200, alvo)
        }
      }
      return json(404, { type: '/erros/x', title: 'x', status: 404 })
    }),
  )
  return {
    estado,
    chamadas,
    listagens: () => chamadas.filter((c) => c.caminho.startsWith('/midias?')),
    escritas: () => chamadas.filter((c) => c.metodo !== 'GET'),
  }
}

function renderBiblioteca(role: 'Admin' | 'Locutor' = 'Locutor') {
  useAuthStore.setState({
    accessToken: 't',
    restoring: false,
    usuario: { id: 'u', nome: 'Lia', email: 'l@x', role, deveTrocarSenha: false },
  })
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  const handlers = new Map<string, (dados: unknown) => void>()
  registrarEventos(
    { on: (nome: string, h: (d: unknown) => void) => handlers.set(nome, h) } as never,
    queryClient,
    criarDedupe(),
  )
  render(
    <QueryClientProvider client={queryClient}>
      <BibliotecaPage />
    </QueryClientProvider>,
  )
  return {
    queryClient,
    emitir: (nome: string, dados: object) => act(async () => handlers.get(nome)!(dados)),
  }
}

describe('Biblioteca (E4-P01 a E4-P03)', () => {
  beforeEach(() => {
    useEnvios.setState({ envios: [] })
  })
  afterEach(() => {
    vi.restoreAllMocks()
    vi.unstubAllGlobals()
    useAuthStore.setState({ accessToken: null, usuario: null })
  })

  it('lista as mídias com tipo, duração, tamanho e quem enviou; sem filtro nenhum a consulta é só a paginação', async () => {
    const api = montarApi([midia('a'), midia('b', { tipoMidia: 'Vinheta', duracaoSegundos: 12 })])
    renderBiblioteca()

    const itens = await screen.findAllByRole('listitem')
    expect(within(itens[0]).getByText('Faixa a')).toBeInTheDocument()
    expect(
      within(itens[0]).getByText(/Música · 3:05 · 3,0 MB · enviada por Lia Locutora/),
    ).toBeInTheDocument()
    expect(within(itens[1]).getByText(/Vinheta · 0:12/)).toBeInTheDocument()
    expect(api.listagens()[0].caminho).toBe('/midias?pagina=1&tamanho=20')
  })

  it('cada filtro vira parâmetro da consulta e a busca espera uma pausa na digitação', async () => {
    const api = montarApi([midia('a')])
    renderBiblioteca()
    const user = userEvent.setup()
    await screen.findByText('Faixa a')

    const filtros = within(screen.getByRole('search'))
    await user.selectOptions(filtros.getByLabelText('Tipo'), 'Vinheta')
    await user.selectOptions(filtros.getByLabelText('Status'), 'Rejeitado')
    await user.type(filtros.getByLabelText('Buscar'), 'rock')

    await waitFor(() =>
      expect(api.listagens().at(-1)!.caminho).toBe(
        '/midias?tipo=Vinheta&status=Rejeitado&busca=rock&pagina=1&tamanho=20',
      ),
    )
    // A digitação não disparou uma consulta por letra.
    expect(api.listagens().filter((c) => c.caminho.includes('busca='))).toHaveLength(1)
  })

  it('o filtro de situação (ativas/desativadas) só existe para o Admin', async () => {
    const api = montarApi([midia('a')])
    renderBiblioteca('Admin')
    const user = userEvent.setup()
    await screen.findByText('Faixa a')

    await user.selectOptions(
      within(screen.getByRole('search')).getByLabelText('Situação'),
      'desativadas',
    )
    await waitFor(() => expect(api.listagens().at(-1)!.caminho).toContain('ativo=false'))
  })

  it('o Locutor não vê o filtro de situação nem desativar, reativar ou excluir', async () => {
    montarApi([midia('a')])
    renderBiblioteca('Locutor')

    await screen.findByText('Faixa a')
    expect(screen.queryByLabelText('Situação')).toBeNull()
    expect(screen.queryByRole('button', { name: /Desativar|Reativar|Excluir/ })).toBeNull()
    expect(screen.getByRole('button', { name: 'Editar Faixa a' })).toBeInTheDocument()
  })

  it('MidiaSanitizada muda o status na tela sem recarregar (relendo por REST)', async () => {
    const api = montarApi([midia('a', { statusSanitizacao: 'Pendente' })])
    const { emitir } = renderBiblioteca()
    expect(await screen.findByText('Na fila de análise', { selector: 'span' })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Pré-escutar Faixa a' })).toBeNull()

    api.estado.midias = [midia('a', { statusSanitizacao: 'Aprovado' })]
    await emitir('MidiaSanitizada', { eventoId: '1', midiaId: 'a', statusSanitizacao: 'Aprovado' })

    expect(await screen.findByText('Aprovada', { selector: 'span' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Pré-escutar Faixa a' })).toBeInTheDocument()
  })

  it('ao reconectar o hub, a lista é relida por REST (eventos perdidos na queda)', async () => {
    const api = montarApi([midia('a', { statusSanitizacao: 'EmAnalise' })])
    const { queryClient } = renderBiblioteca()
    expect(await screen.findByText('Em análise', { selector: 'span' })).toBeInTheDocument()
    const antes = api.listagens().length

    api.estado.midias = [midia('a', { statusSanitizacao: 'Rejeitado', motivoRejeicao: 'duração' })]
    await act(async () => invalidarEstadoDaRadio(queryClient))

    expect(await screen.findByText('Rejeitada', { selector: 'span' })).toBeInTheDocument()
    expect(api.listagens().length).toBeGreaterThan(antes)
  })

  it('motivo da rejeição, título e nome do arquivo aparecem como texto puro (HTML não vira elemento)', async () => {
    montarApi([
      midia('a', {
        titulo: '<img src=x onerror=alert(1)>',
        nomeOriginal: '<b>arquivo</b>.mp3',
        statusSanitizacao: 'Rejeitado',
        motivoRejeicao: '<script>alert(1)</script>',
      }),
    ])
    renderBiblioteca()

    expect(await screen.findByText('<img src=x onerror=alert(1)>')).toBeInTheDocument()
    expect(screen.getByText(/Motivo: <script>alert\(1\)<\/script>/)).toBeInTheDocument()
    expect(screen.getByText(/<b>arquivo<\/b>\.mp3/)).toBeInTheDocument()
    expect(document.body.querySelector('img, b, script')).toBeNull()
  })

  it('a pré-escuta usa um <audio> próprio com a URL pré-assinada da mesma origem', async () => {
    const api = montarApi([midia('a')])
    renderBiblioteca()
    const user = userEvent.setup()

    await user.click(await screen.findByRole('button', { name: 'Pré-escutar Faixa a' }))

    const audio = await screen.findByLabelText('Pré-escuta de Faixa a')
    expect(audio.tagName).toBe('AUDIO')
    expect(audio.getAttribute('src')).toBe(
      `${window.location.origin}/storage/midia/2026/a.mp3?X-Amz-Signature=abc`,
    )
    const pedido = api.chamadas.find((c) => c.caminho === '/midias/a/preview?formato=url')!
    expect(pedido.metodo).toBe('GET')

    await user.click(screen.getByRole('button', { name: 'Fechar pré-escuta' }))
    expect(screen.queryByLabelText('Pré-escuta de Faixa a')).toBeNull()
  })

  it('recusa uma URL de pré-escuta de outra origem ou fora de /storage/midia/', async () => {
    montarApi([midia('a')])
    const original = globalThis.fetch as unknown as (
      url: string,
      init?: RequestInit,
    ) => Promise<Response>
    vi.stubGlobal(
      'fetch',
      vi.fn(async (url: string, init?: RequestInit) =>
        url.includes('/preview?formato=url')
          ? json(200, { url: 'https://evil.example/storage/midia/a.mp3', expiraEmUtc: 'x' })
          : original(url, init),
      ),
    )
    renderBiblioteca()
    const user = userEvent.setup()

    await user.click(await screen.findByRole('button', { name: 'Pré-escutar Faixa a' }))

    expect(await screen.findByRole('alert')).toBeInTheDocument()
    expect(document.querySelector('audio')).toBeNull()
  })

  describe('edição (E4-P03)', () => {
    it('PATCH com If-Match do GET; Música exige título; salvar fecha e avisa', async () => {
      const api = montarApi([midia('a')])
      renderBiblioteca()
      const user = userEvent.setup()

      await user.click(await screen.findByRole('button', { name: 'Editar Faixa a' }))
      const titulo = await within(await screen.findByRole('dialog')).findByLabelText(/^Título/)
      expect(titulo).toHaveValue('Faixa a')

      await user.clear(titulo)
      await user.click(screen.getByRole('button', { name: 'Salvar' }))
      expect(screen.getByText('Informe o título da música.')).toBeInTheDocument()
      expect(api.escritas()).toEqual([])

      await user.type(titulo, 'Nova')
      await user.click(screen.getByRole('button', { name: 'Salvar' }))

      await screen.findByText('Mídia atualizada.')
      const patch = api.escritas()[0]
      expect(patch.metodo).toBe('PATCH')
      expect(patch.caminho).toBe('/midias/a')
      expect(patch.cabecalhos.get('If-Match')).toBe('"v1"')
      expect(patch.corpo).toEqual({ titulo: 'Nova', artista: 'Artista' })
      expect(screen.queryByRole('dialog')).toBeNull()
    })

    it('412: relê a mídia, avisa e mostra o formulário com os dados de agora e o ETag novo', async () => {
      const api = montarApi([midia('a')])
      renderBiblioteca()
      const user = userEvent.setup()
      await user.click(await screen.findByRole('button', { name: 'Editar Faixa a' }))
      await within(await screen.findByRole('dialog')).findByLabelText(/^Título/)

      // Outra pessoa salvou antes: a versão e o título mudaram no servidor.
      api.estado.proximaEscrita = () =>
        json(412, { type: '/erros/versao-desatualizada', title: 'x', status: 412 })
      api.estado.etag = '"v2"'
      api.estado.midias = [midia('a', { titulo: 'Título de outra pessoa' })]
      await user.click(screen.getByRole('button', { name: 'Salvar' }))

      expect(await screen.findByText(/foi alterado por outra pessoa/)).toBeInTheDocument()
      await waitFor(() =>
        expect(within(screen.getByRole('dialog')).getByLabelText(/^Título/)).toHaveValue(
          'Título de outra pessoa',
        ),
      )

      await user.click(screen.getByRole('button', { name: 'Salvar' }))
      await waitFor(() => expect(api.escritas()).toHaveLength(2))
      expect(api.escritas()[1].cabecalhos.get('If-Match')).toBe('"v2"')
    })

    it('400 de validação mostra a mensagem junto do campo', async () => {
      const api = montarApi([midia('a')])
      renderBiblioteca()
      const user = userEvent.setup()
      await user.click(await screen.findByRole('button', { name: 'Editar Faixa a' }))
      await within(await screen.findByRole('dialog')).findByLabelText(/^Título/)
      api.estado.proximaEscrita = () =>
        json(400, {
          type: '/erros/validacao',
          title: 'x',
          status: 400,
          errors: { Titulo: ['O título tem caracteres não permitidos.'] },
        })

      await user.click(screen.getByRole('button', { name: 'Salvar' }))

      expect(await screen.findByText('O título tem caracteres não permitidos.')).toBeInTheDocument()
      expect(within(screen.getByRole('dialog')).getByLabelText(/^Título/)).toHaveAttribute(
        'aria-invalid',
        'true',
      )
    })
  })

  describe('administração (E4-P03)', () => {
    it('desativar lê o ETag e manda PATCH { ativo: false } com If-Match', async () => {
      const api = montarApi([midia('a')])
      renderBiblioteca('Admin')
      const user = userEvent.setup()

      await user.click(await screen.findByRole('button', { name: 'Desativar Faixa a' }))

      await waitFor(() => expect(api.escritas()).toHaveLength(1))
      expect(api.escritas()[0].corpo).toEqual({ ativo: false })
      expect(api.escritas()[0].cabecalhos.get('If-Match')).toBe('"v1"')
    })

    it('excluir pede confirmação; "Manter" não chama a API', async () => {
      const api = montarApi([midia('a')])
      renderBiblioteca('Admin')
      const user = userEvent.setup()

      await user.click(await screen.findByRole('button', { name: 'Excluir Faixa a' }))
      expect(screen.getByText(/Excluir Faixa a\?/)).toBeInTheDocument()
      await user.click(screen.getByRole('button', { name: 'Manter' }))

      expect(api.escritas()).toEqual([])
      expect(screen.getByRole('button', { name: 'Excluir Faixa a' })).toBeInTheDocument()
    })

    it('204: avisa que foi excluída', async () => {
      const api = montarApi([midia('a')])
      renderBiblioteca('Admin')
      const user = userEvent.setup()

      await user.click(await screen.findByRole('button', { name: 'Excluir Faixa a' }))
      api.estado.midias = []
      await user.click(screen.getByRole('button', { name: 'Confirmar exclusão' }))

      expect(await screen.findByText('Faixa a foi excluída.')).toBeInTheDocument()
      expect(api.escritas()[0]).toMatchObject({ metodo: 'DELETE', caminho: '/midias/a' })
    })

    it('200 (já tocou): avisa que só foi desativada', async () => {
      const api = montarApi([midia('a')])
      api.estado.deleteStatus = 200
      renderBiblioteca('Admin')
      const user = userEvent.setup()

      await user.click(await screen.findByRole('button', { name: 'Excluir Faixa a' }))
      await user.click(screen.getByRole('button', { name: 'Confirmar exclusão' }))

      expect(await screen.findByText(/já tocou, então só foi desativada/)).toBeInTheDocument()
    })
  })

  describe('envio (E4-P02)', () => {
    it('recusa localmente: música sem título, sem arquivo e arquivo grande demais; nada entra na fila', async () => {
      montarApi([])
      renderBiblioteca()
      const user = userEvent.setup()
      await screen.findByText('Nenhuma mídia encontrada.')

      await user.click(screen.getByRole('button', { name: 'Enviar' }))
      expect(screen.getByText('Escolha o arquivo de áudio.')).toBeInTheDocument()

      const envio = within(screen.getByRole('region', { name: 'Enviar áudio' }))
      const grande = new File(['x'], 'v.mp3', { type: 'audio/mpeg' })
      Object.defineProperty(grande, 'size', { value: 11 * 1024 * 1024 })
      await user.upload(envio.getByLabelText(/Arquivo/), grande)
      await user.selectOptions(envio.getByLabelText('Tipo'), 'Vinheta')
      await user.click(envio.getByRole('button', { name: 'Enviar' }))
      expect(screen.getByText(/vinheta aceita até 10 MB/)).toBeInTheDocument()

      expect(useEnvios.getState().envios).toEqual([])
    })
  })
})
