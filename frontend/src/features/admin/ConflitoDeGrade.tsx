import { useQuery } from '@tanstack/react-query'
import { apiFetch } from '../../shared/api/httpClient'
import type { ProgramaDto } from '../../shared/api/types'
import { formatarDataHoraBrasilia, formatarHoraBrasilia } from '../../shared/lib/timezone'

/**
 * O 409 de conflito traz só o `programaConflitanteId`: busca o programa (rota pública) para dizer
 * ao Admin QUAL programa ocupa o horário. Se a busca falhar, não mostra nada: a mensagem principal
 * do erro já explica o conflito.
 */
export function ConflitoDeGrade({ programaId }: { programaId: string }) {
  const { data } = useQuery({
    queryKey: ['programa-conflito', programaId],
    queryFn: () => apiFetch<ProgramaDto>(`/programas/${programaId}`),
    retry: false,
  })

  if (!data) return null

  return (
    <p data-testid="conflito-grade" className="text-text mt-1 text-sm">
      Conflita com «{data.titulo}», {formatarDataHoraBrasilia(data.inicioUtc)} às{' '}
      {formatarHoraBrasilia(data.fimUtc)}, com {data.locutor.nome}.
    </p>
  )
}
