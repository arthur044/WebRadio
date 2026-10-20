import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { useAuthStore } from '../../shared/api/authStore'
import { limparCachePrivado } from '../../shared/api/limparCacheAoSair'
import { EstudioPage } from './EstudioPage'

// 43 caracteres de [A-Za-z0-9_-], como o servidor gera (06 §Credencial de transmissão).
const SEGREDO = 'Zk3_aB9-xQ7mP2vN8cL5tR1wY6uH4jE0sD3fG7hK2pM'
const IDENTIFICADOR = '3f2a9c1e-7b64-4d85-9a10-5c8e2d7b6f41'
const CONEXAO = { host: 'radio.exemplo.org', porta: 8005, mount: '/live' }

interface Chamada {
  metodo: string
  caminho: string
  corpo: unknown
}

function json(status: number, body?: unknown, headers: Record<string, string> = {}) {
  return new Response(body === undefined ? null : JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json', ...headers },
  })
}

function problema(status: number, type: string, extras: object = {}) {
  return json(status, { type, title: 'x', status, ...extras })
}

interface Cenario {
  credencial?: {
    identificador: string
    criadaEmUtc: string
    ultimoUsoEmUtc: string | null
    conexao?: object | null
  } | null
  conexao?: object | null
  aoVivo?: { noAr: boolean; programaId?: string; locutorNome?: string }
  programaEmCurso?: object | null
  ativas?: object[]
  gerar?: () => Response
}

/** Servidor falso do Estúdio. Registra as chamadas para os testes conferirem rota, método e corpo. */
function montarApi(cenario: Cenario = {}) {
  const chamadas: Chamada[] = []
  const estado = { credencial: cenario.credencial ?? null, ativas: cenario.ativas ?? [] }

  const fetchMock = vi.fn(async (url: string, init: RequestInit = {}) => {
    const caminho = url.replace('/api/v1', '')
    const metodo = (init.method ?? 'GET').toUpperCase()
    chamadas.push({
      metodo,
      caminho,
      corpo: init.body ? JSON.parse(init.body as string) : undefined,
    })

    if (caminho === '/stream/info') {
      return json(200, {
        url: '/stream/radio.mp3',
        formato: 'audio/mpeg',
        bitrateKbps: 128,
        tocandoAgora: null,
        aoVivo: cenario.aoVivo ?? { noAr: false },
      })
    }
    if (caminho === '/programas/ao-vivo') {
      return cenario.programaEmCurso ? json(200, cenario.programaEmCurso) : json(204)
    }
    if (caminho.startsWith('/programas?')) return json(200, [])
    if (/^\/programas\/[^/]+\/encerrar$/.test(caminho) && metodo === 'POST') {
      return json(200, { ...cenario.programaEmCurso, status: 'Finalizado' })
    }
    if (caminho === '/locutores/me/credencial-transmissao') {
      if (metodo === 'GET') return estado.credencial ? json(200, estado.credencial) : json(204)
      if (metodo === 'POST') {
        const resposta =
          cenario.gerar?.() ??
          json(
            201,
            {
              identificador: IDENTIFICADOR,
              segredo: SEGREDO,
              criadaEmUtc: new Date().toISOString(),
              conexao: cenario.conexao === undefined ? CONEXAO : cenario.conexao,
            },
            { 'Cache-Control': 'no-store' },
          )
        if (resposta.status === 201) {
          estado.credencial = {
            identificador: IDENTIFICADOR,
            criadaEmUtc: new Date().toISOString(),
            ultimoUsoEmUtc: null,
            conexao: cenario.conexao === undefined ? CONEXAO : cenario.conexao,
          }
        }
        return resposta
      }
      if (metodo === 'DELETE') {
        estado.credencial = null
        return json(204)
      }
    }
    if (caminho === '/credenciais-transmissao') return json(200, estado.ativas)
    const revogar = /^\/usuarios\/([^/]+)\/credencial-transmissao$/.exec(caminho)
    if (revogar && metodo === 'DELETE') {
      estado.ativas = estado.ativas.filter(
        (a) => (a as { usuarioId: string }).usuarioId !== revogar[1],
      )
      return json(204)
    }
    return problema(500, '/erros/inesperado')
  })

  vi.stubGlobal('fetch', fetchMock)
  return { chamadas, fetchMock }
}

function entrarComo(role: 'Locutor' | 'Admin', id = 'u1') {
  useAuthStore.setState({
    accessToken: 'token-de-teste',
    restoring: false,
    usuario: { id, nome: 'Ana', email: 'ana@x', role, deveTrocarSenha: false },
  })
}

