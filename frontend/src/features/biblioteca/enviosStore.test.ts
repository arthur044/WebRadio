import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { useEnvios, urlDaPolicy } from './enviosStore'

const MB = 1024 * 1024

function json(status: number, body?: unknown) {
  return new Response(body === undefined ? null : JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  })
}

function arquivoDe(nome: string, tamanho = 1000, type = 'audio/mpeg'): File {
  const f = new File(['x'], nome, { type })
  Object.defineProperty(f, 'size', { value: tamanho })
  return f
}

/** XHR falso: guarda o que foi enviado e deixa o teste decidir quando e como a resposta chega. */
class XhrFalso {
  static instancias: XhrFalso[] = []
  upload: { onprogress: ((e: object) => void) | null } = { onprogress: null }
  onload: (() => void) | null = null
  onerror: (() => void) | null = null
  onabort: (() => void) | null = null
  status = 0
  respondido = false
  metodo = ''
  url = ''
  corpo: FormData | null = null
  constructor() {
    XhrFalso.instancias.push(this)
  }
  open(metodo: string, url: string) {
    this.metodo = metodo
    this.url = url
  }
  send(corpo: FormData) {
    this.corpo = corpo
  }
  abort() {
    this.onabort?.()
  }
  responder(status: number) {
    this.respondido = true
    this.status = status
    this.onload?.()
  }
}

interface Chamada {
  metodo: string
  caminho: string
  corpo: unknown
}

function montarApi(
  respostas: {
    uploads?: Response[]
    concluir?: Response[]
    excluir?: Response[]
    /** Corpo do `GET /midias/{id}` (a releitura depois de um 409 no `concluir`). */
    registro?: object
  } = {},
) {
  const chamadas: Chamada[] = []
  const uploads = [...(respostas.uploads ?? [])]
  const concluir = [...(respostas.concluir ?? [])]
  const excluir = [...(respostas.excluir ?? [])]
  let n = 0
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
      if (caminho === '/midias/uploads') {
        return (
          uploads.shift() ??
          json(201, {
            midiaId: `m${++n}`,
            upload: {
              url: `${window.location.origin}/storage/quarentena`,
              campos: {
                key: `2026/m${n}`,
                policy: 'p',
                'x-amz-signature': 's',
                'Content-Type': 'audio/mpeg',
                success_action_status: '201',
              },
              expiraEmUtc: '2026-11-08T12:00:00Z',
            },
          })
        )
      }
      if (/^\/midias\/[^/]+\/concluir$/.test(caminho)) return concluir.shift() ?? json(202)
      if (metodo === 'GET' && /^\/midias\/[^/]+$/.test(caminho) && respostas.registro) {
        return json(200, respostas.registro)
      }
      if (metodo === 'DELETE' && /^\/midias\/[^/]+$/.test(caminho))
        return excluir.shift() ?? json(204)
      return json(404, { type: '/erros/x', title: 'x', status: 404 })
    }),
  )
  return { chamadas }
}

const esperar = () => new Promise((r) => setTimeout(r, 0))

async function ate(condicao: () => boolean) {
  for (let i = 0; i < 50 && !condicao(); i++) await esperar()
  expect(condicao()).toBe(true)
}

