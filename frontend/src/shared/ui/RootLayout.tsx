import { NavLink, Outlet, useNavigate } from 'react-router-dom'
import { PlayerBar } from '../../features/player/PlayerBar'
import { RequireSenhaAtualizada } from '../../features/auth/RequireSenhaAtualizada'
import { useSessionRestore } from '../../features/auth/useSessionRestore'
import { useAuthStore } from '../api/authStore'
import { apiFetch } from '../api/httpClient'

const navItems = [
  { to: '/', label: 'Mural', end: true },
  { to: '/grade', label: 'Grade' },
  { to: '/pedidos', label: 'Pedir Música' },
  { to: '/estudio', label: 'Estúdio' },
]

function AuthNavItem() {
  const navigate = useNavigate()
  const usuario = useAuthStore((s) => s.usuario)
  const restoring = useAuthStore((s) => s.restoring)

  if (restoring) return null

  if (!usuario) {
    return (
      <NavLink to="/login" className="text-text-muted ml-auto text-sm font-medium">
        Entrar
      </NavLink>
    )
  }

  async function sair() {
    try {
      await apiFetch<void>('/auth/logout', { method: 'POST', skipRefreshRetry: true })
    } finally {
      useAuthStore.getState().clearSession()
      navigate('/', { replace: true })
    }
  }

  return (
    <span className="ml-auto flex items-center gap-3 text-sm">
      <span className="text-text-muted">{usuario.nome}</span>
      <button type="button" onClick={sair} className="text-accent font-medium">
        Sair
      </button>
    </span>
  )
}

export function RootLayout() {
  useSessionRestore()

  return (
    <div className="bg-surface text-text flex min-h-svh flex-col">
      <header className="border-border border-b">
        <nav className="mx-auto flex max-w-4xl items-center gap-1 px-4 py-3" aria-label="Principal">
          {navItems.map((item) => (
            <NavLink
              key={item.to}
              to={item.to}
              end={item.end}
              className={({ isActive }) =>
                `font-display rounded-md px-3 py-1.5 text-sm font-medium ${
                  isActive ? 'bg-accent text-accent-contrast' : 'text-text-muted'
                }`
              }
            >
              {item.label}
            </NavLink>
          ))}
          <AuthNavItem />
        </nav>
      </header>

      <main className="mx-auto w-full max-w-4xl flex-1 px-4 py-6">
        <RequireSenhaAtualizada>
          <Outlet />
        </RequireSenhaAtualizada>
      </main>

      {/* Fora do <Outlet/> de propósito: nunca remonta ao trocar de rota. */}
      <PlayerBar />
    </div>
  )
}