function renderEstudio() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  const utils = render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={['/estudio']}>
        <EstudioPage />
      </MemoryRouter>
    </QueryClientProvider>,
  )
  return { queryClient, ...utils }
}

async function gerarCredencial(user: ReturnType<typeof userEvent.setup>, senha = 'minha-senha') {
  await user.click(await screen.findByRole('button', { name: /^Gerar (nova )?credencial$/ }))
  const dialogo = await screen.findByRole('dialog')
  await user.type(within(dialogo).getByLabelText('Senha da conta'), senha)
  await user.click(within(dialogo).getByRole('button', { name: 'Gerar' }))
  return dialogo
}

/** Tudo que o browser guardaria do segredo: storage, cache de queries e de mutations, e o DOM. */
function onde(segredo: string, queryClient: QueryClient): string[] {
  const achados: string[] = []
  for (const [nome, storage] of [
    ['localStorage', localStorage],
    ['sessionStorage', sessionStorage],
  ] as const) {
    for (let i = 0; i < storage.length; i++) {
      const chave = storage.key(i)!
      if (chave.includes(segredo) || (storage.getItem(chave) ?? '').includes(segredo)) {
        achados.push(`${nome}:${chave}`)
      }
    }
  }
  for (const query of queryClient.getQueryCache().getAll()) {
    if (JSON.stringify(query.state.data ?? null).includes(segredo)) {
      achados.push(`query:${JSON.stringify(query.queryKey)}`)
    }
  }
  for (const mutation of queryClient.getMutationCache().getAll()) {
    const guardado = JSON.stringify([mutation.state.data ?? null, mutation.state.variables ?? null])
    if (guardado.includes(segredo)) achados.push('mutation')
  }
  if (document.body.textContent?.includes(segredo)) achados.push('dom')
  return achados
}

beforeEach(() => {
  localStorage.clear()
  sessionStorage.clear()
})

afterEach(() => {
  vi.unstubAllGlobals()
  useAuthStore.setState({ accessToken: null, usuario: null, restoring: true })
})

describe('Credencial de transmissão: o segredo', () => {
  it('aparece uma vez, com aviso e botão de copiar', async () => {
    entrarComo('Locutor')
    montarApi()
    const user = userEvent.setup()
    renderEstudio()

    const dialogo = await gerarCredencial(user)

    expect(await within(dialogo).findByTestId('segredo-gerado')).toHaveTextContent(SEGREDO)
    expect(within(dialogo).getByText(/não será mostrado de novo/i)).toBeInTheDocument()
    expect(within(dialogo).getByText(IDENTIFICADOR)).toBeInTheDocument()

    await user.click(within(dialogo).getByRole('button', { name: 'Copiar' }))
    expect(await navigator.clipboard.readText()).toBe(SEGREDO)
  })

  it('não fica em localStorage, sessionStorage nem no cache do TanStack Query, aberto ou depois de fechar', async () => {
    entrarComo('Locutor')
    montarApi()
    const user = userEvent.setup()
    const { queryClient } = renderEstudio()

    const dialogo = await gerarCredencial(user)
    await within(dialogo).findByTestId('segredo-gerado')

    // Com o diálogo aberto, só o DOM do próprio diálogo pode ter o segredo.
    expect(onde(SEGREDO, queryClient)).toEqual(['dom'])

    await user.click(within(dialogo).getByRole('button', { name: 'Já copiei, fechar' }))
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())

    // A credencial foi relida (sem segredo) e o painel mostra o identificador; o segredo sumiu de todo lugar.
    expect(await screen.findByText(IDENTIFICADOR)).toBeInTheDocument()
    expect(onde(SEGREDO, queryClient)).toEqual([])
    // O refetch trouxe a credencial, mas o GET nunca devolve o segredo.
    expect(queryClient.getQueryData(['credencial-transmissao'])).not.toHaveProperty('segredo')
  })

  it('o Esc não fecha o diálogo depois de gerar, para não perder o segredo sem querer', async () => {
    entrarComo('Locutor')
    montarApi()
    const user = userEvent.setup()
    renderEstudio()

    const dialogo = await gerarCredencial(user)
    await within(dialogo).findByTestId('segredo-gerado')
    await user.keyboard('{Escape}')

    expect(screen.getByRole('dialog')).toBeInTheDocument()
  })

  it('o Esc cancela antes de gerar', async () => {
    entrarComo('Locutor')
    montarApi()
    const user = userEvent.setup()
    renderEstudio()

    await user.click(await screen.findByRole('button', { name: 'Gerar credencial' }))
    await screen.findByRole('dialog')
    await user.keyboard('{Escape}')

    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  })

  it('senha errada mostra o erro, limpa o campo e faz uma única chamada (sem repetir após refresh)', async () => {
    entrarComo('Locutor')
    const { chamadas } = montarApi({ gerar: () => problema(401, '/erros/credenciais') })
    const user = userEvent.setup()
    renderEstudio()

    const dialogo = await gerarCredencial(user, 'errada')

    expect(await within(dialogo).findByRole('alert')).toHaveTextContent('Senha incorreta.')
    expect(within(dialogo).getByLabelText('Senha da conta')).toHaveValue('')
    expect(chamadas.filter((c) => c.metodo === 'POST')).toHaveLength(1)
    expect(chamadas.some((c) => c.caminho === '/auth/refresh')).toBe(false)
    expect(chamadas.find((c) => c.metodo === 'POST')?.corpo).toEqual({ senhaAtual: 'errada' })
  })

  it('o 429 do limite de gerações vira mensagem de espera', async () => {
    entrarComo('Locutor')
    montarApi({ gerar: () => problema(429, '/erros/limite-excedido', { retryAfterSeconds: 600 }) })
    const user = userEvent.setup()
    renderEstudio()

    const dialogo = await gerarCredencial(user)

    expect(await within(dialogo).findByRole('alert')).toHaveTextContent(/muitas tentativas/i)
  })
})

