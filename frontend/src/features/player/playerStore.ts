import { create } from 'zustand'
import { readStorage, writeStorage } from '../../shared/lib/safeStorage'

export const STREAM_URL = '/stream/radio.mp3'

const VOLUME_KEY = 'webradio:player:volume'
const MUTED_KEY = 'webradio:player:muted'

const RECONNECT_BASE_MS = 1000
const RECONNECT_MAX_MS = 30_000

export type PlayerStatus = 'idle' | 'connecting' | 'playing' | 'paused' | 'error'

interface PlayerState {
  status: PlayerStatus
  volume: number
  muted: boolean
  nowPlaying: string | null
  reconnectAttempt: number
  /** true quando o usuário pausou de propósito — suprime a reconexão automática. */
  userPaused: boolean
}

interface PlayerActions {
  attachAudioElement: (el: HTMLAudioElement | null) => void
  play: () => void
  pause: () => void
  toggle: () => void
  setVolume: (volume: number) => void
  toggleMute: () => void
  setNowPlaying: (title: string | null) => void
}

let audioEl: HTMLAudioElement | null = null
let reconnectTimer: ReturnType<typeof setTimeout> | null = null

function clearReconnectTimer() {
  if (reconnectTimer !== null) {
    clearTimeout(reconnectTimer)
    reconnectTimer = null
  }
}

function updateMediaSession(title: string | null, status: PlayerStatus) {
  if (!('mediaSession' in navigator)) return
  navigator.mediaSession.metadata = new MediaMetadata({
    title: title ?? 'WebRadio',
    artist: 'WebRadio',
  })
  navigator.mediaSession.playbackState = status === 'playing' ? 'playing' : 'paused'
}

export const usePlayerStore = create<PlayerState & PlayerActions>((set, get) => ({
  status: 'idle',
  volume: (() => {
    const stored = Number(readStorage(VOLUME_KEY))
    return Number.isFinite(stored) && stored >= 0 && stored <= 1 ? stored : 1
  })(),
  muted: readStorage(MUTED_KEY) === 'true',
  nowPlaying: null,
  reconnectAttempt: 0,
  userPaused: false,

  attachAudioElement: (el) => {
    audioEl = el
    if (!el) return

    const { volume, muted } = get()
    el.volume = volume
    el.muted = muted

    el.addEventListener('playing', () => {
      clearReconnectTimer()
      set({ status: 'playing', reconnectAttempt: 0 })
      updateMediaSession(get().nowPlaying, 'playing')
    })
    el.addEventListener('pause', () => {
      if (!get().userPaused) return
      set({ status: 'paused' })
      updateMediaSession(get().nowPlaying, 'paused')
    })
    el.addEventListener('waiting', () => set({ status: 'connecting' }))

    const scheduleReconnect = () => {
      if (get().userPaused) return
      set({ status: 'error' })
      clearReconnectTimer()
      const attempt = get().reconnectAttempt
      const delay = Math.min(RECONNECT_BASE_MS * 2 ** attempt, RECONNECT_MAX_MS)
      reconnectTimer = setTimeout(() => {
        set({ reconnectAttempt: attempt + 1 })
        const current = audioEl
        if (!current) return
        current.load()
        current.play().catch(() => {
          /* a próxima tentativa de reconexão cobre a falha */
        })
      }, delay)
    }

    el.addEventListener('error', scheduleReconnect)
    el.addEventListener('stalled', scheduleReconnect)
  },

  play: () => {
    if (!audioEl) return
    set({ userPaused: false, status: 'connecting' })
    audioEl.play().catch(() => set({ status: 'error' }))
  },

  pause: () => {
    if (!audioEl) return
    clearReconnectTimer()
    set({ userPaused: true })
    audioEl.pause()
  },

  toggle: () => {
    const { status, play, pause } = get()
    if (status === 'playing' || status === 'connecting') pause()
    else play()
  },

  setVolume: (volume) => {
    const clamped = Math.min(1, Math.max(0, volume))
    if (audioEl) audioEl.volume = clamped
    writeStorage(VOLUME_KEY, String(clamped))
    set({ volume: clamped })
  },

  toggleMute: () => {
    const next = !get().muted
    if (audioEl) audioEl.muted = next
    writeStorage(MUTED_KEY, String(next))
    set({ muted: next })
  },

  setNowPlaying: (title) => {
    set({ nowPlaying: title })
    updateMediaSession(title, get().status)
  },
}))
