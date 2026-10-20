import { render, screen, waitFor, within, fireEvent } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { AdminGradePage } from './AdminGradePage'

interface Chamada {
  metodo: string
  caminho: string
  corpo: unknown
  headers: Headers
}

const ETAG_1 = '"AAAAAAAAB9E="'
const ETAG_2 = '"AAAAAAAAB9I="'

function json(status: number, body: unknown, headers: Record<string, string> = {}) {
  return new Response(body === undefined ? null : JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json', ...headers },
  })
}

function problema(status: number, type: string, extras: object = {}) {
  return json(status, { type, title: 'x', status, ...extras })
}

function programa(extras: Partial<Record<string, unknown>> = {}) {
  return {
    id: 'p1',
    titulo: 'Café da Manhã',
    descricao: 'Música e notícias',
    locutor: { id: 'l1', nome: 'Ana' },
    // 2026-01-14 09:00–11:00 em Brasília
    inicioUtc: '2026-01-14T12:00:00Z',
    fimUtc: '2026-01-14T14:00:00Z',
    status: 'Agendado',
    ...extras,
  }
}

const usuarios = (role: string) => {
  const itens =
    role === 'Locutor'
      ? [
          { id: 'l1', nome: 'Ana', email: 'a@a', role: 'Locutor', ativo: true },
          { id: 'l2', nome: 'Beto', email: 'b@b', role: 'Locutor', ativo: true },
          { id: 'l3', nome: 'Inativo', email: 'i@i', role: 'Locutor', ativo: false },
        ]
      : [{ id: 'a1', nome: 'Dona Admin', email: 'd@d', role: 'Admin', ativo: true }]
  return { itens, total: itens.length, pagina: 1, tamanho: 100 }
}

type Handler = (c: Chamada) => Response | Promise<Response> | undefined

/** Servidor falso: cada teste sobrescreve só as rotas que importam; o resto devolve o padrão. */
function montarApi(sobrescritas: Handler[] = []) {
  const chamadas: Chamada[] = []
  let lista = [
    programa(),
    programa({
      id: 'p2',
      titulo: 'Tarde Rock',
      status: 'AoVivo',
      inicioUtc: '2026-01-14T18:00:00Z',
      fimUtc: '2026-01-14T20:00:00Z',
    }),
    programa({
      id: 'p3',
      titulo: 'Programa Velho',
      status: 'Cancelado',
      inicioUtc: '2026-01-15T12:00:00Z',
      fimUtc: '2026-01-15T13:00:00Z',
    }),
  ]

  const fetchMock = vi.fn(async (url: string, init: RequestInit = {}) => {
    const caminho = url.replace('/api/v1', '')
    const chamada: Chamada = {
      metodo: (init.method ?? 'GET').toUpperCase(),
      caminho,
      corpo: init.body ? JSON.parse(init.body as string) : undefined,
      headers: new Headers(init.headers),
    }
    chamadas.push(chamada)

    for (const h of sobrescritas) {
      const r = await h(chamada)
      if (r) return r
    }

    if (chamada.metodo === 'GET' && caminho.startsWith('/usuarios')) {
      return json(200, usuarios(new URL(caminho, 'http://x').searchParams.get('role') ?? ''))
    }
    if (chamada.metodo === 'GET' && caminho.startsWith('/programas?')) return json(200, lista)
    const detalhe = /^\/programas\/([^/?]+)$/.exec(caminho)
    if (chamada.metodo === 'GET' && detalhe) {
      const achado = lista.find((p) => p.id === detalhe[1])
      return achado ? json(200, achado, { ETag: ETAG_1 }) : problema(404, '/erros/nao-encontrado')
    }
    if (chamada.metodo === 'POST' && caminho === '/programas') {
      const criado = programa({ id: 'novo', ...(chamada.corpo as object) })
      lista = [...lista, criado]
      return json(201, criado, { ETag: ETAG_1 })
    }
    if (chamada.metodo === 'PUT') return json(200, programa(), { ETag: ETAG_2 })
    if (chamada.metodo === 'POST' && caminho.endsWith('/cancelar')) {
      return json(200, programa({ status: 'Cancelado' }), { ETag: ETAG_2 })
    }
    return problema(500, '/erros/inesperado')
  })

  vi.stubGlobal('fetch', fetchMock)
  return { chamadas, fetchMock }
}

function renderAdmin() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={['/admin/grade']}>
        <AdminGradePage />
      </MemoryRouter>
    </QueryClientProvider>,
  )
}

