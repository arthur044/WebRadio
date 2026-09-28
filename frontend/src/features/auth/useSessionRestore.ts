import { useEffect } from 'react'
import { useAuthStore } from '../../shared/api/authStore'
import { refreshSession } from '../../shared/api/httpClient'

/**
 * F5 mantém a sessão: tenta um /auth/refresh (cookie) uma vez, no boot da SPA.
 * Passa pelo MESMO lock ('wr-refresh') do 401 do httpClient (Guardian M2) — várias
 * abas restaurando ao mesmo tempo (o browser reabrindo a sessão anterior) não viram
 * N refresh concorrentes com o mesmo cookie, o que disparia a detecção de reuso.
 */
export function useSessionRestore() {
  useEffect(() => {
    let cancelled = false
    refreshSession(useAuthStore.getState().accessToken).finally(() => {
      if (!cancelled) useAuthStore.getState().setRestoring(false)
    })
    return () => {
      cancelled = true
    }
  }, [])
}
