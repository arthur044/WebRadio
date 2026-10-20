const MINUTO = 60_000
const HORA = 60 * MINUTO
const DIA = 24 * HORA

/** "há 3 dias", "há 5 minutos", "agora": idade de um instante UTC em relação a `agora`. */
export function formatarIdade(isoUtc: string, agora: Date = new Date()): string {
  const ms = agora.getTime() - new Date(isoUtc).getTime()
  if (!Number.isFinite(ms) || ms < MINUTO) return 'agora'
  if (ms < HORA) return plural(Math.floor(ms / MINUTO), 'minuto')
  if (ms < DIA) return plural(Math.floor(ms / HORA), 'hora')
  return plural(Math.floor(ms / DIA), 'dia')
}

function plural(n: number, unidade: string): string {
  return `há ${n} ${unidade}${n === 1 ? '' : 's'}`
}
