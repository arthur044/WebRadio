import { renderHook, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { useAuthStore } from '../../shared/api/authStore'
import { useSessionRestore } from './useSessionRestore'

function jsonResponse(status: number, body: unknown) {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } })
}

describe('useSessionRestore', () => {
  afterEach(() => {
    useAuthStore.getState().clearSession()
    useAuthStore.getState().setRestoring(true)
    vi.restoreAllMocks()
  })

  it('F5 mantém a sessão: cookie válido no boot restaura o usuário', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () =>
        jsonResponse(200, {
          accessToken: 'token-restaurado',
          expiraEmUtc: new Date().toISOString(),
          deveTrocarSenha: false,
          usuario: { id: '1', nome: 'Ouvinte', email: 'a@a.com', role: 'Ouvinte', deveTrocarSenha: false },
        }),
      ),
    )

    renderHook(() => useSessionRestore())

    await waitFor(() => expect(useAuthStore.getState().restoring).toBe(false))
    expect(useAuthStore.getState().accessToken).toBe('token-restaurado')
    expect(useAuthStore.getState().usuario?.nome).toBe('Ouvinte')
  })

  it('sem cookie válido, segue anônimo sem travar em "restoring"', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => jsonResponse(401, { type: '/erros/credenciais', title: 'sem sessão', status: 401 })),
    )

    renderHook(() => useSessionRestore())

    await waitFor(() => expect(useAuthStore.getState().restoring).toBe(false))
    expect(useAuthStore.getState().accessToken).toBeNull()
  })
})
