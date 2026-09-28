import { useAuthStore } from './authStore'
import { getListenerId } from './listenerId'
import type { LoginResponse, ProblemDetails } from './types'

const BASE_URL = '/api/v1'

/** Erro de UI tipado a partir de um ProblemDetails (RFC 9457, 01-dominio.md §3). */
export class ApiError extends Error {
  readonly status: number
  readonly problem: ProblemDetails

  constructor(status: number, problem: ProblemDetails) {
    super(problem.title || problem.type)
    this.name = 'ApiError'
    // status vem da RESPOSTA HTTP, não do corpo (Guardian L3): o ProblemDetails pode
    // não trazer `status`, ou trazer um valor que não bate com o real.
    this.status = status
    this.problem = problem
  }
}

/**
 * Guardian N2: rotas cujo 401 nunca deve disparar refresh+retry, decidido aqui em vez
 * de depender de cada call site lembrar de passar `skipRefreshRetry`. Login/registrar
 * errados não são sessão expirada; /auth/refresh já roda dentro do lock; logout está
 * encerrando a sessão de propósito. `/auth/trocar-senha` e `/auth/me` ficam FORA disso:
 * lá um 401 de token expirado deve renovar e repetir normalmente.
 */
const SEM_RETRY = new Set(['/auth/login', '/auth/registrar', '/auth/refresh', '/auth/logout'])

interface RequestOptions extends Omit<RequestInit, 'body'> {
  body?: unknown
  /** Pula o Authorization automático — usado por /auth/login e /auth/refresh. */
  skipAuth?: boolean
  /** Pula o retry automático em 401 além do que SEM_RETRY já cobre. */
  skipRefreshRetry?: boolean
  /**
   * X-Listener-Id só onde o contrato pede (/pedidos, /pedidos/meus, hub) — não em
   * toda chamada (Guardian L1): liga o dispositivo à conta sem necessidade em rotas
   * autenticadas, o que a LGPD não pede.
   */
  includeListenerId?: boolean
}

async function rawFetch(path: string, options: RequestOptions = {}): Promise<Response> {
  const { skipAuth, includeListenerId, body, headers, ...rest } = options
  const token = skipAuth ? null : useAuthStore.getState().accessToken

  const finalHeaders = new Headers(headers)
  if (includeListenerId) finalHeaders.set('X-Listener-Id', getListenerId())
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
 * já que é a Web Locks do browser, não um mutex do módulo JS. Usada tanto pelo 401
 * de `apiFetch` quanto pela restauração de sessão no boot (`useSessionRestore`,
 * Guardian M2) — as duas passam pelo MESMO lock.
 *
 * DENTRO da mesma aba: cada chamada guarda o token de antes do lock; se ao entrar no
 * lock o token já mudou (outra chamada da mesma aba renovou primeiro), pula a rede —
 * elas compartilham a mesma instância do authStore.
 *
 * ENTRE abas: NÃO dá pra pular a rede assim — a aba B tem sua própria instância do
 * authStore (memória isolada por aba) e não enxerga o token que a aba A recebeu. B
 * sempre faz o próprio POST /auth/refresh dentro do lock; como o lock serializou A
 * antes de B, o cookie de refresh que B envia já é o rotacionado por A (cookies são
 * do browser, compartilhados entre abas do mesmo origin) — B recebe seu próprio
 * access token novo a partir desse cookie já válido, sem disparar detecção de reuso.
 * Provado com Playwright de 2 páginas no mesmo contexto forçando a sobreposição real
 * das duas chamadas, com controle negativo (e2e/refresh-cross-tab.spec.ts).
 */
export async function refreshSession(tokenAtFailureTime: string | null): Promise<boolean> {
  return navigator.locks.request('wr-refresh', async () => {
    if (useAuthStore.getState().accessToken !== tokenAtFailureTime) {
      return true
    }

    try {
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
    } catch {
      // Falha de rede (não uma resposta HTTP de erro) — sem sessão pra restaurar,
      // segue anônimo em vez de estourar uma rejeição sem dono pra quem chamou.
      useAuthStore.getState().clearSession()
      return false
    }
  })
}

async function toApiError(response: Response): Promise<ApiError> {
  let problem: ProblemDetails
  try {
    problem = await response.json()
  } catch {
    problem = { type: '/erros/desconhecido', title: response.statusText, status: response.status }
  }
  return new ApiError(response.status, problem)
}

export async function apiFetch<T>(path: string, options: RequestOptions = {}): Promise<T> {
  const tokenBeforeRequest = useAuthStore.getState().accessToken
  let response = await rawFetch(path, options)

  // Guardian M1: sem token nenhum antes da chamada, não há sessão pra renovar — só
  // faz a rede extra à toa (anônimo) ou, pior, adota um cookie de OUTRO usuário
  // logado enquanto tenta logar com senha errada (conta 2 falhas no D21). Combinado
  // com SEM_RETRY (Guardian N2), que cobre /auth/login|registrar|refresh|logout sem
  // depender do call site lembrar da flag.
  const semRetry = options.skipRefreshRetry || SEM_RETRY.has(path)
  if (response.status === 401 && !semRetry && tokenBeforeRequest !== null) {
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
