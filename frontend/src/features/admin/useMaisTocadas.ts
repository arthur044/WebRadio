import { keepPreviousData, useQuery } from '@tanstack/react-query'
import { apiFetch } from '../../shared/api/httpClient'
import type { MidiaMaisTocadaDto } from '../../shared/api/types'
import { campoLocalBrasiliaParaUtc, somarDias } from '../../shared/lib/timezone'

/** O servidor recusa período acima de 366 dias (02 §11). */
export const PERIODO_MAXIMO_DIAS = 366
export const PERIODO_PADRAO_DIAS = 30

const PADRAO_DIA = /^\d{4}-\d{2}-\d{2}$/

export interface PeriodoUtc {
  /** Início, inclusive. */
  de: string
  /** Fim, exclusivo (a meia-noite depois do último dia escolhido). */
  ate: string
}

function diasEntre(deChave: string, ateChave: string): number {
  const ms = (c: string) => {
    const [a, m, d] = c.split('-').map(Number)
    return Date.UTC(a, m - 1, d, 12)
  }
  return Math.round((ms(ateChave) - ms(deChave)) / 86_400_000)
}

/**
 * Os dois `<input type="date">` (dia de Brasília, ambos inclusive) como o `[de, ate)` em UTC que a API espera. Devolve a
 * mensagem de erro quando o período não vale, em vez de deixar o servidor responder 400.
 */
export function periodoParaUtc(
  deChave: string,
  ateChave: string,
): { periodo: PeriodoUtc } | { erro: string } {
  if (!PADRAO_DIA.test(deChave) || !PADRAO_DIA.test(ateChave)) {
    return { erro: 'Informe as duas datas.' }
  }
  if (ateChave < deChave) return { erro: 'A data final não pode ser anterior à inicial.' }
  const dias = diasEntre(deChave, ateChave) + 1
  if (dias > PERIODO_MAXIMO_DIAS) {
    return { erro: `O período vai até ${PERIODO_MAXIMO_DIAS} dias; este tem ${dias}.` }
  }
  const de = campoLocalBrasiliaParaUtc(`${deChave}T00:00`)
  const ate = campoLocalBrasiliaParaUtc(`${somarDias(ateChave, 1)}T00:00`)
  if (de === null || ate === null) return { erro: 'Informe datas válidas.' }
  return { periodo: { de, ate } }
}

/** `GET /relatorios/mais-tocadas` (Admin): as mídias por número de reproduções no período. */
export function useMaisTocadas(periodo: PeriodoUtc | null) {
  return useQuery({
    queryKey: ['relatorio-mais-tocadas', periodo],
    queryFn: () =>
      apiFetch<MidiaMaisTocadaDto[]>(
        `/relatorios/mais-tocadas?de=${encodeURIComponent(periodo!.de)}&ate=${encodeURIComponent(periodo!.ate)}`,
      ),
    enabled: periodo !== null,
    placeholderData: keepPreviousData,
  })
}
