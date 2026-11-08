import { keepPreviousData, useQuery, type QueryClient } from '@tanstack/react-query'
import { ApiError, apiFetch, apiFetchComEtag, apiFetchStatus } from '../../shared/api/httpClient'
import type { MidiaDto, PaginaResposta, StatusSanitizacao, TipoMidia } from '../../shared/api/types'

export const TAMANHO_DA_PAGINA = 20

export interface FiltrosDaBiblioteca {
  tipo: TipoMidia | ''
  status: StatusSanitizacao | ''
  /** `ativo=` só vale para o Admin (02 §6); para o Locutor a tela nem oferece o filtro. */
  ativo: 'todas' | 'ativas' | 'desativadas'
  busca: string
  pagina: number
}

export const FILTROS_INICIAIS: FiltrosDaBiblioteca = {
  tipo: '',
  status: '',
  ativo: 'todas',
  busca: '',
  pagina: 1,
}

function paraQuery(filtros: Partial<FiltrosDaBiblioteca>, tamanho: number): string {
  const q = new URLSearchParams()
  if (filtros.tipo) q.set('tipo', filtros.tipo)
  if (filtros.status) q.set('status', filtros.status)
  if (filtros.ativo === 'ativas') q.set('ativo', 'true')
  if (filtros.ativo === 'desativadas') q.set('ativo', 'false')
  const busca = filtros.busca?.trim()
  if (busca) q.set('busca', busca)
  q.set('pagina', String(filtros.pagina ?? 1))
  q.set('tamanho', String(tamanho))
  return q.toString()
}

/**
 * A biblioteca. A chave começa em `['midias']`: é o prefixo que o hub invalida no `MidiaSanitizada` e na reconexão
 * (realtimeHub), então o status muda na tela sem recarregar.
 */
export function useMidias(filtros: FiltrosDaBiblioteca) {
  return useQuery({
    queryKey: ['midias', filtros],
    queryFn: () =>
      apiFetch<PaginaResposta<MidiaDto>>(`/midias?${paraQuery(filtros, TAMANHO_DA_PAGINA)}`),
    placeholderData: keepPreviousData,
  })
}

/** O diálogo de "aprovar com mídia" (E4-D6): só `Musica`, `Aprovado` e ativa. `busca` vazia lista as primeiras. */
export function useMusicasParaPedido(busca: string) {
  return useQuery({
    queryKey: ['midias', 'para-pedido', busca.trim()],
    queryFn: () =>
      apiFetch<PaginaResposta<MidiaDto>>(
        `/midias?${paraQuery({ tipo: 'Musica', status: 'Aprovado', ativo: 'ativas', busca }, 10)}`,
      ),
    placeholderData: keepPreviousData,
  })
}

/**
 * A mídia com o ETag do `If-Match` do `PATCH`. Cada abertura lê de novo (`gcTime` 0) e nada o relê sozinho, porque
 * remontar o formulário no meio da digitação perderia o que a pessoa escreveu (mesmo desenho das divulgações).
 */
export function useMidiaParaEditar(id: string | null) {
  return useQuery({
    queryKey: ['midia-detalhe', id],
    queryFn: () => apiFetchComEtag<MidiaDto>(`/midias/${encodeURIComponent(id!)}`),
    enabled: id !== null,
    gcTime: 0,
    staleTime: Infinity,
    refetchOnWindowFocus: false,
    refetchOnReconnect: false,
    retry: false,
  })
}

export interface AlteracaoDeMidia {
  titulo?: string | null
  artista?: string | null
  /** Só o Admin. */
  ativo?: boolean
}

/** Liga ou desliga a mídia (Admin). O ETag vem de uma leitura de agora: o `PATCH` exige `If-Match` (428 sem ele). */
export async function alterarAtivo(id: string, ativo: boolean): Promise<MidiaDto> {
  const { etag } = await apiFetchComEtag<MidiaDto>(`/midias/${encodeURIComponent(id)}`)
  if (etag === null)
    throw new ApiError(428, { type: '/erros/versao-ausente', title: 'Sem versão', status: 428 })
  return atualizarMidia(id, { ativo }, etag)
}

export function atualizarMidia(
  id: string,
  alteracao: AlteracaoDeMidia,
  etag: string,
): Promise<MidiaDto> {
  return apiFetch<MidiaDto>(`/midias/${encodeURIComponent(id)}`, {
    method: 'PATCH',
    headers: { 'If-Match': etag },
    body: alteracao,
  })
}

/** 204 = apagada; 200 = havia reprodução, então só foi desativada (histórico preservado, 02 §6). */
export async function excluirMidia(id: string): Promise<'excluida' | 'desativada'> {
  const status = await apiFetchStatus(`/midias/${encodeURIComponent(id)}`, { method: 'DELETE' })
  return status === 204 ? 'excluida' : 'desativada'
}

export function recarregarMidias(queryClient: QueryClient): Promise<unknown> {
  return queryClient.invalidateQueries({ queryKey: ['midias'] })
}

/**
 * URL de pré-escuta. O `<audio>` não manda `Authorization`, e o 302 do `GET /midias/{id}/preview` reenviaria o Bearer
 * ao MinIO junto da assinatura; por isso `?formato=url` devolve a URL pré-assinada em JSON (`{ url, expiraEmUtc }`, 5 min; combinado com o Forge, E4-F03).
 * Só aceita o que a API prometeu: mesma origem, sob `/storage/midia/`. Nada vindo da rede vira `src` sem passar daqui.
 */
export async function resolverUrlDePreview(id: string): Promise<string> {
  const { url } = await apiFetch<{ url: string }>(
    `/midias/${encodeURIComponent(id)}/preview?formato=url`,
  )
  let alvo: URL
  try {
    alvo = new URL(url, window.location.origin)
  } catch {
    throw new Error('URL de pré-escuta inesperada')
  }
  // `//outro-host/x` e `javascript:` caem aqui: a origem não é a nossa.
  if (alvo.origin !== window.location.origin || !alvo.pathname.startsWith('/storage/midia/')) {
    throw new Error('URL de pré-escuta inesperada')
  }
  return alvo.href
}