function preencher(rotulo: RegExp | string, valor: string) {
  fireEvent.change(screen.getByLabelText(rotulo), { target: { value: valor } })
}

async function abrirNovoPrograma(user: ReturnType<typeof userEvent.setup>) {
  await user.click(await screen.findByRole('button', { name: 'Novo programa' }))
  const form = await screen.findByRole('form', { name: 'Novo programa' })
  // Os locutores chegam por uma query à parte: espera a opção aparecer.
  await within(form).findByRole('option', { name: 'Beto' })
  return form
}

describe('AdminGradePage', () => {
  beforeEach(() => {
    // Quarta-feira 2026-01-14 11:00 em Brasília: a semana atual é 12/01 a 18/01.
    vi.useFakeTimers({ toFake: ['Date'] })
    vi.setSystemTime(new Date('2026-01-14T14:00:00Z'))
  })

  afterEach(() => {
    vi.useRealTimers()
    vi.restoreAllMocks()
  })

  describe('lista', () => {
    it('pede também os cancelados e só oferece Editar/Cancelar para programa Agendado', async () => {
      const { chamadas } = montarApi()

      renderAdmin()

      expect(await screen.findByText('Café da Manhã')).toBeInTheDocument()
      expect(chamadas.find((c) => c.caminho.startsWith('/programas?'))!.caminho).toContain(
        'incluirCancelados=true',
      )
      expect(screen.getByText('Programa Velho').closest('li')).toHaveTextContent('Cancelado')
      expect(screen.getByRole('button', { name: 'Editar Café da Manhã' })).toBeInTheDocument()
      expect(screen.getByRole('button', { name: 'Cancelar Café da Manhã' })).toBeInTheDocument()
      // AoVivo e Cancelado não aceitam edição nem cancelamento.
      expect(screen.queryByRole('button', { name: 'Editar Tarde Rock' })).not.toBeInTheDocument()
      expect(screen.queryByRole('button', { name: 'Cancelar Tarde Rock' })).not.toBeInTheDocument()
      expect(
        screen.queryByRole('button', { name: 'Editar Programa Velho' }),
      ).not.toBeInTheDocument()
    })

    it('mostra o horário de Brasília do programa (início e fim)', async () => {
      montarApi()

      renderAdmin()

      const linha = (await screen.findByText('Café da Manhã')).closest('li')!
      expect(linha).toHaveTextContent('09:00')
      expect(linha).toHaveTextContent('11:00')
    })
  })

  describe('locutores', () => {
    it('lê todas as páginas: o 101º locutor também aparece na lista', async () => {
      const locutor = (n: number) => ({
        id: `m${n}`,
        nome: `Locutor ${String(n).padStart(3, '0')}`,
        email: `m${n}@x`,
        role: 'Locutor',
        ativo: true,
      })
      const { chamadas } = montarApi([
        (c) => {
          if (!c.caminho.startsWith('/usuarios?role=Locutor')) return undefined
          const pagina = Number(new URL(c.caminho, 'http://x').searchParams.get('pagina'))
          const itens =
            pagina === 1 ? Array.from({ length: 100 }, (_, i) => locutor(i + 1)) : [locutor(101)]
          return json(200, { itens, total: 101, pagina, tamanho: 100 })
        },
      ])
      const user = userEvent.setup()
      renderAdmin()

      await user.click(await screen.findByRole('button', { name: 'Novo programa' }))
      const form = await screen.findByRole('form', { name: 'Novo programa' })

      expect(await within(form).findByRole('option', { name: 'Locutor 101' })).toBeInTheDocument()
      const paginas = chamadas
        .filter((c) => c.caminho.startsWith('/usuarios?role=Locutor'))
        .map((c) => new URL(c.caminho, 'http://x').searchParams.get('pagina'))
      expect(paginas).toEqual(['1', '2'])
    })
  })

  describe('criar', () => {
    it('converte o horário de Brasília para UTC, envia o POST e recarrega a lista', async () => {
      const { chamadas } = montarApi()
      const user = userEvent.setup()
      renderAdmin()

      const form = await abrirNovoPrograma(user)
      preencher('Título', '  Jornal da Noite  ')
      preencher('Descrição', 'Notícias')
      await user.selectOptions(within(form).getByLabelText('Locutor'), 'l2')
      preencher(/^Início/, '2026-01-16T20:00')
      preencher(/^Fim/, '2026-01-16T21:30')
      const listagensAntes = chamadas.filter((c) => c.caminho.startsWith('/programas?')).length
      await user.click(within(form).getByRole('button', { name: 'Criar programa' }))

      expect(await screen.findByRole('status')).toHaveTextContent('Programa criado.')
      const post = chamadas.find((c) => c.metodo === 'POST' && c.caminho === '/programas')!
      expect(post.corpo).toEqual({
        titulo: 'Jornal da Noite',
        descricao: 'Notícias',
        locutorId: 'l2',
        inicioUtc: '2026-01-16T23:00:00Z',
        fimUtc: '2026-01-17T00:30:00Z',
      })
      expect(screen.queryByRole('form', { name: 'Novo programa' })).not.toBeInTheDocument()
      await waitFor(() =>
        expect(chamadas.filter((c) => c.caminho.startsWith('/programas?')).length).toBeGreaterThan(
          listagensAntes,
        ),
      )
    })

    it('sugere a próxima hora cheia de Brasília e oferece só locutores e admins ativos', async () => {
      montarApi()
      const user = userEvent.setup()
      renderAdmin()

      const form = await abrirNovoPrograma(user)

      expect(within(form).getByLabelText(/^Início/)).toHaveValue('2026-01-14T12:00')
      expect(within(form).getByLabelText(/^Fim/)).toHaveValue('2026-01-14T13:00')
      const opcoes = within(form)
        .getAllByRole('option')
        .map((o) => o.textContent)
      expect(opcoes).toContain('Ana')
      expect(opcoes).toContain('Dona Admin (Admin)')
      expect(opcoes).not.toContain('Inativo')
    })

    it('valida no cliente: fim antes do início não chama a API', async () => {
      const { chamadas } = montarApi()
      const user = userEvent.setup()
      renderAdmin()

      const form = await abrirNovoPrograma(user)
      preencher('Título', 'X')
      await user.selectOptions(within(form).getByLabelText('Locutor'), 'l1')
      preencher(/^Início/, '2026-01-16T20:00')
      preencher(/^Fim/, '2026-01-16T19:00')
      await user.click(within(form).getByRole('button', { name: 'Criar programa' }))

      expect(
        await within(form).findByText(/o fim precisa ser depois do início/i),
      ).toBeInTheDocument()
      expect(chamadas.some((c) => c.metodo === 'POST')).toBe(false)
    })

    it.each([
      ['menos de 5 minutos', '2026-01-16T20:00', '2026-01-16T20:03'],
      ['mais de 12 horas', '2026-01-16T08:00', '2026-01-16T20:30'],
    ])('valida a duração (%s) sem chamar a API', async (_nome, inicio, fim) => {
      const { chamadas } = montarApi()
      const user = userEvent.setup()
      renderAdmin()

      const form = await abrirNovoPrograma(user)
      preencher('Título', 'X')
      await user.selectOptions(within(form).getByLabelText('Locutor'), 'l1')
      preencher(/^Início/, inicio)
      preencher(/^Fim/, fim)
      await user.click(within(form).getByRole('button', { name: 'Criar programa' }))

      expect(await within(form).findByText(/entre 5 minutos e 12 horas/i)).toBeInTheDocument()
      expect(chamadas.some((c) => c.metodo === 'POST')).toBe(false)
    })

    it('exige título e locutor', async () => {
      const { chamadas } = montarApi()
      const user = userEvent.setup()
      renderAdmin()

      const form = await abrirNovoPrograma(user)
      await user.click(within(form).getByRole('button', { name: 'Criar programa' }))

      expect(await within(form).findByText('Informe o título.')).toBeInTheDocument()
      expect(within(form).getByText('Escolha o locutor.')).toBeInTheDocument()
      expect(chamadas.some((c) => c.metodo === 'POST')).toBe(false)
    })

    it('409: mostra qual programa conflita (título, horário e locutor) e mantém o formulário', async () => {
      montarApi([
        (c) =>
          c.metodo === 'POST' && c.caminho === '/programas'
            ? problema(409, '/erros/conflito-grade', { programaConflitanteId: 'p2' })
            : undefined,
      ])
      const user = userEvent.setup()
      renderAdmin()

      const form = await abrirNovoPrograma(user)
      preencher('Título', 'Novo')
      await user.selectOptions(within(form).getByLabelText('Locutor'), 'l1')
      preencher(/^Início/, '2026-01-14T15:00')
      preencher(/^Fim/, '2026-01-14T16:00')
      await user.click(within(form).getByRole('button', { name: 'Criar programa' }))

      const alerta = await within(form).findByRole('alert')
      expect(alerta).toHaveTextContent('Esse horário já está ocupado por outro programa.')
      // O programa conflitante é buscado e descrito: 18:00Z–20:00Z = 15:00–17:00 em Brasília.
      const conflito = await within(form).findByTestId('conflito-grade')
      expect(conflito).toHaveTextContent('Tarde Rock')
      expect(conflito).toHaveTextContent('15:00')
      expect(conflito).toHaveTextContent('Ana')
      // O que o admin digitou continua lá para ele ajustar o horário.
      expect(within(form).getByLabelText('Título')).toHaveValue('Novo')
    })

    it('409 mesmo sem conseguir descrever o conflitante: a mensagem principal permanece', async () => {
      montarApi([
        (c) =>
          c.metodo === 'POST' && c.caminho === '/programas'
            ? problema(409, '/erros/conflito-grade', { programaConflitanteId: 'sumiu' })
            : undefined,
      ])
      const user = userEvent.setup()
      renderAdmin()

      const form = await abrirNovoPrograma(user)
      preencher('Título', 'Novo')
      await user.selectOptions(within(form).getByLabelText('Locutor'), 'l1')
      preencher(/^Início/, '2026-01-14T15:00')
      preencher(/^Fim/, '2026-01-14T16:00')
      await user.click(within(form).getByRole('button', { name: 'Criar programa' }))

      expect(await within(form).findByRole('alert')).toHaveTextContent(/já está ocupado/i)
      expect(within(form).queryByTestId('conflito-grade')).not.toBeInTheDocument()
    })

    it('400 do servidor: a mensagem de cada campo aparece no campo certo', async () => {
      montarApi([
        (c) =>
          c.metodo === 'POST' && c.caminho === '/programas'
            ? problema(400, '/erros/validacao', {
                errors: { InicioUtc: ['O início não pode estar mais de 1 minuto no passado.'] },
              })
            : undefined,
      ])
      const user = userEvent.setup()
      renderAdmin()

      const form = await abrirNovoPrograma(user)
      preencher('Título', 'Novo')
      await user.selectOptions(within(form).getByLabelText('Locutor'), 'l1')
      preencher(/^Início/, '2026-01-14T09:00')
      preencher(/^Fim/, '2026-01-14T10:00')
      await user.click(within(form).getByRole('button', { name: 'Criar programa' }))

      const campo = within(form).getByLabelText(/^Início/)
      await waitFor(() =>
        expect(campo).toHaveAccessibleDescription(
          'O início não pode estar mais de 1 minuto no passado.',
        ),
      )
      expect(campo).toBeInvalid()
    })

    it('Cancelar fecha o formulário sem chamar a API', async () => {
      const { chamadas } = montarApi()
      const user = userEvent.setup()
      renderAdmin()

      const form = await abrirNovoPrograma(user)
      await user.click(within(form).getByRole('button', { name: 'Descartar' }))

      expect(screen.queryByRole('form', { name: 'Novo programa' })).not.toBeInTheDocument()
      expect(chamadas.some((c) => c.metodo === 'POST')).toBe(false)
    })
  })

  describe('editar', () => {
    it('lê o ETag do GET do programa, mostra o horário de Brasília e envia o PUT com If-Match', async () => {
      const { chamadas } = montarApi()
      const user = userEvent.setup()
      renderAdmin()

      await user.click(await screen.findByRole('button', { name: 'Editar Café da Manhã' }))
      const form = await screen.findByRole('form', { name: 'Editar programa' })
      await within(form).findByRole('option', { name: 'Beto' })

      expect(within(form).getByLabelText('Título')).toHaveValue('Café da Manhã')
      expect(within(form).getByLabelText(/^Início/)).toHaveValue('2026-01-14T09:00')
      expect(within(form).getByLabelText(/^Fim/)).toHaveValue('2026-01-14T11:00')

      preencher('Título', 'Café Reforçado')
      await user.click(within(form).getByRole('button', { name: 'Salvar alterações' }))

      expect(await screen.findByRole('status')).toHaveTextContent('Programa atualizado.')
      const put = chamadas.find((c) => c.metodo === 'PUT')!
      expect(put.caminho).toBe('/programas/p1')
      expect(put.headers.get('If-Match')).toBe(ETAG_1)
      expect(put.corpo).toMatchObject({
        titulo: 'Café Reforçado',
        locutorId: 'l1',
        inicioUtc: '2026-01-14T12:00:00Z',
        fimUtc: '2026-01-14T14:00:00Z',
      })
    })

    it('sem ETag no GET do programa, não envia o PUT (nunca arrisca sobrescrever uma edição alheia)', async () => {
      const { chamadas } = montarApi([
        (c) =>
          c.metodo === 'GET' && c.caminho === '/programas/p1' ? json(200, programa()) : undefined,
      ])
      const user = userEvent.setup()
      renderAdmin()

      await user.click(await screen.findByRole('button', { name: 'Editar Café da Manhã' }))
      const form = await screen.findByRole('form', { name: 'Editar programa' })
      await within(form).findByRole('option', { name: 'Beto' })
      await user.click(within(form).getByRole('button', { name: 'Salvar alterações' }))

      expect(await screen.findByRole('alert')).toHaveTextContent(/confirmar a versão/i)
      expect(chamadas.some((c) => c.metodo === 'PUT')).toBe(false)
    })

    it('mantém no seletor o locutor do programa mesmo que ele não esteja mais na lista', async () => {
      montarApi([
        (c) =>
          c.metodo === 'GET' && c.caminho === '/programas/p1'
            ? json(200, programa({ locutor: { id: 'fora', nome: 'Ex-locutor' } }), { ETag: ETAG_1 })
            : undefined,
      ])
      const user = userEvent.setup()
      renderAdmin()

      await user.click(await screen.findByRole('button', { name: 'Editar Café da Manhã' }))
      const form = await screen.findByRole('form', { name: 'Editar programa' })

      expect(
        await within(form).findByRole('option', { name: 'Ex-locutor (fora da lista)' }),
      ).toBeInTheDocument()
      expect(within(form).getByLabelText('Locutor')).toHaveValue('fora')
    })

    it('412: avisa, recarrega o programa com o ETag novo e o próximo envio usa o ETag novo', async () => {
      let leituras = 0
      const { chamadas } = montarApi([
        (c) => {
          if (c.metodo === 'GET' && c.caminho === '/programas/p1') {
            leituras += 1
            return json(
              200,
              programa({ titulo: leituras === 1 ? 'Café da Manhã' : 'Café (alterado por Beto)' }),
              { ETag: leituras === 1 ? ETAG_1 : ETAG_2 },
            )
          }
          if (c.metodo === 'PUT' && c.headers.get('If-Match') === ETAG_1) {
            return problema(412, '/erros/versao-desatualizada')
          }
          return undefined
        },
      ])
      const user = userEvent.setup()
      renderAdmin()

      await user.click(await screen.findByRole('button', { name: 'Editar Café da Manhã' }))
      const form = await screen.findByRole('form', { name: 'Editar programa' })
      await within(form).findByRole('option', { name: 'Beto' })
      preencher('Título', 'Minha edição')
      await user.click(within(form).getByRole('button', { name: 'Salvar alterações' }))

      expect(await screen.findByRole('alert')).toHaveTextContent(/alterado por outra pessoa/i)
      // O formulário passa a mostrar a versão que está no servidor.
      await waitFor(() =>
        expect(screen.getByLabelText('Título')).toHaveValue('Café (alterado por Beto)'),
      )

      await user.click(screen.getByRole('button', { name: 'Salvar alterações' }))
      expect(await screen.findByRole('status')).toHaveTextContent('Programa atualizado.')
      const puts = chamadas.filter((c) => c.metodo === 'PUT')
      expect(puts.map((p) => p.headers.get('If-Match'))).toEqual([ETAG_1, ETAG_2])
    })

    it('409 de conflito de horário na edição também diz quem conflita', async () => {
      montarApi([
        (c) =>
          c.metodo === 'PUT'
            ? problema(409, '/erros/conflito-grade', { programaConflitanteId: 'p2' })
            : undefined,
      ])
      const user = userEvent.setup()
      renderAdmin()

      await user.click(await screen.findByRole('button', { name: 'Editar Café da Manhã' }))
      const form = await screen.findByRole('form', { name: 'Editar programa' })
      await within(form).findByRole('option', { name: 'Beto' })
      await user.click(within(form).getByRole('button', { name: 'Salvar alterações' }))

      expect(await within(form).findByTestId('conflito-grade')).toHaveTextContent('Tarde Rock')
    })

    it('programa que deixou de ser Agendado (409 transição inválida): explica e recarrega a lista', async () => {
      const { chamadas } = montarApi([
        (c) => (c.metodo === 'PUT' ? problema(409, '/erros/transicao-invalida') : undefined),
      ])
      const user = userEvent.setup()
      renderAdmin()

      await user.click(await screen.findByRole('button', { name: 'Editar Café da Manhã' }))
      const form = await screen.findByRole('form', { name: 'Editar programa' })
      await within(form).findByRole('option', { name: 'Beto' })
      const listagensAntes = chamadas.filter((c) => c.caminho.startsWith('/programas?')).length
      await user.click(within(form).getByRole('button', { name: 'Salvar alterações' }))

      // O formulário é remontado com o programa relido: a mensagem tem que sobreviver à recarga.
      expect(await screen.findByRole('alert')).toHaveTextContent(/mudou de estado/i)
      expect(await screen.findByRole('form', { name: 'Editar programa' })).toBeInTheDocument()
      await waitFor(() =>
        expect(chamadas.filter((c) => c.caminho.startsWith('/programas?')).length).toBeGreaterThan(
          listagensAntes,
        ),
      )
    })

    it('programa inexistente ao abrir a edição: mostra o erro e não abre formulário', async () => {
      montarApi([
        (c) =>
          c.metodo === 'GET' && c.caminho === '/programas/p1'
            ? problema(404, '/erros/nao-encontrado')
            : undefined,
      ])
      const user = userEvent.setup()
      renderAdmin()

      await user.click(await screen.findByRole('button', { name: 'Editar Café da Manhã' }))

      expect(await screen.findByRole('alert')).toHaveTextContent(/não encontrado/i)
      expect(screen.queryByRole('form', { name: 'Editar programa' })).not.toBeInTheDocument()
    })
  })

  describe('cancelar', () => {
    it('pede confirmação e só então chama POST /cancelar; depois recarrega a lista', async () => {
      const { chamadas } = montarApi()
      const user = userEvent.setup()
      renderAdmin()

      await user.click(await screen.findByRole('button', { name: 'Cancelar Café da Manhã' }))
      expect(chamadas.some((c) => c.caminho.endsWith('/cancelar'))).toBe(false)

      const listagensAntes = chamadas.filter((c) => c.caminho.startsWith('/programas?')).length
      await user.click(screen.getByRole('button', { name: 'Confirmar cancelamento' }))

      expect(await screen.findByRole('status')).toHaveTextContent('Programa cancelado.')
      const cancelar = chamadas.find((c) => c.caminho.endsWith('/cancelar'))!
      expect(cancelar.metodo).toBe('POST')
      expect(cancelar.caminho).toBe('/programas/p1/cancelar')
      await waitFor(() =>
        expect(chamadas.filter((c) => c.caminho.startsWith('/programas?')).length).toBeGreaterThan(
          listagensAntes,
        ),
      )
    })

    it('"Manter" desiste do cancelamento sem chamar a API', async () => {
      const { chamadas } = montarApi()
      const user = userEvent.setup()
      renderAdmin()

      await user.click(await screen.findByRole('button', { name: 'Cancelar Café da Manhã' }))
      await user.click(screen.getByRole('button', { name: 'Manter' }))

      expect(
        screen.queryByRole('button', { name: 'Confirmar cancelamento' }),
      ).not.toBeInTheDocument()
      expect(chamadas.some((c) => c.caminho.endsWith('/cancelar'))).toBe(false)
    })

    it('409 (o programa já começou): mostra a mensagem útil e recarrega a lista', async () => {
      const { chamadas } = montarApi([
        (c) =>
          c.caminho.endsWith('/cancelar') ? problema(409, '/erros/transicao-invalida') : undefined,
      ])
      const user = userEvent.setup()
      renderAdmin()

      await user.click(await screen.findByRole('button', { name: 'Cancelar Café da Manhã' }))
      const listagensAntes = chamadas.filter((c) => c.caminho.startsWith('/programas?')).length
      await user.click(screen.getByRole('button', { name: 'Confirmar cancelamento' }))

      expect(await screen.findByRole('alert')).toHaveTextContent(/mudou de estado/i)
      await waitFor(() =>
        expect(chamadas.filter((c) => c.caminho.startsWith('/programas?')).length).toBeGreaterThan(
          listagensAntes,
        ),
      )
    })
  })
})
