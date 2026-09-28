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
})
