import { create } from 'zustand'
import type { UsuarioDto } from './types'

interface AuthState {
  /** Só em memória — nunca localStorage/sessionStorage (spec: access token só em memória). */
  accessToken: string | null
  usuario: UsuarioDto | null
  /** true até a tentativa de restaurar sessão (POST /auth/refresh) no boot terminar. */
  restoring: boolean
  setSession: (accessToken: string, usuario: UsuarioDto) => void
  clearSession: () => void
  setRestoring: (restoring: boolean) => void
}

export const useAuthStore = create<AuthState>((set) => ({
  accessToken: null,
  usuario: null,
  restoring: true,
  setSession: (accessToken, usuario) => set({ accessToken, usuario }),
  clearSession: () => set({ accessToken: null, usuario: null }),
  setRestoring: (restoring) => set({ restoring }),
}))
