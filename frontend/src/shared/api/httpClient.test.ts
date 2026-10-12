import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { useAuthStore } from './authStore'
import { apiFetch } from './httpClient'

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
