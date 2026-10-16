import { describe, expect, it } from 'vitest'
import {
  campoLocalBrasiliaParaUtc,
  ehChaveDeSegunda,
  chaveSegundaDaSemanaBrasilia,
  formatarDataHoraBrasilia,
  formatarSemanaBrasilia,
  formatarHoraBrasilia,
  semanaAtualBrasiliaParaUtc,
  semanaParaUtc,
  somarDias,
  utcParaCampoLocalBrasilia,
} from './timezone'

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

describe('formatarHoraBrasilia', () => {
  it('formata um horário UTC como horário de Brasília', () => {
    // 2026-01-14T14:00:00Z = 2026-01-14 11:00 em Brasília (UTC-3).
    expect(formatarHoraBrasilia('2026-01-14T14:00:00Z')).toBe('11:00')
  })
})

describe('chaveSegundaDaSemanaBrasilia', () => {
  it('devolve a segunda-feira da semana em Brasília', () => {
    // 2026-01-14 é quarta-feira.
    expect(chaveSegundaDaSemanaBrasilia(new Date('2026-01-14T14:00:00Z'))).toBe('2026-01-12')
  })

  it('domingo à noite em Brasília ainda é a semana que termina nele, mesmo já sendo segunda em UTC', () => {
    expect(chaveSegundaDaSemanaBrasilia(new Date('2026-01-19T01:00:00Z'))).toBe('2026-01-12')
  })

  it('segunda 00:00 em Brasília (03:00Z) já abre a semana nova; 02:59Z ainda é a anterior', () => {
    expect(chaveSegundaDaSemanaBrasilia(new Date('2026-01-19T03:00:00Z'))).toBe('2026-01-19')
    expect(chaveSegundaDaSemanaBrasilia(new Date('2026-01-19T02:59:59Z'))).toBe('2026-01-12')
  })
})

describe('somarDias', () => {
  it('atravessa mês e ano', () => {
    expect(somarDias('2026-01-26', 7)).toBe('2026-02-02')
    expect(somarDias('2025-12-29', 7)).toBe('2026-01-05')
    expect(somarDias('2026-03-02', -7)).toBe('2026-02-23')
  })

  it('respeita fevereiro bissexto', () => {
    expect(somarDias('2028-02-28', 1)).toBe('2028-02-29')
    expect(somarDias('2027-02-28', 1)).toBe('2027-03-01')
  })
})

describe('semanaParaUtc', () => {
  it('[segunda 00:00 BRT, segunda seguinte 00:00 BRT) em UTC', () => {
    expect(semanaParaUtc('2026-01-12')).toEqual({
      deUtc: '2026-01-12T03:00:00.000Z',
      ateUtc: '2026-01-19T03:00:00.000Z',
    })
  })

  it('é o mesmo intervalo que semanaAtualBrasiliaParaUtc para a semana corrente', () => {
    const referencia = new Date('2026-06-05T12:00:00Z')
    expect(semanaParaUtc(chaveSegundaDaSemanaBrasilia(referencia))).toEqual(
      semanaAtualBrasiliaParaUtc(referencia),
    )
  })

  it('cabe no limite de 31 dias da API em qualquer semana', () => {
    for (const segunda of ['2026-01-12', '2026-02-23', '2028-02-28', '2026-12-28']) {
      const { deUtc, ateUtc } = semanaParaUtc(segunda)
      const dias = (new Date(ateUtc).getTime() - new Date(deUtc).getTime()) / 86_400_000
      expect(dias).toBe(7)
    }
  })
})

describe('formatarSemanaBrasilia', () => {
  it('mostra de segunda a domingo', () => {
    expect(formatarSemanaBrasilia('2026-01-12')).toBe('12/01 a 18/01')
  })

  it('atravessa a virada do mês e do ano', () => {
    expect(formatarSemanaBrasilia('2025-12-29')).toBe('29/12 a 04/01')
  })
})

describe('utcParaCampoLocalBrasilia / campoLocalBrasiliaParaUtc', () => {
  it('converte UTC para o valor de um <input type="datetime-local"> em Brasília', () => {
    expect(utcParaCampoLocalBrasilia('2026-01-12T12:00:00Z')).toBe('2026-01-12T09:00')
    // Meia-noite em Brasília (03:00Z) não pode virar "24:00".
    expect(utcParaCampoLocalBrasilia('2026-01-12T03:00:00Z')).toBe('2026-01-12T00:00')
    // Virada de dia: 02:30Z ainda é o dia anterior em Brasília.
    expect(utcParaCampoLocalBrasilia('2026-01-12T02:30:00Z')).toBe('2026-01-11T23:30')
  })

  it('converte o campo local (Brasília) para UTC terminando em Z, sem fração de segundo', () => {
    expect(campoLocalBrasiliaParaUtc('2026-01-12T09:00')).toBe('2026-01-12T12:00:00Z')
    expect(campoLocalBrasiliaParaUtc('2026-01-11T23:30')).toBe('2026-01-12T02:30:00Z')
    expect(campoLocalBrasiliaParaUtc('2026-01-12T00:00')).toBe('2026-01-12T03:00:00Z')
  })

  it('ida e volta preserva o horário', () => {
    for (const iso of ['2026-01-12T12:00:00Z', '2026-07-01T02:15:00Z', '2026-12-31T23:59:00Z']) {
      expect(campoLocalBrasiliaParaUtc(utcParaCampoLocalBrasilia(iso))).toBe(iso)
    }
  })

  it('rejeita campo vazio ou malformado em vez de enviar lixo para a API', () => {
    expect(campoLocalBrasiliaParaUtc('')).toBeNull()
    expect(campoLocalBrasiliaParaUtc('2026-13-40T99:99')).toBeNull()
    expect(campoLocalBrasiliaParaUtc('amanhã')).toBeNull()
  })
})

describe('formatarDataHoraBrasilia', () => {
  it('data curta e hora de Brasília', () => {
    expect(formatarDataHoraBrasilia('2026-01-14T14:00:00Z')).toMatch(/14\/01.*11:00/)
  })
})

describe('ehChaveDeSegunda', () => {
  it('aceita só uma segunda-feira de calendário válida em YYYY-MM-DD', () => {
    expect(ehChaveDeSegunda('2026-01-12')).toBe(true)
    expect(ehChaveDeSegunda('2026-01-13')).toBe(false) // terça
    expect(ehChaveDeSegunda('2026-02-30')).toBe(false) // dia que não existe
    expect(ehChaveDeSegunda('12/01/2026')).toBe(false)
    expect(ehChaveDeSegunda('')).toBe(false)
  })
})