describe('Credencial de transmissão: painel do locutor', () => {
  it('mostra a idade, o último uso e as instruções, sem o segredo', async () => {
    entrarComo('Locutor')
    montarApi({
      credencial: {
        identificador: IDENTIFICADOR,
        criadaEmUtc: new Date(Date.now() - 3 * 24 * 3600_000).toISOString(),
        ultimoUsoEmUtc: null,
        conexao: CONEXAO,
      },
    })
    renderEstudio()

    expect(await screen.findByText(/Criada há 3 dias/)).toBeInTheDocument()
    expect(screen.getByText(/Ainda não foi usada/)).toBeInTheDocument()
    expect(screen.getByText(IDENTIFICADOR)).toBeInTheDocument()
    expect(screen.getByText('radio.exemplo.org')).toBeInTheDocument()
    expect(screen.getByText('8005')).toBeInTheDocument()
    expect(screen.getByText('/live')).toBeInTheDocument()
  })

  it('sem host configurado (conexao nula), manda pedir o endereço à administração', async () => {
    entrarComo('Locutor')
    montarApi({
      credencial: {
        identificador: IDENTIFICADOR,
        criadaEmUtc: new Date().toISOString(),
        ultimoUsoEmUtc: null,
        conexao: null,
      },
    })
    renderEstudio()

    expect(await screen.findByText('peça o endereço à administração')).toBeInTheDocument()
    expect(screen.queryByText('Porta')).not.toBeInTheDocument()
  })

  it('o diálogo de geração mostra a conexão que veio na resposta do POST', async () => {
    entrarComo('Locutor')
    montarApi()
    const user = userEvent.setup()
    renderEstudio()

    const dialogo = await gerarCredencial(user)

    expect(await within(dialogo).findByText('radio.exemplo.org')).toBeInTheDocument()
    expect(within(dialogo).getByText('/live')).toBeInTheDocument()
  })

  it('revoga com confirmação', async () => {
    entrarComo('Locutor')
    const { chamadas } = montarApi({
      credencial: {
        identificador: IDENTIFICADOR,
        criadaEmUtc: new Date().toISOString(),
        ultimoUsoEmUtc: null,
      },
    })
    const user = userEvent.setup()
    renderEstudio()

    await user.click(await screen.findByRole('button', { name: 'Revogar' }))
    expect(chamadas.some((c) => c.metodo === 'DELETE')).toBe(false)
    await user.click(screen.getByRole('button', { name: 'Confirmar revogação' }))

    expect(await screen.findByText(/Credencial revogada/)).toBeInTheDocument()
    expect(chamadas.some((c) => c.metodo === 'DELETE')).toBe(true)
    expect(await screen.findByText(/ainda não tem credencial/i)).toBeInTheDocument()
  })

  it('o logout descarta a credencial do cache (chave privada)', async () => {
    entrarComo('Locutor')
    montarApi({
      credencial: {
        identificador: IDENTIFICADOR,
        criadaEmUtc: new Date().toISOString(),
        ultimoUsoEmUtc: null,
      },
    })
    const { queryClient } = renderEstudio()
    await screen.findByText(IDENTIFICADOR)

    limparCachePrivado(queryClient)

    expect(queryClient.getQueryData(['credencial-transmissao'])).toBeUndefined()
  })
})

