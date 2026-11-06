import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { DivulgacaoDto } from '../../shared/api/types'
import { AdminDivulgacoesPage } from './AdminDivulgacoesPage'

const CHAVE = 'divulgacoes/0123456789abcdef0123456789abcdef.webp'

function json(status: number, body?: unknown, headers: Record<string, string> = {}) {
  return new Response(body === undefined ? null : JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json', ...headers },
  })
}

function problema(status: number, type: string, extras: object = {}) {
  return json(status, { type, title: 'x', status, ...extras })
}

function divulgacao(extras: Partial<DivulgacaoDto> = {}): DivulgacaoDto {
  return {
    id: 'd1',
    titulo: 'Show de sexta',
    mensagem: 'Venha ver',
    imagemChave: CHAVE,
    imagemUrl: `/storage/publico/${CHAVE}`,
    linkDestino: 'https://exemplo.com/show',
    prioridade: 10,
    inicioExibicaoUtc: '2026-11-01T12:00:00Z',
    fimExibicaoUtc: '2026-11-08T12:00:00Z',
    ativo: true,
    ...extras,
  }
}

interface Chamada {
  metodo: string
  caminho: string
  corpo: unknown
  cabecalhos: Headers
}

interface Cenario {
  itens?: DivulgacaoDto[]
  etag?: string
  /** Resposta forçada para o próximo `POST`/`PUT` de divulgação. */
  salvar?: () => Response
  /** Resposta forçada para o próximo `POST /divulgacoes/imagens`. */
  upload?: () => Response
}

function montarApi(cenario: Cenario = {}) {
  const chamadas: Chamada[] = []
  const estado = { itens: cenario.itens ?? [], etag: cenario.etag ?? '"v1"', ...cenario }

  vi.stubGlobal(
    'fetch',
    vi.fn(async (url: string, init: RequestInit = {}) => {
      const caminho = url.replace('/api/v1', '')
      const metodo = (init.method ?? 'GET').toUpperCase()
      const corpoBruto = init.body
      chamadas.push({
        metodo,
        caminho,
        corpo: typeof corpoBruto === 'string' ? JSON.parse(corpoBruto) : corpoBruto,
        cabecalhos: new Headers(init.headers),
      })

      if (caminho === '/divulgacoes/imagens' && metodo === 'POST') {
        return estado.upload
          ? estado.upload()
          : json(201, { chave: CHAVE, imagemUrl: `/storage/publico/${CHAVE}` })
      }
      if (caminho.startsWith('/divulgacoes?')) {
        return json(200, {
          itens: estado.itens,
          total: estado.itens.length,
          pagina: 1,
          tamanho: 100,
        })
      }
      const porId = /^\/divulgacoes\/([^/]+)$/.exec(caminho)
      if (porId) {
        const atual = estado.itens.find((d) => d.id === porId[1])
        if (metodo === 'GET') {
          return atual ? json(200, atual, { ETag: estado.etag }) : problema(404, '/erros/x')
        }
        if (metodo === 'PUT') {
          if (estado.salvar) return estado.salvar()
          return json(
            200,
            { ...atual, ...(typeof init.body === 'string' ? JSON.parse(init.body) : {}) },
            { ETag: '"v2"' },
          )
        }
        if (metodo === 'DELETE') {
          estado.itens = estado.itens.filter((d) => d.id !== porId[1])
          return json(204)
        }
      }
      if (caminho === '/divulgacoes' && metodo === 'POST') {
        if (estado.salvar) return estado.salvar()
        return json(201, divulgacao({ id: 'novo' }), { ETag: '"v1"' })
      }
      return problema(404, '/erros/x')
    }),
  )
  return {
    estado,
    chamadas,
    salvamentos: () =>
      chamadas.filter((c) => ['POST', 'PUT'].includes(c.metodo) && !c.caminho.endsWith('/imagens')),
    uploads: () => chamadas.filter((c) => c.caminho === '/divulgacoes/imagens'),
  }
}

function renderPagina() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  render(
    <QueryClientProvider client={queryClient}>
      <AdminDivulgacoesPage />
    </QueryClientProvider>,
  )
}

function arquivo(nome: string, tipo: string, bytes = 100) {
  return new File([new Uint8Array(bytes)], nome, { type: tipo })
}

