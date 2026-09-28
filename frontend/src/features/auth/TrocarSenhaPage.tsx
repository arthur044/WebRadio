import { useState, type FormEvent } from 'react'
import { useNavigate } from 'react-router-dom'
import { useAuthStore } from '../../shared/api/authStore'
import { apiFetch, ApiError } from '../../shared/api/httpClient'

/**
 * Tela obrigatória quando deveTrocarSenha=true (Admin semeado, S-B07). É a única
 * rota liberada nesse estado além de /auth/me — o RequireSenhaAtualizada garante
 * isso nas outras rotas, redirecionando de volta pra cá.
 */
export function TrocarSenhaPage() {
  const navigate = useNavigate()
  const usuario = useAuthStore((s) => s.usuario)
  const [senhaAtual, setSenhaAtual] = useState('')
  const [novaSenha, setNovaSenha] = useState('')
  const [erro, setErro] = useState<string | null>(null)
  const [enviando, setEnviando] = useState(false)

  async function onSubmit(e: FormEvent) {
    e.preventDefault()
    setErro(null)
    setEnviando(true)
    try {
      await apiFetch<void>('/auth/trocar-senha', {
        method: 'POST',
        body: { senhaAtual, novaSenha },
      })
      const token = useAuthStore.getState().accessToken
      if (usuario && token) {
        useAuthStore.getState().setSession(token, { ...usuario, deveTrocarSenha: false })
      }
      navigate('/', { replace: true })
    } catch (err) {
      setErro(
        err instanceof ApiError
          ? err.problem.detail || err.message
          : 'Não foi possível trocar a senha',
      )
    } finally {
      setEnviando(false)
    }
  }

  return (
    <section className="mx-auto max-w-sm">
      <h1 className="font-display text-2xl font-bold">Troca de senha obrigatória</h1>
      <p className="text-text-muted mt-2 text-sm">
        Por segurança, você precisa definir uma nova senha antes de continuar.
      </p>
      <form onSubmit={onSubmit} className="mt-4 flex flex-col gap-3">
        <label className="flex flex-col gap-1 text-sm">
          Senha atual
          <input
            type="password"
            required
            value={senhaAtual}
            onChange={(e) => setSenhaAtual(e.target.value)}
            className="border-border rounded-md border px-3 py-2"
            autoComplete="current-password"
          />
        </label>
        <label className="flex flex-col gap-1 text-sm">
          Nova senha (12 a 128 caracteres)
          <input
            type="password"
            required
            minLength={12}
            maxLength={128}
            value={novaSenha}
            onChange={(e) => setNovaSenha(e.target.value)}
            className="border-border rounded-md border px-3 py-2"
            autoComplete="new-password"
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
          {enviando ? 'Salvando…' : 'Trocar senha'}
        </button>
      </form>
    </section>
  )
}
