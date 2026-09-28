import { useEffect } from 'react'
import { useAuthStore } from '../../shared/api/authStore'
import { apiFetch } from '../../shared/api/httpClient'
import type { LoginResponse } from '../../shared/api/types'

/** F5 mantém a sessão: tenta um /auth/refresh (cookie) uma vez, no boot da SPA. */
export function useSessionRestore() {
  useEffect(() => {
    let cancelled = false
    apiFetch<LoginResponse>('/auth/refresh', {
      method: 'POST',
      skipAuth: true,
      skipRefreshRetry: true,
    })
      .then((data) => {
        if (cancelled) return
        useAuthStore.getState().setSession(data.accessToken, data.usuario)
      })
      .catch(() => {
        // sem cookie válido — segue anônimo, não é um erro pra reportar
      })
      .finally(() => {
        if (!cancelled) useAuthStore.getState().setRestoring(false)
      })
    return () => {
      cancelled = true
    }
  }, [])
}
