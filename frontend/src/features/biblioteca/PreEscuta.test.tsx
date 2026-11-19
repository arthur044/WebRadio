import { act, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { useAuthStore } from '../../shared/api/authStore'
import type { MidiaDto } from '../../shared/api/types'
import { PreEscuta } from './PreEscuta'

const midia: MidiaDto = {
  id: 'a',
  nomeOriginal: 'a.mp3',
  titulo: 'Faixa a',
  artista: 'Artista',
  tipoMidia: 'Musica',
  mimeType: 'audio/mpeg',
  tamanhoBytes: 3 * 1024 * 1024,
  duracaoSegundos: 1200,
  statusSanitizacao: 'Aprovado',
  motivoRejeicao: null,
  dataUploadUtc: '2026-11-08T15:00:00Z',
  ativo: true,
  enviadoPorNome: 'Lia Locutora',
  podeEditar: true,
}

function urlNum(n: number): string {
  return `${window.location.origin}/storage/midia/2026/a.mp3?X-Amz-Signature=sig${n}`
}

/** Cada chamada a `?formato=url` devolve uma assinatura nova, vencendo `vidaMs` depois de agora. */
function montarApi(vidaMs = 300_000) {
  let n = 0
  const fetchMock = vi.fn(async () => {
    n += 1
    return new Response(
      JSON.stringify({
        url: `/storage/midia/2026/a.mp3?X-Amz-Signature=sig${n}`,
        expiraEmUtc: new Date(Date.now() + vidaMs).toISOString(),
      }),
      { status: 200, headers: { 'Content-Type': 'application/json' } },
    )
  })
  vi.stubGlobal('fetch', fetchMock)
  return fetchMock
}

function definirTempo(audio: HTMLAudioElement, segundos: number) {
  Object.defineProperty(audio, 'currentTime', {
    value: segundos,
    writable: true,
    configurable: true,
  })
}

describe('PreEscuta (E4-P01): URL que vence no meio da pré-escuta', () => {
  beforeEach(() => {
    useAuthStore.setState({
      accessToken: 't',
      restoring: false,
      usuario: { id: 'u', nome: 'Lia', email: 'l@x', role: 'Locutor', deveTrocarSenha: false },
    })
  })
  afterEach(() => {
    vi.useRealTimers()
    vi.restoreAllMocks()
    vi.unstubAllGlobals()
  })

  it('no error do <audio> pede outra URL e retoma do currentTime', async () => {
    const api = montarApi()
    render(<PreEscuta midia={midia} aoFechar={() => {}} />)

    const audio = (await screen.findByLabelText('Pré-escuta de Faixa a')) as HTMLAudioElement
    expect(audio.getAttribute('src')).toBe(urlNum(1))
    definirTempo(audio, 123.5)

    fireEvent.error(audio)

    await waitFor(() => expect(audio.getAttribute('src')).toBe(urlNum(2)))
    expect(api).toHaveBeenCalledTimes(2)
    for (const [caminho] of api.mock.calls as unknown as [string][]) {
      expect(caminho).toBe('/api/v1/midias/a/preview?formato=url')
    }
    // O navegador recarrega com a URL nova; ao saber a duração, a tela volta ao ponto onde parou.
    definirTempo(audio, 0)
    fireEvent.loadedMetadata(audio)
    expect(audio.currentTime).toBe(123.5)
  })

  it('perto de expiraEmUtc busca a próxima URL sem trocar o src; o error usa essa, sem nova chamada', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true })
    const api = montarApi(90_000)
    render(<PreEscuta midia={midia} aoFechar={() => {}} />)
    const audio = (await screen.findByLabelText('Pré-escuta de Faixa a')) as HTMLAudioElement
    expect(api).toHaveBeenCalledTimes(1)

    await act(async () => {
      await vi.advanceTimersByTimeAsync(61_000)
    })
    expect(api).toHaveBeenCalledTimes(2)
    // Tocando: o src não muda só porque a próxima URL chegou.
    expect(audio.getAttribute('src')).toBe(urlNum(1))

    definirTempo(audio, 40)
    fireEvent.error(audio)
    await waitFor(() => expect(audio.getAttribute('src')).toBe(urlNum(2)))
    expect(api).toHaveBeenCalledTimes(2)
  })

  it('URL antecipada que já venceu não é reaproveitada: o error busca outra', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true })
    const api = montarApi(90_000)
    render(<PreEscuta midia={midia} aoFechar={() => {}} />)
    const audio = (await screen.findByLabelText('Pré-escuta de Faixa a')) as HTMLAudioElement

    await act(async () => {
      await vi.advanceTimersByTimeAsync(61_000)
    })
    expect(api).toHaveBeenCalledTimes(2)
    // A URL guardada (sig2) vence 90 s depois de buscada; o `error` só chega depois disso.
    await act(async () => {
      await vi.advanceTimersByTimeAsync(120_000)
    })

    fireEvent.error(audio)
    await waitFor(() => expect(audio.getAttribute('src')).not.toBe(urlNum(1)))
    expect(audio.getAttribute('src')).not.toBe(urlNum(2))
    expect(api.mock.calls.length).toBeGreaterThanOrEqual(3)
  })

  it('desiste depois de dois error seguidos sem tocar, em vez de buscar URL para sempre', async () => {
    const api = montarApi()
    render(<PreEscuta midia={midia} aoFechar={() => {}} />)
    const audio = (await screen.findByLabelText('Pré-escuta de Faixa a')) as HTMLAudioElement

    fireEvent.error(audio)
    await waitFor(() => expect(audio.getAttribute('src')).toBe(urlNum(2)))
    fireEvent.error(audio)
    await waitFor(() => expect(audio.getAttribute('src')).toBe(urlNum(3)))
    fireEvent.error(audio)

    expect(await screen.findByRole('alert')).toHaveTextContent(/Não foi possível tocar/)
    expect(document.querySelector('audio')).toBeNull()
    expect(api).toHaveBeenCalledTimes(3)
  })

  it('tocar de novo zera a conta de falhas: renova quantas vezes a música precisar', async () => {
    const api = montarApi()
    render(<PreEscuta midia={midia} aoFechar={() => {}} />)
    const audio = (await screen.findByLabelText('Pré-escuta de Faixa a')) as HTMLAudioElement

    for (let n = 2; n <= 5; n++) {
      fireEvent.error(audio)
      await waitFor(() => expect(audio.getAttribute('src')).toBe(urlNum(n)))
      fireEvent.playing(audio)
    }
    expect(api).toHaveBeenCalledTimes(5)
    expect(screen.queryByRole('alert')).toBeNull()
  })

  it('a URL pré-assinada não vai a localStorage, sessionStorage nem console', async () => {
    montarApi()
    const consoles = (['log', 'info', 'warn', 'error', 'debug'] as const).map((m) =>
      vi.spyOn(console, m).mockImplementation(() => {}),
    )
    const setItem = vi.spyOn(Storage.prototype, 'setItem')
    render(<PreEscuta midia={midia} aoFechar={() => {}} />)
    const audio = (await screen.findByLabelText('Pré-escuta de Faixa a')) as HTMLAudioElement
    fireEvent.error(audio)
    await waitFor(() => expect(audio.getAttribute('src')).toBe(urlNum(2)))

    const visto = JSON.stringify([setItem.mock.calls, ...consoles.map((c) => c.mock.calls)])
    expect(visto).not.toContain('X-Amz-Signature')
    expect(visto).not.toContain('/storage/midia/')
  })
})
