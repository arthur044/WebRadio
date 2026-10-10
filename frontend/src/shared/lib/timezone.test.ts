import { describe, expect, it } from 'vitest'
import { formatarHorarioBrasilia, semanaAtualBrasiliaParaUtc } from './timezone'

describe('semanaAtualBrasiliaParaUtc', () => {
  it('segunda a domingo da semana, para uma referência em horário comercial de Brasília', () => {
    // 2026-01-14 é quarta-feira; em UTC-3 (Brasília) ainda é quarta às 11h.
    const { deUtc, ateUtc } = semanaAtualBrasiliaParaUtc(new Date('2026-01-14T14:00:00Z'))
    expect(deUtc).toBe('2026-01-12T03:00:00.000Z') // segunda 2026-01-12 00:00 BRT
    expect(ateUtc).toBe('2026-01-19T03:00:00.000Z') // segunda seguinte 2026-01-19 00:00 BRT
  })

  it('usa o dia em Brasília, não o dia UTC, perto da virada da semana (Guardian-style edge case)', () => {
    // 2026-01-19T01:00:00Z já é segunda-feira em UTC, mas em Brasília (UTC-3) ainda é
    // domingo 2026-01-18 22:00 — a semana tem que ser a que TERMINA nesse domingo
    // (12 a 19), não a que começa nele.
    const { deUtc, ateUtc } = semanaAtualBrasiliaParaUtc(new Date('2026-01-19T01:00:00Z'))
    expect(deUtc).toBe('2026-01-12T03:00:00.000Z')
    expect(ateUtc).toBe('2026-01-19T03:00:00.000Z')
  })

  it('intervalo sempre tem exatamente 7 dias', () => {
    const { deUtc, ateUtc } = semanaAtualBrasiliaParaUtc(new Date('2026-06-05T12:00:00Z'))
    const dias = (new Date(ateUtc).getTime() - new Date(deUtc).getTime()) / (24 * 60 * 60 * 1000)
    expect(dias).toBe(7)
  })

  it('as duas datas terminam em Z (02-api-rest.md §4 exige offset zero explícito)', () => {
    const { deUtc, ateUtc } = semanaAtualBrasiliaParaUtc(new Date('2026-03-02T12:00:00Z'))
    expect(deUtc.endsWith('Z')).toBe(true)
    expect(ateUtc.endsWith('Z')).toBe(true)
  })
})

describe('formatarHorarioBrasilia', () => {
  it('formata um horário UTC como horário de Brasília', () => {
    // 2026-01-14T14:00:00Z = 2026-01-14 11:00 em Brasília (UTC-3).
    expect(formatarHorarioBrasilia('2026-01-14T14:00:00Z')).toMatch(/11:00/)
  })
})
