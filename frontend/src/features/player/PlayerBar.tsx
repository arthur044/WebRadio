import { useEffect, useRef } from 'react'
import { STREAM_URL, usePlayerStore } from './playerStore'

export function PlayerBar() {
  const audioRef = useRef<HTMLAudioElement>(null)
  const attachAudioElement = usePlayerStore((s) => s.attachAudioElement)
  const status = usePlayerStore((s) => s.status)
  const volume = usePlayerStore((s) => s.volume)
  const muted = usePlayerStore((s) => s.muted)
  const nowPlaying = usePlayerStore((s) => s.nowPlaying)
  const toggle = usePlayerStore((s) => s.toggle)
  const setVolume = usePlayerStore((s) => s.setVolume)
  const toggleMute = usePlayerStore((s) => s.toggleMute)

  useEffect(() => {
    attachAudioElement(audioRef.current)
    return () => attachAudioElement(null)
  }, [attachAudioElement])

  const isLive = status === 'playing'

  return (
    <div
      data-testid="player-bar"
      className="border-border bg-surface-raised text-text flex items-center gap-4 border-t px-4 py-3"
    >
      <audio ref={audioRef} src={STREAM_URL} preload="none" data-testid="radio-audio" />

      <button
        type="button"
        onClick={toggle}
        aria-label={isLive ? 'Pausar' : 'Tocar'}
        data-testid="player-toggle"
        className="bg-accent text-accent-contrast flex h-10 w-10 shrink-0 items-center justify-center rounded-full"
      >
        {isLive ? '❚❚' : '▶'}
      </button>

      <div className="flex min-w-0 flex-1 items-center gap-2">
        <span
          aria-hidden="true"
          className={`bg-on-air h-2.5 w-2.5 shrink-0 rounded-full ${isLive ? 'animate-pulse' : 'opacity-30'}`}
        />
        <span className="font-display text-on-air text-xs font-bold tracking-wide uppercase">
          {isLive ? 'Ao vivo' : status === 'connecting' ? 'Conectando' : 'Fora do ar'}
        </span>
        <span className="text-text-muted truncate text-sm">{nowPlaying ?? 'WebRadio'}</span>
      </div>

      <button
        type="button"
        onClick={toggleMute}
        aria-label={muted ? 'Reativar som' : 'Mudo'}
        data-testid="player-mute"
        className="text-text-muted shrink-0"
      >
        {muted ? '🔇' : '🔊'}
      </button>
      <input
        type="range"
        min={0}
        max={1}
        step={0.01}
        value={volume}
        onChange={(e) => setVolume(Number(e.target.value))}
        aria-label="Volume"
        data-testid="player-volume"
        className="w-24 shrink-0"
      />
    </div>
  )
}
