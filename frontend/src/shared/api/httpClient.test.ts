import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { useAuthStore } from './authStore'
import { apiFetch, apiFetchComEtag } from './httpClient'

function jsonResponse(status: number, body: unknown) {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  })
}

describe('apiFetch — single-flight de refresh (S-M10)', () => {
  beforeEach(() => {
    useAuthStore.getState().setSession('token-expirado', {
      id: '1',
      nome: 'Ouvinte',
      email: 'a@a.com',
      role: 'Ouvinte',
      deveTrocarSenha: false,
    })
  })

  afterEach(() => {
    useAuthStore.getState().clearSession()
    vi.restoreAllMocks()
  })

  it('3 requisições com 401 simultâneo disparam 1 refresh só', async () => {
    let refreshCalls = 0

    vi.stubGlobal(
      'fetch',
      vi.fn(async (url: string) => {
        if (url.endsWith('/auth/refresh')) {
          refreshCalls += 1
          return jsonResponse(200, {
            accessToken: 'token-novo',
            expiraEmUtc: new Date().toISOString(),
            deveTrocarSenha: false,
            usuario: {
              id: '1',
              nome: 'Ouvinte',
              email: 'a@a.com',
              role: 'Ouvinte',
              deveTrocarSenha: false,
            },
          })
        }
        // qualquer outra rota: 401 enquanto o token ainda for o expirado, 200 depois
        const current = useAuthStore.getState().accessToken
        if (current === 'token-expirado') {
          return jsonResponse(401, { type: '/erros/credenciais', title: 'expirado', status: 401 })
        }
        return jsonResponse(200, { ok: true })
      }),
    )

    const results = await Promise.all([
      apiFetch('/programas/ao-vivo'),
      apiFetch('/pedidos/meus'),
      apiFetch('/divulgacoes/ativas'),
    ])

    expect(results).toEqual([{ ok: true }, { ok: true }, { ok: true }])
    expect(refreshCalls).toBe(1)
    expect(useAuthStore.getState().accessToken).toBe('token-novo')
  })

  function refreshOk() {
    return jsonResponse(200, {
      accessToken: 'token-novo',
      expiraEmUtc: new Date().toISOString(),
      deveTrocarSenha: false,
      usuario: {
        id: '1',
        nome: 'Ouvinte',
        email: 'a@a.com',
        role: 'Ouvinte',
        deveTrocarSenha: false,
      },
    })
  }

  function stubRefreshSequence(refreshResponses: Response[]) {
    let refreshCalls = 0
    vi.stubGlobal(
      'fetch',
      vi.fn(async (url: string) => {
        if (url.endsWith('/auth/refresh')) {
          refreshCalls += 1
          return refreshResponses.shift() ?? jsonResponse(500, {})
        }
        return useAuthStore.getState().accessToken === 'token-expirado'
          ? jsonResponse(401, { type: '/erros/credenciais', title: 'expirado', status: 401 })
          : jsonResponse(200, { ok: true })
      }),
    )
    return () => refreshCalls
  }

  it('refresh-concorrente: refaz o refresh UMA vez e mantém a sessão', async () => {
    const calls = stubRefreshSequence([
      jsonResponse(401, { type: '/erros/refresh-concorrente', title: 'concorrente', status: 401 }),
      refreshOk(),
    ])

    await expect(apiFetch('/pedidos/meus')).resolves.toEqual({ ok: true })
    expect(calls()).toBe(2)
    expect(useAuthStore.getState().accessToken).toBe('token-novo')
  })

  it('refresh-concorrente duas vezes seguidas: para no retry único e desloga', async () => {
    const calls = stubRefreshSequence([
      jsonResponse(401, { type: '/erros/refresh-concorrente', title: 'c', status: 401 }),
      jsonResponse(401, { type: '/erros/refresh-concorrente', title: 'c', status: 401 }),
      refreshOk(),
    ])

    await expect(apiFetch('/pedidos/meus')).rejects.toMatchObject({ status: 401 })
    expect(calls()).toBe(2)
    expect(useAuthStore.getState().accessToken).toBeNull()
  })

  it('/erros/credenciais no refresh = sessão morta: desloga sem retry', async () => {
    const calls = stubRefreshSequence([
      jsonResponse(401, { type: '/erros/credenciais', title: 'morta', status: 401 }),
      refreshOk(),
    ])

    await expect(apiFetch('/pedidos/meus')).rejects.toMatchObject({ status: 401 })
    expect(calls()).toBe(1)
    expect(useAuthStore.getState().accessToken).toBeNull()
  })
})

