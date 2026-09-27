import { NavLink, Outlet } from 'react-router-dom'
import { PlayerBar } from '../../features/player/PlayerBar'

const navItems = [
  { to: '/', label: 'Mural', end: true },
  { to: '/grade', label: 'Grade' },
  { to: '/pedidos', label: 'Pedir Música' },
  { to: '/estudio', label: 'Estúdio' },
]

export function RootLayout() {
  return (
    <div className="bg-surface text-text flex min-h-svh flex-col">
      <header className="border-border border-b">
        <nav className="mx-auto flex max-w-4xl gap-1 px-4 py-3" aria-label="Principal">
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
        </nav>
      </header>

      <main className="mx-auto w-full max-w-4xl flex-1 px-4 py-6">
        <Outlet />
      </main>

      {/* Fora do <Outlet/> de propósito: nunca remonta ao trocar de rota. */}
      <PlayerBar />
    </div>
  )
}
