import { HubConnectionState } from '@microsoft/signalr'
import { describe, expect, it, vi } from 'vitest'
import { atrasoDeRetry, iniciarComRetry } from './realtimeHub'

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
