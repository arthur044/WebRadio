import { act, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { useAlertasHarbor } from '../../shared/api/alertasHarborStore'
import { useAuthStore } from '../../shared/api/authStore'
import { criarDedupe, registrarEventos } from '../../shared/api/realtimeHub'
import { AlertasDoHarbor } from './AlertasDoHarbor'
import { CredenciaisAtivasAdmin } from './CredenciaisAtivasAdmin'

const IDENTIFICADOR = '3f2a9c1e-7b64-4d85-9a10-5c8e2d7b6f41'
const LOC1 = '0a1b2c3d-1111-4222-8333-444455556666'
const LOC2 = '0a1b2c3d-2222-4222-8333-444455556666'
const OUTRO = '0a1b2c3d-9999-4222-8333-444455556666'
const OCORRIDO = '2026-10-22T12:00:00Z'

function alerta(extras: object = {}) {
  return {
    eventoId: crypto.randomUUID(),
    ocorridoEmUtc: OCORRIDO,
    tipo: 'credencial',
    falhas: 10,
    janelaMinutos: 5,
    locutorId: LOC1,
    locutorNome: 'Ana',
    credencialAtiva: true,
    ...extras,
  }
}

function receber(dados: object) {
  act(() => useAlertasHarbor.getState().registrar(dados))
}

function entrarComo(role: 'Locutor' | 'Admin', id: string) {
  useAuthStore.setState({
    accessToken: 't',
    restoring: false,
    usuario: { id, nome: 'X', email: 'x@x', role, deveTrocarSenha: false },
  })
}

interface CredencialDoTeste {
  usuarioId: string
  usuarioNome: string
  identificador: string
  criadaEmUtc: string
  ultimoUsoEmUtc: string | null
}

/** A lista que o servidor falso devolve: o teste a muda entre o aviso e o clique. */
let ativas: CredencialDoTeste[] = []
/** Segura a resposta da lista até o teste liberar. */
const portao: { espera: Promise<void> | null } = { espera: null }

function montarApi(criadaEmUtc = '2026-10-20T10:00:00Z', listaFalha = { falhando: false }) {
  const chamadas: string[] = []
  ativas = [
    {
      usuarioId: LOC1,
      usuarioNome: 'Ana',
      identificador: IDENTIFICADOR,
      criadaEmUtc,
      ultimoUsoEmUtc: null,
    },
  ]
  vi.stubGlobal(
    'fetch',
    vi.fn(async (url: string, init: RequestInit = {}) => {
      const caminho = url.replace('/api/v1', '')
      chamadas.push(`${(init.method ?? 'GET').toUpperCase()} ${caminho}`)
      const json = (s: number, b?: unknown) =>
        new Response(b === undefined ? null : JSON.stringify(b), {
          status: s,
          headers: { 'Content-Type': 'application/json' },
        })
      if (caminho === '/credenciais-transmissao') {
        if (portao.espera) await portao.espera
        return listaFalha.falhando
          ? json(500, { type: '/erros/inesperado', title: 'x', status: 500 })
          : json(200, ativas)
      }
      if (caminho === `/usuarios/${LOC1}/credencial-transmissao`) return json(204)
      return json(500, { type: '/erros/inesperado', title: 'x', status: 500 })
    }),
  )
  return chamadas
}

function renderar(ui: React.ReactElement) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(<QueryClientProvider client={queryClient}>{ui}</QueryClientProvider>)
}

beforeEach(() => {
  useAlertasHarbor.getState().limpar()
  portao.espera = null
})
afterEach(() => {
  useAlertasHarbor.getState().limpar()
  useAuthStore.setState({ accessToken: null, usuario: null })
  vi.unstubAllGlobals()
})

