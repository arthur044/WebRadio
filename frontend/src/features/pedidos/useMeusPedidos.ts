import { useQuery } from '@tanstack/react-query'
import { useAuthStore } from '../../shared/api/authStore'
import { apiFetch } from '../../shared/api/httpClient'
import type { PedidoDto } from '../../shared/api/types'

/**
 * Últimos 20 pedidos do dispositivo (ou da conta, com Bearer). O `usuarioId` entra na chave: o servidor responde por
 * conta quando há login, então o cache de antes do login não pode servir depois dele. O hub invalida pelo prefixo.
 */
export function useMeusPedidos() {
  const usuarioId = useAuthStore((s) => s.usuario?.id ?? null)
  return useQuery({
    queryKey: ['pedidos-meus', usuarioId],
    queryFn: () => apiFetch<PedidoDto[]>('/pedidos/meus', { includeListenerId: true }),
  })
}
