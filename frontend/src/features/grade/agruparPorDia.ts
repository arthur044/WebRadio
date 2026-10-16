import type { ProgramaDto } from '../../shared/api/types'
import { chaveDataBrasilia } from '../../shared/lib/timezone'

/** Agrupa por dia em Brasília, preservando a ordem cronológica que a API já devolve. */
export function agruparPorDia(programas: ProgramaDto[]): Array<[string, ProgramaDto[]]> {
  const porDia = new Map<string, ProgramaDto[]>()
  for (const programa of programas) {
    const chave = chaveDataBrasilia(programa.inicioUtc)
    const grupo = porDia.get(chave)
    if (grupo) grupo.push(programa)
    else porDia.set(chave, [programa])
  }
  return [...porDia.entries()].sort(([a], [b]) => a.localeCompare(b))
}