async function preencherBasico(user: ReturnType<typeof userEvent.setup>) {
  await user.type(screen.getByLabelText('Título'), 'Promoção')
  await user.type(screen.getByLabelText('Mensagem'), 'Tudo com desconto')
}

describe('Administração de divulgações (E3-P04)', () => {
  beforeEach(() => {
    URL.createObjectURL = vi.fn(() => 'blob:previa')
    URL.revokeObjectURL = vi.fn()
  })

  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('lista as divulgações com a situação e avisa da imagem que não carrega', async () => {
    montarApi({
      itens: [
        divulgacao({ inicioExibicaoUtc: null, fimExibicaoUtc: null }),
        divulgacao({
          id: 'd2',
          titulo: 'Rascunho',
          ativo: false,
          imagemChave: null,
          imagemUrl: null,
        }),
        divulgacao({
          id: 'd3',
          titulo: 'Velha',
          inicioExibicaoUtc: '2020-01-01T00:00:00Z',
          fimExibicaoUtc: '2020-02-01T00:00:00Z',
        }),
      ],
    })
    renderPagina()

    const linhas = await screen.findAllByRole('listitem')
    expect(within(linhas[0]).getByText(/Prioridade 10 · No ar/)).toBeInTheDocument()
    expect(within(linhas[1]).getByText(/Inativa$/)).toBeInTheDocument()
    expect(within(linhas[2]).getByText(/Expirada/)).toBeInTheDocument()

    const miniatura = within(linhas[0]).getByAltText('Imagem de Show de sexta')
    miniatura.dispatchEvent(new Event('error'))
    expect(await within(linhas[0]).findByText('Imagem indisponível')).toBeInTheDocument()
  })

  it('cria sem imagem: nenhum upload, janela de Brasília enviada em UTC', async () => {
    const api = montarApi()
    renderPagina()
    const user = userEvent.setup()

    await user.click(await screen.findByRole('button', { name: 'Nova divulgação' }))
    await preencherBasico(user)
    await user.type(screen.getByLabelText(/Início da exibição/), '2026-11-01T09:00')
    await user.type(screen.getByLabelText(/Fim da exibição/), '2026-11-08T18:30')
    await user.clear(screen.getByLabelText(/Prioridade/))
    await user.type(screen.getByLabelText(/Prioridade/), '50')
    await user.click(screen.getByRole('button', { name: 'Criar divulgação' }))

    expect(await screen.findByRole('status')).toHaveTextContent('Divulgação criada.')
    expect(api.uploads()).toHaveLength(0)
    expect(api.salvamentos()[0].corpo).toEqual({
      titulo: 'Promoção',
      mensagem: 'Tudo com desconto',
      imagemChave: null,
      linkDestino: null,
      prioridade: 50,
      inicioExibicaoUtc: '2026-11-01T12:00:00Z',
      fimExibicaoUtc: '2026-11-08T21:30:00Z',
      ativo: true,
    })
  })

  it('a imagem só sobe ao salvar: a prévia é local e o POST leva a chave devolvida', async () => {
    const api = montarApi()
    renderPagina()
    const user = userEvent.setup()

    await user.click(await screen.findByRole('button', { name: 'Nova divulgação' }))
    await preencherBasico(user)
    await user.upload(screen.getByLabelText(/Imagem \(JPEG ou PNG/), arquivo('a.png', 'image/png'))

    expect(screen.getByAltText('Prévia da imagem da divulgação')).toHaveAttribute(
      'src',
      'blob:previa',
    )
    expect(api.uploads()).toHaveLength(0)

    await user.click(screen.getByRole('button', { name: 'Criar divulgação' }))
    await screen.findByRole('status')

    const [upload] = api.uploads()
    expect(upload.corpo).toBeInstanceOf(FormData)
    expect((upload.corpo as FormData).get('file')).toBeInstanceOf(File)
    expect(api.salvamentos()[0].corpo).toMatchObject({ imagemChave: CHAVE })
    // O upload vem antes do salvar.
    expect(api.chamadas.findIndex((c) => c === upload)).toBeLessThan(
      api.chamadas.findIndex((c) => c === api.salvamentos()[0]),
    )
  })

  it('recusa na hora arquivo acima de 2 MB e arquivo que não é JPEG/PNG, sem enviar nada', async () => {
    const api = montarApi()
    renderPagina()
    const user = userEvent.setup({ applyAccept: false })

    await user.click(await screen.findByRole('button', { name: 'Nova divulgação' }))
    const campo = screen.getByLabelText(/Imagem \(JPEG ou PNG/)

    await user.upload(campo, arquivo('grande.png', 'image/png', 2 * 1024 * 1024 + 1))
    expect(screen.getByText('A imagem passa de 2 MB. Escolha uma menor.')).toBeInTheDocument()

    await user.upload(campo, arquivo('a.webp', 'image/webp'))
    expect(screen.getByText('Escolha uma imagem JPEG ou PNG.')).toBeInTheDocument()

    expect(screen.queryByAltText('Prévia da imagem da divulgação')).toBeNull()
    expect(api.chamadas.filter((c) => c.metodo !== 'GET')).toHaveLength(0)
  })

  describe('erros do upload', () => {
    it.each([
      [400, /não foi aceita.*JPEG ou PNG.*4096/],
      [413, /passa de 2 MB/],
      [429, /limite é 30/],
    ])('HTTP %i diz o que fazer e não cria a divulgação', async (status, texto) => {
      const api = montarApi({ upload: () => problema(status, '/erros/x') })
      renderPagina()
      const user = userEvent.setup()

      await user.click(await screen.findByRole('button', { name: 'Nova divulgação' }))
      await preencherBasico(user)
      await user.upload(
        screen.getByLabelText(/Imagem \(JPEG ou PNG/),
        arquivo('a.png', 'image/png'),
      )
      await user.click(screen.getByRole('button', { name: 'Criar divulgação' }))

      expect(await screen.findByRole('alert')).toHaveTextContent(texto)
      expect(api.salvamentos()).toHaveLength(0)
    })
  })

  it('se o salvar falha depois do upload, a nova tentativa reusa a chave (limite de 30 por hora)', async () => {
    const api = montarApi({ salvar: () => problema(500, '/erros/x') })
    renderPagina()
    const user = userEvent.setup()

    await user.click(await screen.findByRole('button', { name: 'Nova divulgação' }))
    await preencherBasico(user)
    await user.upload(screen.getByLabelText(/Imagem \(JPEG ou PNG/), arquivo('a.png', 'image/png'))
    await user.click(screen.getByRole('button', { name: 'Criar divulgação' }))
    await screen.findByRole('alert')

    api.estado.salvar = undefined
    await user.click(screen.getByRole('button', { name: 'Criar divulgação' }))
    await screen.findByRole('status')

    expect(api.uploads()).toHaveLength(1)
    expect(api.salvamentos()).toHaveLength(2)
    expect(api.salvamentos()[1].corpo).toMatchObject({ imagemChave: CHAVE })
  })

  it('editar só o título reenvia a imagemChave e o resto do DTO (o PUT substitui tudo)', async () => {
    const api = montarApi({ itens: [divulgacao()] })
    renderPagina()
    const user = userEvent.setup()

    await user.click(await screen.findByRole('button', { name: 'Editar Show de sexta' }))
    const titulo = await screen.findByLabelText('Título')
    await user.clear(titulo)
    await user.type(titulo, 'Show de sábado')
    await user.click(screen.getByRole('button', { name: 'Salvar alterações' }))

    expect(await screen.findByRole('status')).toHaveTextContent('Divulgação atualizada.')
    const [put] = api.salvamentos()
    expect(put.metodo).toBe('PUT')
    expect(put.caminho).toBe('/divulgacoes/d1')
    expect(put.cabecalhos.get('If-Match')).toBe('"v1"')
    expect(put.corpo).toEqual({
      titulo: 'Show de sábado',
      mensagem: 'Venha ver',
      imagemChave: CHAVE,
      linkDestino: 'https://exemplo.com/show',
      prioridade: 10,
      inicioExibicaoUtc: '2026-11-01T12:00:00Z',
      fimExibicaoUtc: '2026-11-08T12:00:00Z',
      ativo: true,
    })
    expect(api.uploads()).toHaveLength(0)
  })

  it('o fuso volta certo na edição: 12:00Z aparece como 09:00 de Brasília', async () => {
    montarApi({ itens: [divulgacao()] })
    renderPagina()
    const user = userEvent.setup()

    await user.click(await screen.findByRole('button', { name: 'Editar Show de sexta' }))

    expect(await screen.findByLabelText(/Início da exibição/)).toHaveValue('2026-11-01T09:00')
    expect(screen.getByLabelText(/Fim da exibição/)).toHaveValue('2026-11-08T09:00')
  })

  it('"Remover imagem" manda imagemChave null', async () => {
    const api = montarApi({ itens: [divulgacao()] })
    renderPagina()
    const user = userEvent.setup()

    await user.click(await screen.findByRole('button', { name: 'Editar Show de sexta' }))
    await screen.findByLabelText('Título')
    await user.click(screen.getByRole('button', { name: 'Remover imagem' }))
    await user.click(screen.getByRole('button', { name: 'Salvar alterações' }))

    await screen.findByRole('status')
    expect(api.salvamentos()[0].corpo).toMatchObject({ imagemChave: null })
  })

  it('412: avisa, relê a divulgação e remonta o formulário com o ETag novo', async () => {
    const api = montarApi({
      itens: [divulgacao()],
      salvar: () => problema(412, '/erros/versao-desatualizada'),
    })
    renderPagina()
    const user = userEvent.setup()

    await user.click(await screen.findByRole('button', { name: 'Editar Show de sexta' }))
    await user.click(await screen.findByRole('button', { name: 'Salvar alterações' }))

    await waitFor(() =>
      expect(screen.getByRole('alert')).toHaveTextContent('alterado por outra pessoa'),
    )
    // GET do detalhe de novo: a releitura que traz o ETag atual.
    await waitFor(() =>
      expect(
        api.chamadas.filter((c) => c.metodo === 'GET' && c.caminho === '/divulgacoes/d1'),
      ).toHaveLength(2),
    )
  })

  it('link sem https:// é recusado no formulário; o 400 do servidor aparece no campo do link', async () => {
    const api = montarApi({
      salvar: () =>
        problema(400, '/erros/validacao', {
          errors: { LinkDestino: ['O link não é aceito.'] },
        }),
    })
    renderPagina()
    const user = userEvent.setup()

    await user.click(await screen.findByRole('button', { name: 'Nova divulgação' }))
    await preencherBasico(user)
    const link = screen.getByLabelText(/Link/)

    await user.type(link, 'http://exemplo.com')
    await user.click(screen.getByRole('button', { name: 'Criar divulgação' }))
    expect(screen.getByText(/precisa começar com https:\/\//)).toBeInTheDocument()
    expect(api.salvamentos()).toHaveLength(0)

    await user.clear(link)
    await user.type(link, 'https://10.0.0.1/')
    await user.click(screen.getByRole('button', { name: 'Criar divulgação' }))
    expect(await screen.findByText('O link não é aceito.')).toBeInTheDocument()
    expect(link).toHaveAttribute('aria-invalid', 'true')
  })

  it('prioridade fora de 0 a 100 e fim antes do início não são enviados', async () => {
    const api = montarApi()
    renderPagina()
    const user = userEvent.setup()

    await user.click(await screen.findByRole('button', { name: 'Nova divulgação' }))
    await preencherBasico(user)
    await user.clear(screen.getByLabelText(/Prioridade/))
    await user.type(screen.getByLabelText(/Prioridade/), '101')
    await user.type(screen.getByLabelText(/Início da exibição/), '2026-11-08T09:00')
    await user.type(screen.getByLabelText(/Fim da exibição/), '2026-11-01T09:00')
    await user.click(screen.getByRole('button', { name: 'Criar divulgação' }))

    expect(screen.getByText('A prioridade é um número inteiro de 0 a 100.')).toBeInTheDocument()
    expect(screen.getByText('O fim precisa ser depois do início.')).toBeInTheDocument()
    expect(api.salvamentos()).toHaveLength(0)
  })

  it('remover pede confirmação e manda o DELETE', async () => {
    const api = montarApi({ itens: [divulgacao()] })
    renderPagina()
    const user = userEvent.setup()

    await user.click(await screen.findByRole('button', { name: 'Remover Show de sexta' }))
    expect(api.chamadas.some((c) => c.metodo === 'DELETE')).toBe(false)
    await user.click(screen.getByRole('button', { name: 'Confirmar remoção' }))

    expect(await screen.findByRole('status')).toHaveTextContent('Divulgação removida.')
    expect(api.chamadas.find((c) => c.metodo === 'DELETE')?.caminho).toBe('/divulgacoes/d1')
    expect(await screen.findByText('Nenhuma divulgação cadastrada.')).toBeInTheDocument()
  })
})