describe('AlertasDoHarbor: Admin', () => {
  beforeEach(() => entrarComo('Admin', 'adm'))

  it('credencial ativa: texto do contrato e botão de revogar', async () => {
    montarApi()
    renderar(<AlertasDoHarbor para="Admin" />)
    receber(alerta())

    expect(
      await screen.findByText(
        'A credencial de transmissão de Ana teve 10 tentativas recusadas em 5 min. Confira o BUTT/Mixxx do locutor; se não for ele, revogue a credencial',
      ),
    ).toBeInTheDocument()
    expect(
      await screen.findByRole('button', { name: 'Revogar a credencial de Ana' }),
    ).toBeInTheDocument()
  })

  it('credencial antiga: texto próprio e sem botão de revogar', () => {
    montarApi()
    renderar(<AlertasDoHarbor para="Admin" />)
    receber(alerta({ credencialAtiva: false }))

    expect(
      screen.getByText(
        'Uma credencial antiga de Ana teve 10 tentativas recusadas em 5 min. O BUTT/Mixxx do locutor provavelmente ainda usa o segredo anterior',
      ),
    ).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /Revogar/ })).not.toBeInTheDocument()
  })

  it('global: texto do contrato', () => {
    montarApi()
    renderar(<AlertasDoHarbor para="Admin" />)
    receber({ ocorridoEmUtc: OCORRIDO, tipo: 'global', falhas: 50, janelaMinutos: 5 })

    expect(screen.getByText('50 tentativas recusadas no ao vivo em 5 min')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: /Revogar/ })).not.toBeInTheDocument()
  })

  it('dois alertas da mesma chave deixam um aviso só; chaves diferentes convivem', () => {
    montarApi()
    renderar(<AlertasDoHarbor para="Admin" />)
    receber(alerta({ falhas: 10 }))
    receber(alerta({ falhas: 12 }))
    expect(screen.getAllByRole('alert')).toHaveLength(1)
    expect(screen.getByText(/teve 12 tentativas/)).toBeInTheDocument()

    receber(alerta({ locutorId: LOC2, locutorNome: 'Bia' }))
    receber({ ocorridoEmUtc: OCORRIDO, tipo: 'global', falhas: 50, janelaMinutos: 5 })
    expect(screen.getAllByRole('alert')).toHaveLength(3)
  })

  it('locutorNome com HTML aparece como texto', () => {
    montarApi()
    const { container } = renderar(<AlertasDoHarbor para="Admin" />)
    receber(alerta({ locutorNome: '<b>Ana</b>', credencialAtiva: false }))

    expect(container.querySelector('b')).toBeNull()
    expect(screen.getByText(/<b>Ana<\/b>/)).toBeInTheDocument()
  })

  it('o texto não mostra IP nem identificador, mesmo que o servidor mande', () => {
    montarApi()
    const { container } = renderar(<AlertasDoHarbor para="Admin" />)
    receber(alerta({ identificador: IDENTIFICADOR, ip: '203.0.113.7', segredo: 'S3gredo' }))

    const texto = container.textContent ?? ''
    expect(texto).not.toContain(IDENTIFICADOR)
    expect(texto).not.toContain('203.0.113.7')
    expect(texto).not.toContain('S3gredo')
    expect(JSON.stringify(useAlertasHarbor.getState().alertas)).not.toContain('203.0.113.7')
  })

  it('revogar pede confirmação e dispensa o aviso', async () => {
    const chamadas = montarApi()
    const user = userEvent.setup()
    renderar(<AlertasDoHarbor para="Admin" />)
    receber(alerta())

    await user.click(await screen.findByRole('button', { name: 'Revogar a credencial de Ana' }))
    const dialogo = screen.getByRole('alertdialog')
    expect(within(dialogo).queryByText(/criada depois deste alerta/)).not.toBeInTheDocument()
    expect(chamadas).not.toContain(`DELETE /usuarios/${LOC1}/credencial-transmissao`)

    await user.click(within(dialogo).getByRole('button', { name: 'Confirmar revogação' }))

    await waitFor(() =>
      expect(chamadas).toContain(`DELETE /usuarios/${LOC1}/credencial-transmissao`),
    )
    await waitFor(() => expect(useAlertasHarbor.getState().alertas).toEqual({}))
  })

  it('avisa quando a credencial ativa foi criada depois do alerta', async () => {
    montarApi('2026-10-22T12:30:00Z')
    const user = userEvent.setup()
    renderar(<AlertasDoHarbor para="Admin" />)
    receber(alerta())

    await user.click(await screen.findByRole('button', { name: 'Revogar a credencial de Ana' }))

    expect(screen.getByRole('alertdialog')).toHaveTextContent(
      /a credencial ativa foi criada depois deste alerta.*derruba a credencial nova/,
    )
  })

  it('a confirmação leva o foco ao Manter, liga as descrições e devolve o foco ao Revogar', async () => {
    montarApi('2026-10-22T12:30:00Z')
    const user = userEvent.setup()
    renderar(<AlertasDoHarbor para="Admin" />)
    receber(alerta())

    await user.click(await screen.findByRole('button', { name: 'Revogar a credencial de Ana' }))

    const dialogo = screen.getByRole('alertdialog')
    expect(within(dialogo).getByRole('button', { name: 'Manter' })).toHaveFocus()
    expect(dialogo).toHaveAccessibleDescription(
      /Revogar a credencial de Ana\?.*criada depois deste alerta/,
    )

    await user.click(within(dialogo).getByRole('button', { name: 'Manter' }))

    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Revogar a credencial de Ana' })).toHaveFocus()
  })

  it('se a lista de credenciais falha, mostra o erro e deixa tentar de novo', async () => {
    const falha = { falhando: true }
    montarApi('2026-10-20T10:00:00Z', falha)
    const user = userEvent.setup()
    renderar(<AlertasDoHarbor para="Admin" />)
    receber(alerta())

    const tentar = await screen.findByRole('button', { name: 'Tentar de novo' })
    expect(screen.getAllByRole('alert')).toHaveLength(2) // o aviso e o erro da lista
    expect(screen.queryByRole('button', { name: /Revogar a credencial/ })).not.toBeInTheDocument()

    falha.falhando = false
    await user.click(tentar)

    expect(await screen.findByRole('button', { name: 'Revogar a credencial de Ana' })).toBeEnabled()
  })

  it('relê a lista ao clicar: a credencial criada depois do aviso entra na confirmação', async () => {
    montarApi()
    const user = userEvent.setup()
    renderar(<AlertasDoHarbor para="Admin" />)
    receber(alerta())
    const revogar = await screen.findByRole('button', { name: 'Revogar a credencial de Ana' })
    await waitFor(() => expect(revogar).toBeEnabled())

    // Entre o aviso e o clique o locutor gerou outra credencial.
    ativas[0].criadaEmUtc = '2026-10-22T12:30:00Z'
    await user.click(revogar)

    expect(await screen.findByRole('alertdialog')).toHaveAccessibleDescription(
      /criada depois deste alerta/,
    )
  })

  it('desabilita o botão enquanto relê e só abre a confirmação com o resultado novo', async () => {
    montarApi()
    const user = userEvent.setup()
    renderar(<AlertasDoHarbor para="Admin" />)
    receber(alerta())
    const revogar = await screen.findByRole('button', { name: 'Revogar a credencial de Ana' })
    await waitFor(() => expect(revogar).toBeEnabled())

    let liberar!: () => void
    portao.espera = new Promise<void>((r) => (liberar = r))
    await user.click(revogar)

    expect(screen.getByRole('button', { name: 'Conferindo a credencial…' })).toBeDisabled()
    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()

    liberar()
    expect(await screen.findByRole('alertdialog')).toBeInTheDocument()
  })

  it('se a releitura falha, abre a confirmação avisando que não deu para conferir', async () => {
    const falha = { falhando: false }
    montarApi('2026-10-20T10:00:00Z', falha)
    const user = userEvent.setup()
    renderar(<AlertasDoHarbor para="Admin" />)
    receber(alerta())
    const revogar = await screen.findByRole('button', { name: 'Revogar a credencial de Ana' })
    await waitFor(() => expect(revogar).toBeEnabled())

    falha.falhando = true
    await user.click(revogar)

    const dialogo = await screen.findByRole('alertdialog')
    expect(dialogo).toHaveTextContent('Não foi possível conferir a credencial ativa agora')
    expect(within(dialogo).getByRole('button', { name: 'Confirmar revogação' })).toBeEnabled()
  })

  it('credencial nova e releitura falha juntas: ids da descrição distintos', async () => {
    const falha = { falhando: false }
    montarApi('2026-10-22T12:30:00Z', falha)
    const user = userEvent.setup()
    renderar(<AlertasDoHarbor para="Admin" />)
    receber(alerta())
    const revogar = await screen.findByRole('button', { name: 'Revogar a credencial de Ana' })
    await waitFor(() => expect(revogar).toBeEnabled())

    falha.falhando = true
    await user.click(revogar)

    const dialogo = await screen.findByRole('alertdialog')
    const ids = dialogo.getAttribute('aria-describedby')!.split(' ')
    expect(ids).toHaveLength(3)
    expect(new Set(ids).size).toBe(3)
    expect(dialogo).toHaveAccessibleDescription(/criada depois.*Não foi possível conferir/)
  })

  it('se a credencial foi revogada entre o aviso e o clique, não abre a confirmação', async () => {
    montarApi()
    const user = userEvent.setup()
    renderar(<AlertasDoHarbor para="Admin" />)
    receber(alerta())
    const revogar = await screen.findByRole('button', { name: 'Revogar a credencial de Ana' })
    await waitFor(() => expect(revogar).toBeEnabled())

    ativas = []
    await user.click(revogar)

    expect(await screen.findByText('Essa credencial já não está ativa.')).toBeInTheDocument()
    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
  })

  it('destaca o locutor na lista de credenciais ativas', async () => {
    montarApi()
    renderar(<CredenciaisAtivasAdmin />)
    const linha = (await screen.findByText('Ana')).closest('li')!
    expect(linha).not.toHaveAttribute('data-alerta')

    receber(alerta())

    await waitFor(() => expect(linha).toHaveAttribute('data-alerta', 'true'))
    expect(linha).toHaveTextContent('10 tentativas recusadas em 5 min')
  })

  it('não mostra o painel do Locutor para o Admin', () => {
    montarApi()
    renderar(<AlertasDoHarbor para="Locutor" />)
    receber(alerta())
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
  })
})

