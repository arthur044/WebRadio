import { act, render, screen } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { StreamInfoDto } from '../../shared/api/types'
import { PlayerBar } from './PlayerBar'
import { usePlayerStore } from './playerStore'
import { rotuloDoAgora } from './useStreamInfo'

function info(extras: Partial<StreamInfoDto> = {}): StreamInfoDto {
  return {
    url: '/stream/radio.mp3',
    formato: 'audio/mpeg',
    bitrateKbps: 128,
    tocandoAgora: null,
    aoVivo: { noAr: false },
    ...extras,
  }
}

function renderPlayer(dados: StreamInfoDto) {
  vi.stubGlobal(
    'fetch',
    vi.fn(
      async () =>
        new Response(JSON.stringify(dados), { headers: { 'Content-Type': 'application/json' } }),
    ),
  )
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  const utils = render(
    <QueryClientProvider client={queryClient}>
      <PlayerBar />
    </QueryClientProvider>,
  )
  return { queryClient, ...utils }
}

afterEach(() => {
  vi.unstubAllGlobals()
  usePlayerStore.setState({
    status: 'idle',
    nowPlaying: null,
    userPaused: false,
    reconnectAttempt: 0,
  })
})

describe('rotuloDoAgora', () => {
  it('sem dados, nada', () => {
    expect(rotuloDoAgora(undefined)).toBeNull()
    expect(rotuloDoAgora(info())).toBeNull()
  })

  it('faixa da rotação: artista e título, ou só o título', () => {
    expect(
      rotuloDoAgora(info({ tocandoAgora: { titulo: 'Blue', artista: 'Joni', tipo: 'Musica' } })),
    ).toBe('Joni — Blue')
    expect(
      rotuloDoAgora(info({ tocandoAgora: { titulo: 'Blue', artista: null, tipo: null } })),
    ).toBe('Blue')
  })

  it('ao vivo tem prioridade sobre a faixa', () => {
    const ao = info({
      tocandoAgora: { titulo: 'Blue', artista: 'Joni', tipo: 'Musica' },
      aoVivo: { noAr: true, programaId: 'p1', locutorNome: 'Ana' },
    })
    expect(rotuloDoAgora(ao)).toBe('Ao vivo com Ana')
    expect(rotuloDoAgora(info({ aoVivo: { noAr: true } }))).toBe('Transmissão ao vivo')
  })
})

describe('PlayerBar', () => {
  it('mostra a faixa do "tocando agora"', async () => {
    renderPlayer(info({ tocandoAgora: { titulo: 'Blue', artista: 'Joni', tipo: 'Musica' } }))

    expect(await screen.findByText('Joni — Blue')).toBeInTheDocument()
    expect(usePlayerStore.getState().nowPlaying).toBe('Joni — Blue')
  })

  it('mostra o locutor quando há transmissão ao vivo e troca o selo para "Ao vivo"', async () => {
    renderPlayer(info({ aoVivo: { noAr: true, programaId: 'p1', locutorNome: 'Ana' } }))
    expect(await screen.findByText('Ao vivo com Ana')).toBeInTheDocument()

    act(() => usePlayerStore.setState({ status: 'playing' }))
    expect(screen.getByText('Ao vivo')).toBeInTheDocument()
  })

  it('tocando a rotação, o selo é "No ar" (não "Ao vivo")', async () => {
    renderPlayer(info())
    await screen.findByText('WebRadio')

    act(() => usePlayerStore.setState({ status: 'playing' }))

    expect(screen.getByText('No ar')).toBeInTheDocument()
    expect(screen.queryByText('Ao vivo')).not.toBeInTheDocument()
  })

  it('com o áudio caído, uma resposta nova do /stream/info reconecta sem esperar o backoff', async () => {
    const { queryClient } = renderPlayer(info())
    await screen.findByText('WebRadio')
    const play = vi.spyOn(HTMLMediaElement.prototype, 'play').mockResolvedValue()
    vi.spyOn(HTMLMediaElement.prototype, 'load').mockImplementation(() => {})
    act(() => usePlayerStore.setState({ status: 'error', userPaused: false }))

    await act(async () => {
      await queryClient.invalidateQueries({ queryKey: ['stream-info'] })
    })

    expect(play).toHaveBeenCalled()
    expect(usePlayerStore.getState().status).toBe('connecting')
  })

  it('quem pausou de propósito não é reconectado', async () => {
    const { queryClient } = renderPlayer(info())
    await screen.findByText('WebRadio')
    const play = vi.spyOn(HTMLMediaElement.prototype, 'play').mockResolvedValue()
    act(() => usePlayerStore.setState({ status: 'error', userPaused: true }))

    await act(async () => {
      await queryClient.invalidateQueries({ queryKey: ['stream-info'] })
    })

    expect(play).not.toHaveBeenCalled()
  })
})
