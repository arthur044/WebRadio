import { apiFetch } from '../../src/shared/api/httpClient'
import { useAuthStore } from '../../src/shared/api/authStore'

declare global {
  interface Window {
    __probe: (expiredToken: string) => Promise<void>
  }
}

window.__probe = async (expiredToken: string) => {
  useAuthStore.getState().setSession(expiredToken, {
    id: '1',
    nome: 'Ouvinte Teste',
    email: 't@t.com',
    role: 'Ouvinte',
    deveTrocarSenha: false,
  })

  const resultEl = document.getElementById('result')!
  try {
    await apiFetch('/probe/protegido')
    resultEl.textContent = `ok:${useAuthStore.getState().accessToken}`
  } catch (err) {
    resultEl.textContent = `erro:${err instanceof Error ? err.message : String(err)}`
  }
}

document.getElementById('ready')!.textContent = 'ready'
