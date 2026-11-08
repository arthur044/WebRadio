import { useQuery, type QueryClient } from '@tanstack/react-query'
import { apiFetch } from '../../shared/api/httpClient'
import type { PaginaResposta, PedidoDto } from '../../shared/api/types'

export type StatusDaFila = 'Pendente' | 'Aprovado'

/** O contrato (02 §5) aceita só estes dois; `Aprovado` são os das últimas 24 h (E3-D9). Máximo de 100 por página. */
export function usePedidosDaFila(status: StatusDaFila) {
  return useQuery({
    queryKey: ['pedidos-moderacao', status],
    queryFn: () =>
      apiFetch<PaginaResposta<PedidoDto>>(`/pedidos?status=${status}&pagina=1&tamanho=100`),
  })
}

export type AcaoDeModeracao = 'aprovar' | 'rejeitar' | 'marcar-tocado'

/** `aprovar` leva `{}` (o `midiaId` só existe a partir do Épico 4); `rejeitar`, o motivo; `marcar-tocado`, nada. */
export async function moderarPedido(
  id: string,
  acao: AcaoDeModeracao,
  motivo?: string,
): Promise<void> {
  const corpo = acao === 'aprovar' ? {} : acao === 'rejeitar' ? { motivo } : undefined
  await apiFetch<void>(`/pedidos/${encodeURIComponent(id)}/${acao}`, {
    method: 'POST',
    body: corpo,
  })
}

/** Tira o pedido das duas listas já em memória (outro locutor o moderou) e relê as duas. */
export function removerDaFila(queryClient: QueryClient, id: string): void {
  queryClient.setQueriesData<PaginaResposta<PedidoDto>>(
    { queryKey: ['pedidos-moderacao'] },
    (atual) =>
      atual
        ? {
            ...atual,
            itens: atual.itens.filter((p) => p.id !== id),
            total: Math.max(0, atual.total - (atual.itens.some((p) => p.id === id) ? 1 : 0)),
          }
        : atual,
  )
  void queryClient.invalidateQueries({ queryKey: ['pedidos-moderacao'] })
}
