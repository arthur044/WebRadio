import { useQuery } from '@tanstack/react-query'
import { apiFetch } from '../../shared/api/httpClient'
import type { ProgramaDto } from '../../shared/api/types'

/** `GET /programas/ao-vivo` (público): o programa em curso ou `null` (204). Invalidada por `ProgramaStatusAlterado`. */
export function useProgramaAoVivo() {
  return useQuery({
    queryKey: ['programa-ao-vivo'],
    queryFn: async () => (await apiFetch<ProgramaDto | undefined>('/programas/ao-vivo')) ?? null,
  })
}