describe('AlertasDoHarbor: Locutor', () => {
  beforeEach(() => entrarComo('Locutor', LOC1))

  it('credencial ativa e credencial antiga', () => {
    montarApi()
    renderar(<AlertasDoHarbor para="Locutor" />)
    receber(alerta())
    expect(
      screen.getByText(
        'Sua credencial teve 10 tentativas recusadas nos últimos 5 min. Confira o segredo configurado no BUTT/Mixxx; se não foi você, revogue e gere outra',
      ),
    ).toBeInTheDocument()

    receber(alerta({ credencialAtiva: false }))
    expect(
      screen.getByText(
        'Seu BUTT/Mixxx está tentando conectar com uma credencial antiga, já revogada. Atualize-o com o identificador e o segredo atuais',
      ),
    ).toBeInTheDocument()
    expect(screen.getAllByRole('alert')).toHaveLength(1)
    expect(screen.queryByRole('button', { name: /Revogar/ })).not.toBeInTheDocument()
  })

  it('não mostra alerta de outro locutor nem o global', () => {
    montarApi()
    renderar(<AlertasDoHarbor para="Locutor" />)
    receber(alerta({ locutorId: OUTRO }))
    receber({ ocorridoEmUtc: OCORRIDO, tipo: 'global', falhas: 50, janelaMinutos: 5 })
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
  })
})