describe('Estúdio: no ar e encerrar programa', () => {
  const programa = {
    id: 'p1',
    titulo: 'Tarde Rock',
    descricao: null,
    locutor: { id: 'u1', nome: 'Ana' },
    inicioUtc: '2026-01-14T18:00:00Z',
    fimUtc: '2026-01-14T20:00:00Z',
    status: 'AoVivo',
  }

  it('mostra se há transmissão ao vivo', async () => {
    entrarComo('Locutor')
    montarApi({
      aoVivo: { noAr: true, programaId: 'p1', locutorNome: 'Ana' },
      programaEmCurso: programa,
    })
    renderEstudio()

    expect(await screen.findByText('Transmissão ao vivo com Ana.')).toBeInTheDocument()
  })

  it('encerra o programa do próprio locutor, depois de confirmar', async () => {
    entrarComo('Locutor', 'u1')
    const { chamadas } = montarApi({ programaEmCurso: programa })
    const user = userEvent.setup()
    renderEstudio()

    await user.click(await screen.findByRole('button', { name: 'Encerrar programa' }))
    expect(chamadas.some((c) => c.caminho.endsWith('/encerrar'))).toBe(false)
    await user.click(screen.getByRole('button', { name: 'Confirmar encerramento' }))

    await waitFor(() =>
      expect(
        chamadas.some((c) => c.metodo === 'POST' && c.caminho === '/programas/p1/encerrar'),
      ).toBe(true),
    )
  })

  it('não oferece encerrar o programa de outro locutor, mas o Admin pode', async () => {
    entrarComo('Locutor', 'outro')
    montarApi({ programaEmCurso: programa })
    const locutor = renderEstudio()
    await screen.findByText(/Programa em curso: Tarde Rock/)
    expect(screen.queryByRole('button', { name: 'Encerrar programa' })).not.toBeInTheDocument()
    locutor.unmount()

    entrarComo('Admin', 'admin')
    montarApi({ programaEmCurso: programa })
    renderEstudio()
    expect(await screen.findByRole('button', { name: 'Encerrar programa' })).toBeInTheDocument()
  })

  it('sem programa em curso, não há o que encerrar', async () => {
    entrarComo('Locutor')
    montarApi({ programaEmCurso: null })
    renderEstudio()

    expect(await screen.findByText('Nenhum programa em curso.')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Encerrar programa' })).not.toBeInTheDocument()
  })

  it('um 409 de transição inválida vira mensagem útil', async () => {
    entrarComo('Locutor', 'u1')
    montarApi({ programaEmCurso: programa })
    const fetchOriginal = vi.mocked(fetch)
    const user = userEvent.setup()
    renderEstudio()
    fetchOriginal.mockImplementationOnce(async () => problema(409, '/erros/transicao-invalida'))

    await user.click(await screen.findByRole('button', { name: 'Encerrar programa' }))
    // A primeira chamada depois do clique é a do encerrar; as releituras vêm do servidor falso.
    await user.click(screen.getByRole('button', { name: 'Confirmar encerramento' }))

    expect(await screen.findByRole('alert')).toHaveTextContent(/mudou de estado/)
  })
})

describe('Estúdio: credenciais ativas (Admin)', () => {
  const ativas = [
    {
      usuarioId: 'u7',
      usuarioNome: 'Beto',
      identificador: 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa',
      criadaEmUtc: new Date(Date.now() - 2 * 3600_000).toISOString(),
      ultimoUsoEmUtc: null,
    },
  ]

  it('o Admin vê a lista e revoga a credencial de outro locutor', async () => {
    entrarComo('Admin', 'admin')
    const { chamadas } = montarApi({ ativas })
    const user = userEvent.setup()
    renderEstudio()

    expect(await screen.findByText('Beto')).toBeInTheDocument()
    expect(screen.getByText(/Criada há 2 horas/)).toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: 'Revogar a credencial de Beto' }))
    await user.click(screen.getByRole('button', { name: 'Confirmar revogação' }))

    await waitFor(() =>
      expect(
        chamadas.some(
          (c) => c.metodo === 'DELETE' && c.caminho === '/usuarios/u7/credencial-transmissao',
        ),
      ).toBe(true),
    )
    expect(await screen.findByText('Nenhuma credencial ativa.')).toBeInTheDocument()
  })

  it('o Locutor nem pede a lista', async () => {
    entrarComo('Locutor')
    const { chamadas } = montarApi({ ativas })
    renderEstudio()

    await screen.findByText(/ainda não tem credencial/i)
    expect(screen.queryByText('Credenciais ativas (Admin)')).not.toBeInTheDocument()
    expect(chamadas.some((c) => c.caminho === '/credenciais-transmissao')).toBe(false)
  })
})