describe('apiFetchComEtag', () => {
  afterEach(() => {
    vi.restoreAllMocks()
  })

  it('devolve o corpo e o ETag da resposta', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(
        async () =>
          new Response(JSON.stringify({ id: 'p1' }), {
            status: 200,
            headers: { 'Content-Type': 'application/json', ETag: '"AAAAAAAAB9E="' },
          }),
      ),
    )

    const { data, etag } = await apiFetchComEtag<{ id: string }>('/programas/p1')

    expect(data).toEqual({ id: 'p1' })
    expect(etag).toBe('"AAAAAAAAB9E="')
  })

  it('etag é null quando a resposta não traz o cabeçalho', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => jsonResponse(200, { id: 'p1' })),
    )

    expect((await apiFetchComEtag('/programas/p1')).etag).toBeNull()
  })

  it('repassa o If-Match recebido nos headers (o PUT da edição)', async () => {
    const fetchMock = vi.fn(async (_url: string, _init?: RequestInit) => jsonResponse(200, {}))
    vi.stubGlobal('fetch', fetchMock)

    await apiFetchComEtag('/programas/p1', {
      method: 'PUT',
      headers: { 'If-Match': '"AAAAAAAAB9E="' },
      body: { titulo: 'x' },
    })

    const init = fetchMock.mock.calls[0][1]!
    expect(new Headers(init.headers).get('If-Match')).toBe('"AAAAAAAAB9E="')
    expect(init.method).toBe('PUT')
  })

  it('erro HTTP vira ApiError, como no apiFetch', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () =>
        jsonResponse(412, { type: '/erros/versao-desatualizada', title: 'Versão desatualizada.' }),
      ),
    )

    await expect(
      apiFetchComEtag('/programas/p1', { method: 'PUT', body: {} }),
    ).rejects.toMatchObject({
      status: 412,
      problem: { type: '/erros/versao-desatualizada' },
    })
  })
})

describe('apiFetch — 403 renova a sessão (Sentinel L2 do #42)', () => {
  const sessao = (role: 'Admin' | 'Locutor') => ({
    id: '1',
    nome: 'Ana',
    email: 'a@a.com',
    role,
    deveTrocarSenha: false,
  })

  beforeEach(() => {
    useAuthStore.getState().setSession('token-admin', sessao('Admin'))
  })

  afterEach(() => {
    useAuthStore.getState().clearSession()
    vi.restoreAllMocks()
  })

  function stub(refresh: Response, corpo403?: Response) {
    const urls: string[] = []
    vi.stubGlobal(
      'fetch',
      vi.fn(async (url: string) => {
        urls.push(url)
        if (url.endsWith('/auth/refresh')) return refresh
        // A política de papel do ASP.NET responde 403 sem corpo; só a recusa de domínio traz o type.
        return corpo403 ?? new Response(null, { status: 403 })
      }),
    )
    return urls
  }

  it('o 403 busca o papel de agora: conta rebaixada passa a valer na store, e a chamada não é repetida', async () => {
    const urls = stub(
      jsonResponse(200, {
        accessToken: 'token-locutor',
        expiraEmUtc: new Date().toISOString(),
        deveTrocarSenha: false,
        usuario: sessao('Locutor'),
      }),
    )

    await expect(apiFetch('/programas')).rejects.toMatchObject({ status: 403 })

    expect(useAuthStore.getState().usuario?.role).toBe('Locutor')
    expect(useAuthStore.getState().accessToken).toBe('token-locutor')
    expect(urls.filter((u) => u.endsWith('/programas'))).toHaveLength(1)
    expect(urls.filter((u) => u.endsWith('/auth/refresh'))).toHaveLength(1)
  })

  it('403 de domínio (/erros/acesso-negado, ex.: não é o dono) não gasta um refresh', async () => {
    const urls = stub(
      jsonResponse(500, {}),
      jsonResponse(403, { type: '/erros/acesso-negado', title: 'x', status: 403 }),
    )

    await expect(apiFetch('/programas/p1/encerrar', { method: 'POST' })).rejects.toMatchObject({
      status: 403,
    })

    expect(urls.some((u) => u.endsWith('/auth/refresh'))).toBe(false)
  })

  it('N 403 em paralelo (ex.: várias queries depois de um rebaixamento) fazem um único /auth/refresh', async () => {
    const urls = stub(
      jsonResponse(200, {
        accessToken: 'token-locutor',
        expiraEmUtc: new Date().toISOString(),
        deveTrocarSenha: false,
        usuario: sessao('Locutor'),
      }),
    )

    await Promise.allSettled([apiFetch('/a'), apiFetch('/b'), apiFetch('/c'), apiFetch('/d')])

    expect(urls.filter((u) => u.endsWith('/auth/refresh'))).toHaveLength(1)
  })

  it.each([429, 503])('refresh que responde %i não derruba a sessão', async (status) => {
    stub(new Response(null, { status }))

    await expect(apiFetch('/programas')).rejects.toMatchObject({ status: 403 })

    expect(useAuthStore.getState().usuario?.role).toBe('Admin')
    expect(useAuthStore.getState().accessToken).toBe('token-admin')
  })

  it('conta desativada: o refresh falha e a sessão cai', async () => {
    stub(jsonResponse(401, { type: '/erros/credenciais', title: 'morta', status: 401 }))

    await expect(apiFetch('/programas')).rejects.toMatchObject({ status: 403 })

    expect(useAuthStore.getState().usuario).toBeNull()
  })

  it('anônimo não dispara refresh', async () => {
    useAuthStore.getState().clearSession()
    const urls = stub(jsonResponse(500, {}))

    await expect(apiFetch('/programas')).rejects.toMatchObject({ status: 403 })

    expect(urls.some((u) => u.endsWith('/auth/refresh'))).toBe(false)
  })
})

