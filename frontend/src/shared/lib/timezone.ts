const FUSO_BRASILIA = 'America/Sao_Paulo'

function partesData(data: Date, opcoes: Intl.DateTimeFormatOptions): Record<string, string> {
  const formatador = new Intl.DateTimeFormat('en-US', { timeZone: FUSO_BRASILIA, ...opcoes })
  return Object.fromEntries(
    formatador.formatToParts(data).map((parte) => [parte.type, parte.value]),
  )
}

/**
 * Deslocamento (em minutos) tal que `horaLocal = instante + deslocamento`. O Brasil não tem
 * mais horário de verão desde 2019 — Brasília fica fixo em UTC-3 —, então uma única leitura
 * já é exata; não precisa da iteração de convergência que fusos com DST exigiriam.
 */
function deslocamentoMinutosEm(instante: Date): number {
  const p = partesData(instante, {
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit',
    hour12: false,
  })
  const comoUtc = Date.UTC(
    Number(p.year),
    Number(p.month) - 1,
    Number(p.day),
    Number(p.hour) === 24 ? 0 : Number(p.hour),
    Number(p.minute),
    Number(p.second),
  )
  return (comoUtc - instante.getTime()) / 60_000
}

/** Converte um horário de parede em Brasília (ano/mês/dia/hora local) para o instante UTC correspondente. */
function horaBrasiliaParaUtc(ano: number, mes: number, dia: number, hora = 0): Date {
  const palpite = new Date(Date.UTC(ano, mes - 1, dia, hora))
  const deslocamento = deslocamentoMinutosEm(palpite)
  return new Date(palpite.getTime() - deslocamento * 60_000)
}

/**
 * Semana atual (segunda a domingo) em horário de Brasília, como o intervalo `[deUtc, ateUtc)`
 * que `GET /programas?deUtc=&ateUtc=` espera (02-api-rest.md §4) — os dois em UTC terminando
 * em `Z`. `referencia` existe só para teste determinístico; por padrão é "agora".
 */
export function semanaAtualBrasiliaParaUtc(referencia: Date = new Date()): {
  deUtc: string
  ateUtc: string
} {
  const hoje = partesData(referencia, { year: 'numeric', month: '2-digit', day: '2-digit' })
  const ano = Number(hoje.year)
  const mes = Number(hoje.month)
  const dia = Number(hoje.day)

  // Dia da semana como data de calendário pura (meio-dia UTC evita qualquer troca de dia por
  // fuso da máquina que roda o teste) — 0=domingo..6=sábado.
  const diaDaSemana = new Date(Date.UTC(ano, mes - 1, dia, 12)).getUTCDay()
  const diasDesdeSegunda = (diaDaSemana + 6) % 7

  const inicioDia = dia - diasDesdeSegunda
  const fimDia = inicioDia + 7

  return {
    deUtc: horaBrasiliaParaUtc(ano, mes, inicioDia).toISOString(),
    ateUtc: horaBrasiliaParaUtc(ano, mes, fimDia).toISOString(),
  }
}

/** Hora (HH:mm) de um instante UTC em Brasília — via `formatToParts`, sem depender do separador do ICU. */
export function formatarHoraBrasilia(isoUtc: string): string {
  const partes = partesData(new Date(isoUtc), { hour: '2-digit', minute: '2-digit', hour12: false })
  const hora = partes.hour === '24' ? '00' : partes.hour
  return `${hora}:${partes.minute}`
}

/** Chave `YYYY-MM-DD` do dia em Brasília — pra agrupar itens que caem no mesmo dia local. */
export function chaveDataBrasilia(isoUtc: string): string {
  const p = partesData(new Date(isoUtc), { year: 'numeric', month: '2-digit', day: '2-digit' })
  return `${p.year}-${p.month}-${p.day}`
}

/** Cabeçalho de dia em Brasília, ex. "seg., 12/01" — a partir da mesma chave `YYYY-MM-DD`. */
export function formatarDiaBrasilia(chaveData: string): string {
  const [ano, mes, dia] = chaveData.split('-').map(Number)
  // Meio-dia UTC: representa o dia de calendário sem risco de cair no dia anterior/seguinte
  // por causa do fuso, só pra formatar — não é usado pra nenhum cálculo de instante.
  return new Intl.DateTimeFormat('pt-BR', {
    timeZone: 'UTC',
    weekday: 'short',
    day: '2-digit',
    month: '2-digit',
  }).format(new Date(Date.UTC(ano, mes - 1, dia, 12)))
}
