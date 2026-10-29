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
   * Não renova nem repete quando o 401 é `/erros/credenciais` (senha errada que a própria rota conta como tentativa),
   * mas renova e repete no 401 do token vencido, que o handler nunca vê.
   */
  semRefreshSeCredenciais?: boolean
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
  // FormData leva o `Content-Type: multipart/form-data; boundary=...` que só o browser sabe montar.
  const ehFormulario = body instanceof FormData
  if (body !== undefined && !ehFormulario) finalHeaders.set('Content-Type', 'application/json')
  if (token) finalHeaders.set('Authorization', `Bearer ${token}`)

  return fetch(`${BASE_URL}${path}`, {
    ...rest,
    credentials: 'include',
    headers: finalHeaders,
    body: body === undefined ? undefined : ehFormulario ? body : JSON.stringify(body),
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
      let response = await postRefresh()

      // Forge F07: 401 /erros/refresh-concorrente (Retry-After: 0) é uma corrida
      // perdida, não sessão morta — o cookie já foi rotacionado por outro request.
      // Refaz UMA vez; só /erros/credenciais (ou qualquer outra falha) derruba a sessão.
      if (response.status === 401 && (await isRefreshConcorrente(response))) {
        response = await postRefresh()
      }

      if (!response.ok) {
        // Só um 401 diz que a sessão morreu. 429 (balde do IP compartilhado), 5xx e rede não: derrubar a sessão por
        // isso deslogaria quem não mudou nada; a próxima chamada tenta de novo.
        if (response.status === 401) useAuthStore.getState().clearSession()
        return false
      }

      const data: LoginResponse = await response.json()
      useAuthStore.getState().setSession(data.accessToken, data.usuario)
      return true
    } catch {
      // Falha de rede (não uma resposta HTTP de erro): a sessão pode estar viva, então fica como está e não estoura
      // uma rejeição sem dono pra quem chamou. No boot não havia usuário, e segue anônimo do mesmo jeito.
      return false
    }
  })
}

function postRefresh(): Promise<Response> {
  return rawFetch('/auth/refresh', { method: 'POST', skipAuth: true, skipRefreshRetry: true })
}

/** O `type` do ProblemDetails da resposta (sem consumir o corpo), ou null se não houver corpo JSON. */
async function tipoDoProblema(response: Response): Promise<string | null> {
  if (response.ok) return null
  try {
    const problem: ProblemDetails = await response.clone().json()
    return problem.type ?? null
  } catch {
    return null
  }
}

async function isRefreshConcorrente(response: Response): Promise<boolean> {
  return (await tipoDoProblema(response)) === '/erros/refresh-concorrente'
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

/** Faz a chamada com o refresh+retry em 401 e devolve a resposta já validada (`ok`); senão lança `ApiError`. */
async function fetchComSessao(path: string, options: RequestOptions): Promise<Response> {
  const tokenBeforeRequest = useAuthStore.getState().accessToken
  let response = await rawFetch(path, options)

  // Guardian M1: sem token nenhum antes da chamada, não há sessão pra renovar — só
  // faz a rede extra à toa (anônimo) ou, pior, adota um cookie de OUTRO usuário
  // logado enquanto tenta logar com senha errada (conta 2 falhas no D21). Combinado
  // com SEM_RETRY (Guardian N2), que cobre /auth/login|registrar|refresh|logout sem
  // depender do call site lembrar da flag.
  const semRetry = options.skipRefreshRetry || SEM_RETRY.has(path)
  const poupar =
    options.semRefreshSeCredenciais && (await tipoDoProblema(response)) === '/erros/credenciais'
  if (response.status === 401 && !semRetry && !poupar && tokenBeforeRequest !== null) {
    const refreshed = await refreshSession(tokenBeforeRequest)
    if (refreshed) {
      response = await rawFetch(path, options)
    }
  }

  // Sentinel L2 do #42: o papel viaja no access token. Depois de rebaixar ou desativar a conta, o token velho segue
  // dando 403 e a tela montada continuaria à mostra. Renovar traz o papel de agora (ou derruba a sessão, se a conta
  // saiu), e o RequireRole reavalia. Só o 403 SEM `/erros/acesso-negado` entra (L2 do #57): esse tipo é a recusa de
  // domínio ("você não é o dono"), que um token novo não muda; a política de papel responde 403 sem corpo.
  // Aguarda, para a store já estar certa quando o chamador tratar o erro. Não repete a chamada: o 403 continua
  // valendo. `refreshSession` não passa por aqui, então não há laço.
  // O token é o de ANTES da chamada: com N 403 em paralelo, o lock deixa passar à rede só a primeira renovação; as
  // outras veem o token já trocado e pulam o `/auth/refresh` (Sentinel #3 do #57).
  if (
    response.status === 403 &&
    tokenBeforeRequest !== null &&
    !SEM_RETRY.has(path) &&
    (await tipoDoProblema(response)) !== '/erros/acesso-negado'
  ) {
    await refreshSession(tokenBeforeRequest)
  }

  if (!response.ok) {
    throw await toApiError(response)
  }

  return response
}

async function lerCorpo<T>(response: Response): Promise<T> {
  if (response.status === 204 || response.status === 202) {
    return undefined as T
  }

  return (await response.json()) as T
}

export async function apiFetch<T>(path: string, options: RequestOptions = {}): Promise<T> {
  return lerCorpo<T>(await fetchComSessao(path, options))
}

/**
 * Como `apiFetch`, mas devolve também o `ETag` da resposta (RowVersion em base64, entre aspas).
 * `GET` de recurso editável é a única fonte dele (02-api-rest.md, cabeçalho): o JSON não o traz.
 * O valor volta no `If-Match` do `PUT`/`PATCH`, exatamente como veio.
 */
export async function apiFetchComEtag<T>(
  path: string,
  options: RequestOptions = {},
): Promise<{ data: T; etag: string | null }> {
  const response = await fetchComSessao(path, options)
  return { data: await lerCorpo<T>(response), etag: response.headers.get('ETag') }
}
