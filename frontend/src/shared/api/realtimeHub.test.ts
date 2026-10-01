import { HubConnectionState } from '@microsoft/signalr'
import { describe, expect, it, vi } from 'vitest'
import { atrasoDeRetry, criarDedupe, iniciarComRetry, registrarEventos } from './realtimeHub'

describe('atrasoDeRetry', () => {
  it('cresce exponencialmente, limita em 30s e nunca é null', () => {
    expect([0, 1, 2, 3, 4, 5, 6, 20].map(atrasoDeRetry)).toEqual([
      0, 2000, 4000, 8000, 16000, 30000, 30000, 30000,
    ])
  })
})

describe('iniciarComRetry', () => {
  it('repete start() até conseguir', async () => {
    const start = vi
      .fn()
      .mockRejectedValueOnce(new Error('x'))
      .mockRejectedValueOnce(new Error('x'))
      .mockResolvedValue(undefined)
    const esperar = vi.fn().mockResolvedValue(undefined)

    await iniciarComRetry({ start, state: HubConnectionState.Disconnected }, () => false, esperar)

    expect(start).toHaveBeenCalledTimes(3)
    expect(esperar).toHaveBeenNthCalledWith(1, 2000)
    expect(esperar).toHaveBeenNthCalledWith(2, 4000)
  })

  it('para quando cancelado (unmount)', async () => {
    const start = vi.fn().mockRejectedValue(new Error('x'))
    let cancelado = false
    const esperar = vi.fn(async () => {
      cancelado = true
    })

    await iniciarComRetry(
      { start, state: HubConnectionState.Disconnected },
      () => cancelado,
      esperar,
    )

    expect(start).toHaveBeenCalledTimes(1)
  })

  it('não chama start() se já não está Disconnected', async () => {
    const start = vi.fn()
    await iniciarComRetry({ start, state: HubConnectionState.Connected }, () => false)
    expect(start).not.toHaveBeenCalled()
  })
})

describe('criarDedupe', () => {
  it('marca repetidos e esquece os mais antigos acima do teto de 500', () => {
    const jaProcessado = criarDedupe()
    expect(jaProcessado('a')).toBe(false)
    expect(jaProcessado('a')).toBe(true)
    for (let i = 0; i < 500; i++) jaProcessado(`e${i}`)
    expect(jaProcessado('a')).toBe(false) // 'a' foi expulso
  })
})

describe('registrarEventos', () => {
  function montar() {
    const handlers = new Map<string, (dados: unknown) => void>()
    const connection = { on: (nome: string, h: (d: unknown) => void) => handlers.set(nome, h) }
    const invalidateQueries = vi.fn()
    registrarEventos(connection as never, { invalidateQueries } as never, criarDedupe())
    return { handlers, invalidateQueries }
  }

  it.each([
    ['ProgramaStatusAlterado', ['programa-ao-vivo', 'grade-semana']],
    ['TocandoAgora', ['stream-info']],
    ['PedidoCriado', ['pedidos-moderacao']],
    ['PedidoModerado', ['pedidos-meus']],
    ['PedidoTocado', ['pedidos-meus']],
    ['MidiaSanitizada', ['midias']],
  ])('%s invalida as queries certas', (evento, chaves) => {
    const { handlers, invalidateQueries } = montar()
    handlers.get(evento)!({ eventoId: 'x' })
    expect(invalidateQueries.mock.calls.map(([a]) => a.queryKey[0])).toEqual(chaves)
  })

  it('ignora evento repetido, mas sempre processa evento sem eventoId', () => {
    const { handlers, invalidateQueries } = montar()
    handlers.get('TocandoAgora')!({ eventoId: 'x' })
    handlers.get('TocandoAgora')!({ eventoId: 'x' })
    expect(invalidateQueries).toHaveBeenCalledTimes(1)
    handlers.get('TocandoAgora')!({})
    handlers.get('TocandoAgora')!({})
    expect(invalidateQueries).toHaveBeenCalledTimes(3)
  })
})
