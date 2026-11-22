import { describe, expect, it } from 'vitest'
import { formatarEspera } from './useEspera'

describe('formatarEspera', () => {
  it.each([
    [0, '0:00'],
    [9, '0:09'],
    [272, '4:32'],
    [3599, '59:59'],
    [3600, '1:00:00'],
    [5400, '1:30:00'],
    [7200, '2:00:00'],
    [7261, '2:01:01'],
  ])('%i s -> %s', (segundos, esperado) => {
    expect(formatarEspera(segundos)).toBe(esperado)
  })
})
