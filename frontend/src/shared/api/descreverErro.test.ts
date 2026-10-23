import { describe, expect, it } from 'vitest'
import { ApiError } from './httpClient'
import { descreverErro, semVersao } from './descreverErro'

function erro(status: number, type: string, extras: object = {}) {
  return new ApiError(status, { type, title: 'x', status, ...extras })
}

describe('descreverErro', () => {
  it('409 conflito de grade: mensagem útil e o id do programa que conflita', () => {
    const d = descreverErro(erro(409, '/erros/conflito-grade', { programaConflitanteId: 'abc' }))
    expect(d.mensagem).toMatch(/já está ocupado/i)
    expect(d.conflitoComId).toBe('abc')
    expect(d.recarregar).toBe(false)
  })

  it('412 versão desatualizada: pede para recarregar, sem culpar o usuário', () => {
    const d = descreverErro(erro(412, '/erros/versao-desatualizada'))
    expect(d.mensagem).toMatch(/alterado por outra pessoa/i)
    expect(d.recarregar).toBe(true)
  })

  it('412 sem o type conhecido também pede para recarregar (decide pelo status)', () => {
    const d = descreverErro(erro(412, '/erros/outro'))
    expect(d.mensagem).toMatch(/alterado por outra pessoa/i)
    expect(d.recarregar).toBe(true)
  })

  it('428: não foi possível confirmar a versão', () => {
    const d = descreverErro(erro(428, '/erros/precondicao-obrigatoria'))
    expect(d.mensagem).toMatch(/versão/i)
    expect(d.recarregar).toBe(true)
  })

  it('409 transição inválida: o programa mudou de estado', () => {
    const d = descreverErro(erro(409, '/erros/transicao-invalida'))
    expect(d.mensagem).toMatch(/mudou de estado/i)
    expect(d.recarregar).toBe(true)
  })

  it('400 validação: separa por campo, normalizando PascalCase e camelCase', () => {
    const d = descreverErro(
      erro(400, '/erros/validacao', {
        errors: {
          Titulo: ['O título é obrigatório.'],
          FimUtc: [
            'O fim precisa ser depois do início.',
            'A duração precisa estar entre 5 minutos e 12 horas.',
          ],
          locutorId: ['O locutor precisa ser um usuário ativo com papel Locutor ou Admin.'],
        },
      }),
    )
    expect(d.campos.titulo).toBe('O título é obrigatório.')
    expect(d.campos.fimUtc).toBe(
      'O fim precisa ser depois do início. A duração precisa estar entre 5 minutos e 12 horas.',
    )
    expect(d.campos.locutorId).toMatch(/Locutor ou Admin/)
    expect(d.mensagem).toMatch(/corrija/i)
  })

  it('400 validação sem o mapa de erros ainda dá uma mensagem', () => {
    const d = descreverErro(erro(400, '/erros/validacao'))
    expect(d.campos).toEqual({})
    expect(d.mensagem).not.toMatch(/destacados/i)
    expect(d.mensagem).toMatch(/não foram aceitos/i)
  })

  it.each([
    [404, 'Programa'],
    [412, 'programa'],
    [428, 'programa'],
  ])('status %i: o texto não fala de programa (serve a auth e a credencial)', (status, palavra) => {
    const d = descreverErro(erro(status, '/erros/qualquer'))
    expect(d.mensagem).not.toContain(palavra)
  })

  it('semVersao() também é neutro', () => {
    expect(semVersao().mensagem).not.toMatch(/programa/i)
  })

  it.each([
    [401, /sessão/i],
    [403, /permissão/i],
    [404, /não encontrado/i],
    [429, /muitas tentativas/i],
    [503, /ocupado/i],
    [500, /tente novamente/i],
  ])('status %i vira mensagem própria', (status, padrao) => {
    expect(descreverErro(erro(status, '/erros/qualquer')).mensagem).toMatch(padrao)
  })

  it('falha de rede (fetch rejeitado) não vaza o texto técnico', () => {
    const d = descreverErro(new TypeError('Failed to fetch'))
    expect(d.mensagem).toMatch(/conexão/i)
    expect(d.mensagem).not.toMatch(/fetch/i)
  })

  it('erro desconhecido cai na mensagem genérica', () => {
    expect(descreverErro('boom').mensagem).toMatch(/tente novamente/i)
  })

  it('nunca mostra o detail cru do servidor', () => {
    const d = descreverErro(erro(409, '/erros/conflito-grade', { detail: 'SELECT * FROM x' }))
    expect(d.mensagem).not.toMatch(/SELECT/)
  })
})
