import type { ReactNode } from 'react'
import { Navigate, useLocation } from 'react-router-dom'
import { useAuthStore } from '../../shared/api/authStore'
import type { Role } from '../../shared/api/types'

interface RequireRoleProps {
  roles: Role[]
  children: ReactNode
}

/** Ouvinte que tenta uma rota de Locutor/Admin vê 403 amigável — não é chutado pra fora. */
export function RequireRole({ roles, children }: RequireRoleProps) {
  const usuario = useAuthStore((s) => s.usuario)
  const restoring = useAuthStore((s) => s.restoring)
  const location = useLocation()

  if (restoring) return null

  if (!usuario) {
    return <Navigate to="/login" replace state={{ from: location }} />
  }

  if (!roles.includes(usuario.role)) {
    return (
      <section className="mx-auto max-w-sm text-center">
        <h1 className="font-display text-2xl font-bold">Acesso restrito</h1>
        <p className="text-text-muted mt-2 text-sm">
          Sua conta ({usuario.role}) não tem permissão para ver esta página.
        </p>
      </section>
    )
  }

  return <>{children}</>
}