describe('AlertaHarborFalhas no hub e na store', () => {
  function montarHub() {
    const handlers = new Map<string, (dados: unknown) => void>()
    const connection = { on: (nome: string, h: (d: unknown) => void) => handlers.set(nome, h) }
    registrarEventos(connection as never, { invalidateQueries: vi.fn() } as never, criarDedupe())
    return handlers.get('AlertaHarborFalhas')!
  }

  it('eventoId repetido não muda nada', () => {
    const aoReceber = montarHub()
    const a = alerta({ falhas: 10 })
    aoReceber(a)
    aoReceber({ ...a, falhas: 99 })
    expect(Object.values(useAlertasHarbor.getState().alertas).map((x) => x.falhas)).toEqual([10])
  })

  it('o aviso não vai para localStorage nem sessionStorage', () => {
    const local = vi.spyOn(Storage.prototype, 'setItem')
    montarHub()(alerta())
    expect(Object.keys(useAlertasHarbor.getState().alertas)).toHaveLength(1)
    expect(local).not.toHaveBeenCalled()
    expect(window.localStorage.length).toBe(0)
    expect(window.sessionStorage.length).toBe(0)
    local.mockRestore()
  })

  it.each(['..', '../x', 'a/b', 'loc1', `${LOC1}/x`, ' '])(
    'locutorId %j que não é GUID: o alerta é ignorado',
    (locutorId) => {
      montarHub()(alerta({ locutorId }))
      expect(useAlertasHarbor.getState().alertas).toEqual({})
    },
  )

  it('GUID em maiúsculas vale e é guardado em minúsculas (mesma chave do alerta em minúsculas)', () => {
    const aoReceber = montarHub()
    aoReceber(alerta({ locutorId: LOC1.toUpperCase() }))
    aoReceber(alerta({ locutorId: LOC1 }))
    const alertas = useAlertasHarbor.getState().alertas
    expect(Object.keys(alertas)).toEqual([`credencial:${LOC1}`])
    expect(alertas[`credencial:${LOC1}`].locutorId).toBe(LOC1)
  })

  it('payload inválido é ignorado', () => {
    const aoReceber = montarHub()
    aoReceber({ eventoId: 'a', tipo: 'credencial', falhas: 10, janelaMinutos: 5 })
    aoReceber({
      eventoId: 'b',
      tipo: 'outro',
      falhas: 10,
      janelaMinutos: 5,
      ocorridoEmUtc: OCORRIDO,
    })
    expect(useAlertasHarbor.getState().alertas).toEqual({})
  })
})
