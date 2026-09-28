import { useState, type FormEvent } from 'react'
import { useNavigate } from 'react-router-dom'
import { useAuthStore } from '../../shared/api/authStore'
import { apiFetch } from '../../shared/api/httpClient'
import type { LoginResponse } from '../../shared/api/types'

export function LoginPage() {
  const navigate = useNavigate()
  const [email, setEmail] = useState('')
  const [senha, setSenha] = useState('')
  const [erro, setErro] = useState<string | null>(null)
  const [enviando, setEnviando] = useState(false)

  async function onSubmit(e: FormEvent) {
    e.preventDefault()
    setErro(null)
    setEnviando(true)
    try {
      const data = await apiFetch<LoginResponse>('/auth/login', {
        method: 'POST',
        skipAuth: true,
        body: { email, senha },
      })
      useAuthStore.getState().setSession(data.accessToken, data.usuario)
      navigate(data.deveTrocarSenha ? '/trocar-senha' : '/', { replace: true })
    } catch {
      // Mensagem sempre genérica — nunca revela se o e-mail existe, conta bloqueada
      // ou senha errada (D21/S-A12: qualquer falha de login vira 401 genérico).
      setErro('E-mail ou senha inválidos')
    } finally {
      setEnviando(false)
    }
  }

  return (
    <section className="mx-auto max-w-sm">
      <h1 className="font-display text-2xl font-bold">Entrar</h1>
      <form onSubmit={onSubmit} className="mt-4 flex flex-col gap-3">
        <label className="flex flex-col gap-1 text-sm">
          E-mail
          <input
            type="email"
            required
            value={email}
            onChange={(e) => setEmail(e.target.value)}
            className="border-border rounded-md border px-3 py-2"
            autoComplete="username"
          />
        </label>
        <label className="flex flex-col gap-1 text-sm">
          Senha
          <input
            type="password"
            required
            value={senha}
            onChange={(e) => setSenha(e.target.value)}
            className="border-border rounded-md border px-3 py-2"
            autoComplete="current-password"
          />
        </label>
        {erro && (
          <p role="alert" className="text-on-air text-sm">
            {erro}
          </p>
        )}
        <button
          type="submit"
          disabled={enviando}
          className="bg-accent text-accent-contrast rounded-md px-3 py-2 disabled:opacity-50"
        >
          {enviando ? 'Entrando…' : 'Entrar'}
        </button>
      </form>
    </section>
  )
}
