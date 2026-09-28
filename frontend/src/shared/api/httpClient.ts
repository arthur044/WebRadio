import { useAuthStore } from './authStore'
import { getListenerId } from './listenerId'
import type { LoginResponse, ProblemDetails } from './types'

const BASE_URL = '/api/v1'

/** Erro de UI tipado a partir de um ProblemDetails (RFC 9457, 01-dominio.md §3). */
export class ApiError extends Error {
  readonly status: number
  readonly problem: ProblemDetails

  constructor(problem: ProblemDetails) {
    super(problem.title || problem.type)
    this.name = 'ApiError'
    this.status = problem.status
    this.problem = problem
  }
}

interface RequestOptions extends Omit<RequestInit, 'body'> {
  body?: unknown
  /** Pula o Authorization automático — usado por /auth/login e /auth/refresh. */
  skipAuth?: boolean
  /** Pula o retry automático em 401 — o próprio refresh usa isso pra não entrar em loop. */
  skipRefreshRetry?: boolean
}

async function rawFetch(path: string, options: RequestOptions = {}): Promise<Response> {
  const { skipAuth, body, headers, ...rest } = options
  const token = skipAuth ? null : useAuthStore.getState().accessToken

  const finalHeaders = new Headers(headers)
  finalHeaders.set('X-Listener-Id', getListenerId())
  if (body !== undefined) finalHeaders.set('Content-Type', 'application/json')
  if (token) finalHeaders.set('Authorization', `Bearer ${token}`)

  return fetch(`${BASE_URL}${path}`, {
    ...rest,
    credentials: 'include',
    headers: finalHeaders,
    body: body !== undefined ? JSON.stringify(body) : undefined,
  })
}

/**
 * Single-flight de refresh (S-M10). `navigator.locks` serializa por nome de lock em
 * todo o origin — entre chamadas concorrentes NA MESMA aba e entre abas diferentes,
 * já que é a Web Locks do browser, não um mutex do módulo JS.
 *
 * DENTRO da mesma aba: cada chamada guarda o token de antes do lock; se ao entrar no
 * lock o token já mudou (outra chamada da mesma aba renovou primeiro), pula a rede —
 * elas compartilham a mesma instância do authStore.
 *
 * ENTRE abas: NÃO dá pra pular a rede assim — a aba B tem sua própria instância do
 * authStore (memória isolada por aba) e não enxerga o token que a aba A recebeu. B
 * sempre faz o próprio POST /auth/refresh dentro do lock; como o lock serializou A
 * antes de B, o cookie wr_refresh que B envia já é o rotacionado por A (cookies são
 * do browser, compartilhados entre abas do mesmo origin) — B recebe seu próprio
 * access token novo a partir desse cookie já válido, sem disparar detecção de reuso.
 * Confirmado com Playwright de 2 páginas no mesmo contexto (e2e/refresh-cross-tab).
 */
async function refreshSession(tokenAtFailureTime: string | null): Promise<boolean> {
  return navigator.locks.request('wr-refresh', async () => {
    if (useAuthStore.getState().accessToken !== tokenAtFailureTime) {
      return true
    }

    const response = await rawFetch('/auth/refresh', {
      method: 'POST',
      skipAuth: true,
      skipRefreshRetry: true,
    })

    if (!response.ok) {
      useAuthStore.getState().clearSession()
      return false
    }

    const data: LoginResponse = await response.json()
    useAuthStore.getState().setSession(data.accessToken, data.usuario)
    return true
  })
}

async function toApiError(response: Response): Promise<ApiError> {
  let problem: ProblemDetails
  try {
    problem = await response.json()
  } catch {
    problem = { type: '/erros/desconhecido', title: response.statusText, status: response.status }
  }
  return new ApiError(problem)
}

export async function apiFetch<T>(path: string, options: RequestOptions = {}): Promise<T> {
  const tokenBeforeRequest = useAuthStore.getState().accessToken
  let response = await rawFetch(path, options)

  if (response.status === 401 && !options.skipRefreshRetry) {
    const refreshed = await refreshSession(tokenBeforeRequest)
    if (refreshed) {
      response = await rawFetch(path, options)
    }
  }

  if (!response.ok) {
    throw await toApiError(response)
  }

  if (response.status === 204 || response.status === 202) {
    return undefined as T
  }

  return (await response.json()) as T
}
