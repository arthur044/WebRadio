import { readStorage, writeStorage } from '../lib/safeStorage'

const KEY = 'webradio:listenerId'

let cached: string | null = null

/** UUID persistido do dispositivo (Fluxo B / hub SignalR), gerado uma vez por navegador. */
export function getListenerId(): string {
  if (cached) return cached
  const stored = readStorage(KEY)
  if (stored) {
    cached = stored
    return stored
  }
  const fresh = crypto.randomUUID()
  writeStorage(KEY, fresh)
  cached = fresh
  return fresh
}
