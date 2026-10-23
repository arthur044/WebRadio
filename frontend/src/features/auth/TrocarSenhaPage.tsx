import { useState, type FormEvent } from 'react'
import { Navigate, useNavigate } from 'react-router-dom'
import { useAuthStore } from '../../shared/api/authStore'
import { descreverErro, type ErroDescrito } from '../../shared/api/descreverErro'
import { apiFetch, refreshSession } from '../../shared/api/httpClient'

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
  const [erro, setErro] = useState<ErroDescrito | null>(null)
  const [enviando, setEnviando] = useState(false)

  // Com o motivo ao lado do campo, o texto genérico do 400 só repetiria o aviso. Mensagem de um campo que a tela não
  // tem (ou erro sem campo, como 429 e rede) ainda precisa aparecer em algum lugar.
  const soltas = Object.entries(erro?.campos ?? {})
    .filter(([campo]) => campo !== 'senhaAtual' && campo !== 'novaSenha')
    .map(([, mensagem]) => mensagem)
  const avisoGeral =
    soltas.length > 0
      ? soltas.join(' ')
      : erro && Object.keys(erro.campos).length === 0
        ? erro.mensagem
        : null

  // Anônimo não tem o que trocar (Guardian L5) — a página exige Bearer no POST.
  if (!usuario) {
    return <Navigate to="/login" replace />
  }

  async function onSubmit(e: FormEvent) {
    e.preventDefault()
    setErro(null)
    setEnviando(true)
    try {
      await apiFetch<void>('/auth/trocar-senha', {
        method: 'POST',
        body: { senhaAtual, novaSenha },
      })
      // Guardian H3: o 204 não devolve token novo, e o access token atual só vale
      // pra /auth/trocar-senha e /auth/me enquanto deveTrocarSenha=true (01 §2.1).
      // Sem trocar de token aqui, toda chamada seguinte (mesmo depois de "trocar a
      // senha com sucesso") recebe 403 até o token expirar sozinho. Passa pelo MESMO
      // lock do refresh comum — a família de refresh não foi revogada, só rotacionada.
      const renovou = await refreshSession(useAuthStore.getState().accessToken)
      if (!renovou) {
        // Guardian N1: a senha já foi trocada no servidor (204 recebido) mas o
        // refreshSession limpou a sessão local (clearSession, dentro dele mesmo em
        // caso de falha). Mandar pra '/' deslogado e sem aviso deixaria o usuário sem
        // saber por que perdeu a sessão logo depois de "ter sucesso".
        navigate('/login', {
          replace: true,
          state: { mensagem: 'Senha trocada. Entre novamente.' },
        })
        return
      }
      navigate('/', { replace: true })
    } catch (err) {
      // O motivo do 400 (`/erros/validacao`) vem em `problem.errors`, não no `detail` genérico: o descreverErro o
      // entrega por campo (`campos.novaSenha`, ...).
      setErro(descreverErro(err))
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
        {/* Regra e erro ficam fora do <label>: dentro dele entrariam no nome acessível do campo. */}
        <div className="flex flex-col gap-1 text-sm">
          <label htmlFor="senha-atual">Senha atual</label>
          <input
            id="senha-atual"
            type="password"
            required
            value={senhaAtual}
            onChange={(e) => setSenhaAtual(e.target.value)}
            aria-invalid={erro?.campos.senhaAtual ? true : undefined}
            aria-describedby={erro?.campos.senhaAtual ? 'erro-senha-atual' : undefined}
            className="border-border rounded-md border px-3 py-2"
            autoComplete="current-password"
          />
          {erro?.campos.senhaAtual && (
            <span id="erro-senha-atual" role="alert" className="text-on-air text-xs">
              {erro.campos.senhaAtual}
            </span>
          )}
        </div>
        <div className="flex flex-col gap-1 text-sm">
          <label htmlFor="nova-senha">Nova senha</label>
          <input
            id="nova-senha"
            type="password"
            required
            minLength={12}
            maxLength={128}
            value={novaSenha}
            onChange={(e) => setNovaSenha(e.target.value)}
            aria-invalid={erro?.campos.novaSenha ? true : undefined}
            aria-describedby={
              erro?.campos.novaSenha ? 'regra-nova-senha erro-nova-senha' : 'regra-nova-senha'
            }
            className="border-border rounded-md border px-3 py-2"
            autoComplete="new-password"
          />
          <span id="regra-nova-senha" className="text-text-muted text-xs">
            A senha precisa ter de 12 a 128 caracteres.
          </span>
          {erro?.campos.novaSenha && (
            <span id="erro-nova-senha" role="alert" className="text-on-air text-xs">
              {erro.campos.novaSenha}
            </span>
          )}
        </div>
        {avisoGeral && (
          <p role="alert" className="text-on-air text-sm">
            {avisoGeral}
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
