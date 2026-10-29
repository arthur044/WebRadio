import { useQuery, type QueryClient } from '@tanstack/react-query'
import { apiFetch, apiFetchComEtag } from '../../shared/api/httpClient'
import type {
  DivulgacaoDto,
  DivulgacaoRequest,
  ImagemEnviadaDto,
  PaginaResposta,
} from '../../shared/api/types'

/** Todas as divulgações (Admin), ativas ou não. Máximo de 100 por página (02 §7). */
export function useListaDeDivulgacoes() {
  return useQuery({
    queryKey: ['divulgacoes-admin'],
    queryFn: () => apiFetch<PaginaResposta<DivulgacaoDto>>('/divulgacoes?pagina=1&tamanho=100'),
  })
}

/**
 * A divulgação com o ETag do `If-Match` do `PUT`. O ETag só vem do GET do recurso: cada abertura lê de novo (`gcTime`
 * 0) e nada o relê sozinho, porque remontar o formulário no meio da digitação perderia o que o Admin escreveu.
 */
export function useDivulgacaoParaEditar(id: string | null) {
  return useQuery({
    queryKey: ['divulgacao-detalhe', id],
    queryFn: () => apiFetchComEtag<DivulgacaoDto>(`/divulgacoes/${encodeURIComponent(id!)}`),
    enabled: id !== null,
    gcTime: 0,
    staleTime: Infinity,
    refetchOnWindowFocus: false,
    refetchOnReconnect: false,
    retry: false,
  })
}

/** `POST /divulgacoes/imagens`: multipart com uma parte `file`. A API recodifica para WebP e devolve só a chave. */
export function enviarImagem(arquivo: File): Promise<ImagemEnviadaDto> {
  const corpo = new FormData()
  corpo.append('file', arquivo)
  return apiFetch<ImagemEnviadaDto>('/divulgacoes/imagens', { method: 'POST', body: corpo })
}

/** `POST` (sem `etag`) ou `PUT` com `If-Match`. O `PUT` substitui tudo: o corpo leva a `imagemChave` de qualquer jeito. */
export function salvarDivulgacao(
  id: string | null,
  corpo: DivulgacaoRequest,
  etag: string | null,
): Promise<DivulgacaoDto> {
  return apiFetch<DivulgacaoDto>(id ? `/divulgacoes/${encodeURIComponent(id)}` : '/divulgacoes', {
    method: id ? 'PUT' : 'POST',
    headers: id && etag ? { 'If-Match': etag } : undefined,
    body: corpo,
  })
}

export function removerDivulgacao(id: string): Promise<void> {
  return apiFetch<void>(`/divulgacoes/${encodeURIComponent(id)}`, { method: 'DELETE' })
}

/** A lista do Admin e o Mural público mostram a mesma coisa: depois de mexer, as duas releem. */
export function recarregarDivulgacoes(queryClient: QueryClient): Promise<unknown> {
  return Promise.all([
    queryClient.invalidateQueries({ queryKey: ['divulgacoes-admin'] }),
    queryClient.invalidateQueries({ queryKey: ['divulgacoes-ativas'] }),
  ])
}