describe('fila de envios (E4-P02)', () => {
  beforeEach(() => {
    XhrFalso.instancias = []
    vi.stubGlobal('XMLHttpRequest', XhrFalso)
    useEnvios.setState({ envios: [] })
  })
  afterEach(async () => {
    // O laço da fila é do módulo: cada teste precisa deixá-lo ocioso, senão o seguinte herda um envio pendurado.
    vi.useRealTimers()
    for (let i = 0; i < 20; i++) {
      for (const xhr of XhrFalso.instancias) if (!xhr.respondido) xhr.responder(201)
      await esperar()
    }
    vi.unstubAllGlobals()
  })

  it('pede a policy, envia os campos e o arquivo por último ao storage e conclui', async () => {
    const api = montarApi()
    useEnvios.getState().adicionar({
      arquivo: arquivoDe('musica.mp3'),
      tipo: 'Musica',
      titulo: '  Aquarela ',
      artista: '',
    })

    await ate(() => XhrFalso.instancias.length === 1)
    const xhr = XhrFalso.instancias[0]
    expect(xhr.metodo).toBe('POST')
    expect(xhr.url).toBe(`${window.location.origin}/storage/quarentena`)
    // Só os campos da policy, na ordem que vieram, e o `file` por último: nenhum campo extra (acl, x-amz-meta-*) dá 403.
    expect([...xhr.corpo!.keys()]).toEqual([
      'key',
      'policy',
      'x-amz-signature',
      'Content-Type',
      'success_action_status',
      'file',
    ])
    expect(api.chamadas[0]).toEqual({
      metodo: 'POST',
      caminho: '/midias/uploads',
      corpo: {
        nomeOriginal: 'musica.mp3',
        tipoMidia: 'Musica',
        tamanhoBytes: 1000,
        mimeTypeDeclarado: 'audio/mpeg',
        titulo: 'Aquarela',
        artista: null,
      },
    })
    expect(useEnvios.getState().envios[0].estado.fase).toBe('enviando')

    xhr.upload.onprogress?.({ lengthComputable: true, loaded: 50, total: 100 })
    expect(useEnvios.getState().envios[0].estado).toEqual({ fase: 'enviando', progresso: 0.5 })

    xhr.responder(201)
    await ate(() => useEnvios.getState().envios[0].estado.fase === 'enviado')
    expect(api.chamadas.at(-1)).toEqual({
      metodo: 'POST',
      caminho: '/midias/m1/concluir',
      corpo: undefined,
    })
    expect(useEnvios.getState().envios[0].estado).toEqual({ fase: 'enviado', midiaId: 'm1' })
  })

  it('manda um envio por vez: o segundo só pede a policy quando o primeiro termina', async () => {
    const api = montarApi()
    const { adicionar } = useEnvios.getState()
    adicionar({ arquivo: arquivoDe('a.mp3'), tipo: 'Vinheta', titulo: '', artista: '' })
    adicionar({ arquivo: arquivoDe('b.mp3'), tipo: 'Vinheta', titulo: '', artista: '' })

    await ate(() => XhrFalso.instancias.length === 1)
    await esperar()
    expect(api.chamadas.filter((c) => c.caminho === '/midias/uploads')).toHaveLength(1)
    expect(useEnvios.getState().envios[1].estado.fase).toBe('aguardando')

    XhrFalso.instancias[0].responder(201)
    await ate(() => XhrFalso.instancias.length === 2)
    expect(api.chamadas.filter((c) => c.caminho === '/midias/uploads')).toHaveLength(2)
  })

  it('arquivo acima do limite do tipo nem pede a policy', async () => {
    const api = montarApi()
    useEnvios.getState().adicionar({
      arquivo: arquivoDe('grande.mp3', 11 * MB),
      tipo: 'Vinheta',
      titulo: '',
      artista: '',
    })

    await ate(() => useEnvios.getState().envios[0].estado.fase === 'erro')
    expect(api.chamadas).toEqual([])
    expect(XhrFalso.instancias).toEqual([])
  })

  it('não envia o arquivo para um destino fora de /storage/quarentena da própria origem', async () => {
    montarApi({
      uploads: [
        json(201, {
          midiaId: 'm1',
          upload: { url: 'https://evil.example/storage/quarentena', campos: {}, expiraEmUtc: 'x' },
        }),
      ],
    })
    useEnvios.getState().adicionar({
      arquivo: arquivoDe('a.mp3'),
      tipo: 'Vinheta',
      titulo: '',
      artista: '',
    })

    await ate(() => useEnvios.getState().envios[0].estado.fase === 'erro')
    expect(XhrFalso.instancias).toEqual([])
  })

  it('429 na policy: espera o Retry-After, pausa a fila e retoma sozinha', async () => {
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout', 'Date'] })
    montarApi({
      uploads: [
        json(429, {
          type: '/erros/limite',
          title: 'Muitos envios',
          status: 429,
          retryAfterSeconds: 90,
        }),
      ],
    })
    const { adicionar } = useEnvios.getState()
    adicionar({ arquivo: arquivoDe('a.mp3'), tipo: 'Vinheta', titulo: '', artista: '' })
    adicionar({ arquivo: arquivoDe('b.mp3'), tipo: 'Vinheta', titulo: '', artista: '' })

    await vi.waitFor(() => expect(useEnvios.getState().envios[0].estado.fase).toBe('espera'))
    const espera = useEnvios.getState().envios[0].estado
    expect(espera.fase === 'espera' && espera.ateEm - Date.now()).toBeGreaterThan(89_000)
    // O segundo não tenta enquanto o primeiro espera: bateria no mesmo limite.
    expect(useEnvios.getState().envios[1].estado.fase).toBe('aguardando')
    expect(XhrFalso.instancias).toEqual([])

    await vi.advanceTimersByTimeAsync(90_000)
    await vi.waitFor(() => expect(XhrFalso.instancias).toHaveLength(1))
  })

  it('429 do limite de abertos: espera o Retry-After inteiro, de até 2 h, sem cortar em 1 h', async () => {
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout', 'Date'] })
    montarApi({
      uploads: [
        json(429, {
          type: '/erros/limite',
          title: 'Muitos envios',
          status: 429,
          retryAfterSeconds: 7200,
        }),
      ],
    })
    useEnvios.getState().adicionar({
      arquivo: arquivoDe('a.mp3'),
      tipo: 'Vinheta',
      titulo: '',
      artista: '',
    })

    await vi.waitFor(() => expect(useEnvios.getState().envios[0].estado.fase).toBe('espera'))
    const espera = useEnvios.getState().envios[0].estado
    expect(espera.fase === 'espera' && espera.ateEm - Date.now()).toBeGreaterThan(7_199_000)
    expect(XhrFalso.instancias).toEqual([])
  })

  it('cancelar um envio que está enviando aborta o XHR e marca como cancelado', async () => {
    montarApi()
    useEnvios.getState().adicionar({
      arquivo: arquivoDe('a.mp3'),
      tipo: 'Vinheta',
      titulo: '',
      artista: '',
    })
    await ate(() => XhrFalso.instancias.length === 1)

    useEnvios.getState().cancelar(useEnvios.getState().envios[0].id)

    await ate(() => useEnvios.getState().envios[0].estado.fase === 'cancelado')
  })

  it('o storage recusando vira erro com o status, e "tentar de novo" refaz o envio', async () => {
    montarApi()
    useEnvios.getState().adicionar({
      arquivo: arquivoDe('a.mp3'),
      tipo: 'Vinheta',
      titulo: '',
      artista: '',
    })
    await ate(() => XhrFalso.instancias.length === 1)
    XhrFalso.instancias[0].responder(403)

    await ate(() => useEnvios.getState().envios[0].estado.fase === 'erro')
    expect(useEnvios.getState().envios[0].estado).toEqual({
      fase: 'erro',
      mensagem: 'O armazenamento recusou o arquivo (HTTP 403).',
    })

    useEnvios.getState().tentarDeNovo(useEnvios.getState().envios[0].id)
    await ate(() => XhrFalso.instancias.length === 2)
  })

  it('400 da policy mostra a mensagem do campo (ex.: nome do arquivo recusado)', async () => {
    montarApi({
      uploads: [
        json(400, {
          type: '/erros/validacao',
          title: 'Inválido',
          status: 400,
          errors: { NomeOriginal: ['O nome do arquivo tem caracteres não permitidos.'] },
        }),
      ],
    })
    useEnvios.getState().adicionar({
      arquivo: arquivoDe('a.mp3'),
      tipo: 'Vinheta',
      titulo: '',
      artista: '',
    })

    await ate(() => useEnvios.getState().envios[0].estado.fase === 'erro')
    expect(useEnvios.getState().envios[0].estado).toEqual({
      fase: 'erro',
      mensagem: 'O nome do arquivo tem caracteres não permitidos.',
    })
  })
})

