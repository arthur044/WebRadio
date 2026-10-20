import { useQuery } from '@tanstack/react-query'
import { apiFetch } from '../../shared/api/httpClient'
import type { StreamInfoDto } from '../../shared/api/types'

/**
 * `GET /stream/info` (público): "tocando agora" e o estado do ao vivo. A chave `'stream-info'` é invalidada
 * por `TocandoAgora` e `TransmissaoAoVivoAlterada` e, ao reconectar o hub, por `invalidarEstadoDaRadio`.
 */
export function useStreamInfo() {
  return useQuery({
    queryKey: ['stream-info'],
    queryFn: () => apiFetch<StreamInfoDto>('/stream/info'),
    staleTime: 30_000,
  })
}

/** Texto da barra do player para o que está no ar. Ao vivo tem prioridade sobre a faixa da rotação. */
export function rotuloDoAgora(info: StreamInfoDto | undefined): string | null {
  if (!info) return null
  if (info.aoVivo?.noAr) {
    return info.aoVivo.locutorNome
      ? `Ao vivo com ${info.aoVivo.locutorNome}`
      : 'Transmissão ao vivo'
  }
  const faixa = info.tocandoAgora
  if (!faixa?.titulo) return null
  return faixa.artista ? `${faixa.artista} — ${faixa.titulo}` : faixa.titulo
}
