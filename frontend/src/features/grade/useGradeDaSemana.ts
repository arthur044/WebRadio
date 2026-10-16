import { useQuery } from '@tanstack/react-query'
import { apiFetch } from '../../shared/api/httpClient'
import type { ProgramaDto } from '../../shared/api/types'
import { semanaParaUtc } from '../../shared/lib/timezone'

/**
 * Programas da semana que começa em `segunda` (Brasília). A chave começa por `'grade-semana'`: o
 * `ProgramaStatusAlterado` do hub invalida esse prefixo (realtimeHub.ts), então a grade pública e a
 * do Admin se atualizam sozinhas. `incluirCancelados` só vale para Admin (02 §4).
 */
export function useGradeDaSemana(segunda: string, incluirCancelados = false) {
  const { deUtc, ateUtc } = semanaParaUtc(segunda)
  const extra = incluirCancelados ? '&incluirCancelados=true' : ''

  return useQuery({
    queryKey: ['grade-semana', incluirCancelados ? 'admin' : 'publica', segunda],
    queryFn: () =>
      apiFetch<ProgramaDto[]>(
        `/programas?deUtc=${encodeURIComponent(deUtc)}&ateUtc=${encodeURIComponent(ateUtc)}${extra}`,
      ),
  })
}
