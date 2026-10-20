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

/** Converte um horário de parede em Brasília (ano/mês/dia/hora/minuto local) para o instante UTC correspondente. */
function horaBrasiliaParaUtc(ano: number, mes: number, dia: number, hora = 0, minuto = 0): Date {
  const palpite = new Date(Date.UTC(ano, mes - 1, dia, hora, minuto))
  const deslocamento = deslocamentoMinutosEm(palpite)
  return new Date(palpite.getTime() - deslocamento * 60_000)
}

const PADRAO_CHAVE_DIA = /^(\d{4})-(\d{2})-(\d{2})$/
const PADRAO_CAMPO_LOCAL = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2})$/

function doisDigitos(n: number): string {
  return String(n).padStart(2, '0')
}

/** Soma dias a uma chave `YYYY-MM-DD` de calendário puro (meio-dia UTC: nenhum fuso troca o dia). */
export function somarDias(chaveData: string, dias: number): string {
  const [, ano, mes, dia] = PADRAO_CHAVE_DIA.exec(chaveData) ?? []
  const data = new Date(Date.UTC(Number(ano), Number(mes) - 1, Number(dia) + dias, 12))
  return `${data.getUTCFullYear()}-${doisDigitos(data.getUTCMonth() + 1)}-${doisDigitos(data.getUTCDate())}`
}

/** `true` se `chave` é uma data de calendário real (`YYYY-MM-DD`) que cai numa segunda-feira. */
export function ehChaveDeSegunda(chave: string): boolean {
  const m = PADRAO_CHAVE_DIA.exec(chave)
  if (!m) return false
  const [ano, mes, dia] = m.slice(1).map(Number)
  const data = new Date(Date.UTC(ano, mes - 1, dia, 12))
  return (
    data.getUTCFullYear() === ano &&
    data.getUTCMonth() === mes - 1 &&
    data.getUTCDate() === dia &&
    data.getUTCDay() === 1
  )
}

/**
 * Chave `YYYY-MM-DD` da segunda-feira da semana (segunda a domingo) em que `referencia` cai em
 * Brasília. `referencia` existe só para teste determinístico; por padrão é "agora".
 */
export function chaveSegundaDaSemanaBrasilia(referencia: Date = new Date()): string {
  const hoje = partesData(referencia, { year: 'numeric', month: '2-digit', day: '2-digit' })
  const chaveHoje = `${hoje.year}-${hoje.month}-${hoje.day}`
  // Dia da semana como data de calendário pura (meio-dia UTC evita qualquer troca de dia por
  // fuso da máquina que roda o teste) — 0=domingo..6=sábado.
  const diaDaSemana = new Date(
    Date.UTC(Number(hoje.year), Number(hoje.month) - 1, Number(hoje.day), 12),
  ).getUTCDay()
  return somarDias(chaveHoje, -((diaDaSemana + 6) % 7))
}

/**
 * A semana que começa na segunda `chaveSegunda` (Brasília) como o intervalo `[deUtc, ateUtc)` que
 * `GET /programas?deUtc=&ateUtc=` espera (02-api-rest.md §4) — os dois em UTC terminando em `Z`.
 */
export function semanaParaUtc(chaveSegunda: string): { deUtc: string; ateUtc: string } {
  const [, ano, mes, dia] = PADRAO_CHAVE_DIA.exec(chaveSegunda) ?? []
  const [, anoFim, mesFim, diaFim] = PADRAO_CHAVE_DIA.exec(somarDias(chaveSegunda, 7)) ?? []
  return {
    deUtc: horaBrasiliaParaUtc(Number(ano), Number(mes), Number(dia)).toISOString(),
    ateUtc: horaBrasiliaParaUtc(Number(anoFim), Number(mesFim), Number(diaFim)).toISOString(),
  }
}

/** Semana atual (segunda a domingo) em horário de Brasília, como `[deUtc, ateUtc)`. */
export function semanaAtualBrasiliaParaUtc(referencia: Date = new Date()): {
  deUtc: string
  ateUtc: string
} {
  return semanaParaUtc(chaveSegundaDaSemanaBrasilia(referencia))
}

/** "12/01 a 18/01": de segunda a domingo da semana que começa em `chaveSegunda`. */
export function formatarSemanaBrasilia(chaveSegunda: string): string {
  const curta = (chave: string) => {
    const [, , mes, dia] = PADRAO_CHAVE_DIA.exec(chave) ?? []
    return `${dia}/${mes}`
  }
  return `${curta(chaveSegunda)} a ${curta(somarDias(chaveSegunda, 6))}`
}

/**
 * Instante UTC -> valor de um `<input type="datetime-local">` (`YYYY-MM-DDTHH:mm`) em Brasília. O
 * input não tem fuso: o valor é sempre lido e escrito como horário de parede de Brasília, nunca
 * como o fuso do navegador.
 */
export function utcParaCampoLocalBrasilia(isoUtc: string): string {
  const p = partesData(new Date(isoUtc), {
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
    hour12: false,
  })
  return `${p.year}-${p.month}-${p.day}T${p.hour === '24' ? '00' : p.hour}:${p.minute}`
}

/**
 * Valor de um `<input type="datetime-local">` lido como horário de Brasília -> instante UTC no
 * formato que a API exige (termina em `Z`, precisão de segundo). `null` se vazio ou inválido.
 */
export function campoLocalBrasiliaParaUtc(valor: string): string | null {
  const m = PADRAO_CAMPO_LOCAL.exec(valor)
  if (!m) return null
  const [ano, mes, dia, hora, minuto] = m.slice(1).map(Number)
  const calendario = new Date(Date.UTC(ano, mes - 1, dia))
  const diaExiste =
    calendario.getUTCFullYear() === ano &&
    calendario.getUTCMonth() === mes - 1 &&
    calendario.getUTCDate() === dia
  if (!diaExiste || hora > 23 || minuto > 59) return null
  return horaBrasiliaParaUtc(ano, mes, dia, hora, minuto).toISOString().replace('.000Z', 'Z')
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

/** "qua., 14/01 11:00" — dia e hora de um instante UTC em Brasília. */
export function formatarDataHoraBrasilia(isoUtc: string): string {
  return `${formatarDiaBrasilia(chaveDataBrasilia(isoUtc))} ${formatarHoraBrasilia(isoUtc)}`
}