describe('apiFetch — semRefreshSeCredenciais (geração da credencial de transmissão)', () => {
  beforeEach(() => {
    useAuthStore.getState().setSession('token-vencido', {
      id: '1',
      nome: 'Ana',
      email: 'a@a.com',
      role: 'Locutor',
      deveTrocarSenha: false,
    })
  })

  afterEach(() => {
    useAuthStore.getState().clearSession()
    vi.restoreAllMocks()
  })

  function stub(respostaDoPost: (token: string | null) => Response) {
    const posts: Array<string | null> = []
    let refreshes = 0
    vi.stubGlobal(
      'fetch',
      vi.fn(async (url: string, init: RequestInit = {}) => {
        if (url.endsWith('/auth/refresh')) {
          refreshes += 1
          return jsonResponse(200, {
            accessToken: 'token-novo',
            expiraEmUtc: new Date().toISOString(),
            deveTrocarSenha: false,
            usuario: {
              id: '1',
              nome: 'Ana',
              email: 'a@a.com',
              role: 'Locutor',
              deveTrocarSenha: false,
            },
          })
        }
        const token = new Headers(init.headers).get('Authorization')
        posts.push(token)
        return respostaDoPost(token)
      }),
    )
    return { posts, refreshes: () => refreshes }
  }

  it('401 do token vencido (sem type): renova e repete uma vez', async () => {
    const { posts, refreshes } = stub((token) =>
      token === 'Bearer token-vencido'
        ? new Response(null, { status: 401 })
        : jsonResponse(201, { ok: true }),
    )

    await expect(
      apiFetch('/locutores/me/credencial-transmissao', {
        method: 'POST',
        body: { senhaAtual: 'x' },
        semRefreshSeCredenciais: true,
      }),
    ).resolves.toEqual({ ok: true })

    expect(refreshes()).toBe(1)
    expect(posts).toEqual(['Bearer token-vencido', 'Bearer token-novo'])
  })

  it('401 /erros/credenciais (senha errada): não renova e não repete', async () => {
    const { posts, refreshes } = stub(() =>
      jsonResponse(401, { type: '/erros/credenciais', title: 'x', status: 401 }),
    )

    await expect(
      apiFetch('/locutores/me/credencial-transmissao', {
        method: 'POST',
        body: { senhaAtual: 'errada' },
        semRefreshSeCredenciais: true,
      }),
    ).rejects.toMatchObject({ status: 401 })

    expect(refreshes()).toBe(0)
    expect(posts).toHaveLength(1)
  })
})