describe('fila de envios: cancelar, concluir e o slot de AguardandoUpload (Guardian no #110, spec 08 v1.5)', () => {
  beforeEach(() => {
    XhrFalso.instancias = []
    vi.stubGlobal('XMLHttpRequest', XhrFalso)
    useEnvios.setState({ envios: [] })
  })
  afterEach(async () => {
    vi.useRealTimers()
    for (let i = 0; i < 20; i++) {
      for (const xhr of XhrFalso.instancias) if (!xhr.respondido) xhr.responder(201)
      await esperar()
    }
    vi.unstubAllGlobals()
  })

  const novo = (nome: string) => ({
    arquivo: arquivoDe(nome),
    tipo: 'Vinheta' as const,
    titulo: '',
    artista: '',
  })

  it('cancelar o envio em espera do 429 destrava a fila: o seguinte pede a policy', async () => {
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout', 'Date'] })
    const api = montarApi({
      uploads: [
        json(429, { type: '/erros/limite', title: 'x', status: 429, retryAfterSeconds: 600 }),
      ],
    })
    useEnvios.getState().adicionar(novo('a.mp3'))
    useEnvios.getState().adicionar(novo('b.mp3'))
    await vi.waitFor(() => expect(useEnvios.getState().envios[0].estado.fase).toBe('espera'))
    expect(api.chamadas.filter((c) => c.caminho === '/midias/uploads')).toHaveLength(1)

    useEnvios.getState().cancelar(useEnvios.getState().envios[0].id)

    await vi.waitFor(() => expect(XhrFalso.instancias).toHaveLength(1))
    expect(api.chamadas.filter((c) => c.caminho === '/midias/uploads')).toHaveLength(2)
    expect(useEnvios.getState().envios[0].estado.fase).toBe('cancelado')
  })

  it('falha no concluir: "tentar de novo" repete só o concluir, sem nova policy nem novo arquivo', async () => {
    const api = montarApi({ concluir: [json(503, { type: '/erros/x', title: 'x', status: 503 })] })
    useEnvios.getState().adicionar(novo('a.mp3'))
    await ate(() => XhrFalso.instancias.length === 1)
    XhrFalso.instancias[0].responder(201)
    await ate(() => useEnvios.getState().envios[0].estado.fase === 'erro')
    expect(useEnvios.getState().envios[0].midiaId).toBe('m1')

    useEnvios.getState().tentarDeNovo(useEnvios.getState().envios[0].id)

    await ate(() => useEnvios.getState().envios[0].estado.fase === 'enviado')
    expect(api.chamadas.map((c) => `${c.metodo} ${c.caminho}`)).toEqual([
      'POST /midias/uploads',
      'POST /midias/m1/concluir',
      'POST /midias/m1/concluir',
    ])
    expect(XhrFalso.instancias).toHaveLength(1)
    // O registro ainda vale: nada de DELETE, o arquivo já está no storage.
    expect(api.chamadas.some((c) => c.metodo === 'DELETE')).toBe(false)
  })

  it('concluir recusado com 400: o retry continua sendo só o concluir, sem DELETE nem nova policy', async () => {
    const api = montarApi({ concluir: [json(400, { type: '/erros/x', title: 'x', status: 400 })] })
    useEnvios.getState().adicionar(novo('a.mp3'))
    await ate(() => XhrFalso.instancias.length === 1)
    XhrFalso.instancias[0].responder(201)
    await ate(() => useEnvios.getState().envios[0].estado.fase === 'erro')

    useEnvios.getState().tentarDeNovo(useEnvios.getState().envios[0].id)
    await ate(() => useEnvios.getState().envios[0].estado.fase === 'enviado')

    expect(api.chamadas.map((c) => `${c.metodo} ${c.caminho}`)).toEqual([
      'POST /midias/uploads',
      'POST /midias/m1/concluir',
      'POST /midias/m1/concluir',
    ])
  })

  it('DELETE do slot com 409 (um concluir ganhou a corrida): o envio segue como concluído', async () => {
    montarApi({ excluir: [json(409, { type: '/erros/x', title: 'x', status: 409 })] })
    useEnvios.getState().adicionar(novo('a.mp3'))
    await ate(() => XhrFalso.instancias.length === 1)

    XhrFalso.instancias[0].responder(403)

    await ate(() => useEnvios.getState().envios[0].estado.fase === 'enviado')
    const envio = useEnvios.getState().envios[0]
    expect(envio.estado).toEqual({ fase: 'enviado', midiaId: 'm1' })
    expect(envio.slotAberto).toBeUndefined()
  })

  it('409 no concluir com o registro já em análise (resposta perdida): o envio vira concluído', async () => {
    const api = montarApi({
      concluir: [json(409, { type: '/erros/transicao-invalida', title: 'x', status: 409 })],
      registro: { statusSanitizacao: 'Pendente', cancelado: false },
    })
    useEnvios.getState().adicionar(novo('a.mp3'))
    await ate(() => XhrFalso.instancias.length === 1)
    XhrFalso.instancias[0].responder(201)

    await ate(() => useEnvios.getState().envios[0].estado.fase === 'enviado')
    expect(api.chamadas.filter((c) => c.caminho === '/midias/m1/concluir')).toHaveLength(1)
  })

  it('409 no concluir com o envio cancelado: não repete o concluir; o retry refaz o envio inteiro', async () => {
    const api = montarApi({
      concluir: [json(409, { type: '/erros/transicao-invalida', title: 'x', status: 409 })],
      registro: { statusSanitizacao: 'Rejeitado', cancelado: true },
    })
    useEnvios.getState().adicionar(novo('a.mp3'))
    await ate(() => XhrFalso.instancias.length === 1)
    XhrFalso.instancias[0].responder(201)
    await ate(() => useEnvios.getState().envios[0].estado.fase === 'erro')
    expect(useEnvios.getState().envios[0].midiaId).toBeUndefined()

    useEnvios.getState().tentarDeNovo(useEnvios.getState().envios[0].id)
    await ate(() => XhrFalso.instancias.length === 2)
    expect(api.chamadas.filter((c) => c.caminho === '/midias/uploads')).toHaveLength(2)
  })

  it('cancelar um envio em andamento devolve o slot com DELETE, antes de o seguinte pedir policy', async () => {
    const api = montarApi()
    useEnvios.getState().adicionar(novo('a.mp3'))
    useEnvios.getState().adicionar(novo('b.mp3'))
    await ate(() => XhrFalso.instancias.length === 1)

    useEnvios.getState().cancelar(useEnvios.getState().envios[0].id)

    await ate(() => XhrFalso.instancias.length === 2)
    expect(api.chamadas.map((c) => `${c.metodo} ${c.caminho}`)).toEqual([
      'POST /midias/uploads',
      'DELETE /midias/m1',
      'POST /midias/uploads',
    ])
    expect(useEnvios.getState().envios[0].estado.fase).toBe('cancelado')
    expect(useEnvios.getState().envios[0].slotAberto).toBeUndefined()
  })

  it('o storage recusando devolve o slot na hora, e "tentar de novo" pede policy nova', async () => {
    const api = montarApi()
    useEnvios.getState().adicionar(novo('a.mp3'))
    await ate(() => XhrFalso.instancias.length === 1)
    XhrFalso.instancias[0].responder(403)
    await ate(() => api.chamadas.some((c) => c.metodo === 'DELETE'))
    await ate(() => useEnvios.getState().envios[0].estado.fase === 'erro')

    useEnvios.getState().tentarDeNovo(useEnvios.getState().envios[0].id)
    await ate(() => XhrFalso.instancias.length === 2)

    expect(api.chamadas.map((c) => `${c.metodo} ${c.caminho}`)).toEqual([
      'POST /midias/uploads',
      'DELETE /midias/m1',
      'POST /midias/uploads',
    ])
  })

  it('DELETE que falhou (rede): o "tentar de novo" o repete antes de pedir outra policy', async () => {
    const api = montarApi({ excluir: [json(503, { type: '/erros/x', title: 'x', status: 503 })] })
    useEnvios.getState().adicionar(novo('a.mp3'))
    await ate(() => XhrFalso.instancias.length === 1)
    XhrFalso.instancias[0].responder(403)
    await ate(() => api.chamadas.some((c) => c.metodo === 'DELETE'))
    await ate(() => useEnvios.getState().envios[0].estado.fase === 'erro')
    expect(useEnvios.getState().envios[0].slotAberto).toBe('m1')

    useEnvios.getState().tentarDeNovo(useEnvios.getState().envios[0].id)
    await ate(() => XhrFalso.instancias.length === 2)

    expect(api.chamadas.map((c) => `${c.metodo} ${c.caminho}`)).toEqual([
      'POST /midias/uploads',
      'DELETE /midias/m1',
      'DELETE /midias/m1',
      'POST /midias/uploads',
    ])
  })
})

describe('urlDaPolicy', () => {
  it('aceita só a quarentena da própria origem', () => {
    const o = window.location.origin
    expect(urlDaPolicy(`${o}/storage/quarentena`)?.href).toBe(`${o}/storage/quarentena`)
    expect(urlDaPolicy('/storage/quarentena')?.href).toBe(`${o}/storage/quarentena`)
    expect(urlDaPolicy(`${o}/storage/midia`)).toBeNull()
    expect(urlDaPolicy(`${o}/storage/quarentena/outra`)).toBeNull()
    expect(urlDaPolicy('//evil.example/storage/quarentena')).toBeNull()
    expect(urlDaPolicy('javascript:alert(1)')).toBeNull()
  })
})
