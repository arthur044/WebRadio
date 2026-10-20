import { describe, expect, it } from 'vitest'
import { formatarIdade } from './idade'

const AGORA = new Date('2026-01-14T12:00:00Z')

describe('formatarIdade', () => {
  it.each([
    ['2026-01-14T11:59:30Z', 'agora'],
    ['2026-01-14T12:00:30Z', 'agora'], // relógio do servidor adiantado
    ['2026-01-14T11:59:00Z', 'há 1 minuto'],
    ['2026-01-14T11:15:00Z', 'há 45 minutos'],
    ['2026-01-14T11:00:00Z', 'há 1 hora'],
    ['2026-01-14T00:00:00Z', 'há 12 horas'],
    ['2026-01-13T12:00:00Z', 'há 1 dia'],
    ['2026-01-04T12:00:00Z', 'há 10 dias'],
  ])('%s → %s', (iso, esperado) => {
    expect(formatarIdade(iso, AGORA)).toBe(esperado)
  })

  it('data inválida não quebra', () => {
    expect(formatarIdade('lixo', AGORA)).toBe('agora')
  })
})
