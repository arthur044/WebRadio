import type { ReactNode } from 'react'
import { Navigate, useLocation } from 'react-router-dom'
import { useAuthStore } from '../../shared/api/authStore'

/**
 * Enquanto deveTrocarSenha=true, a única rota liberada (além de /auth/me, que não é
 * página) é /trocar-senha — qualquer outra navegação volta pra lá (02-api-rest.md §2).
 */
export function RequireSenhaAtualizada({ children }: { children: ReactNode }) {
  const usuario = useAuthStore((s) => s.usuario)
  const location = useLocation()

  if (usuario?.deveTrocarSenha && location.pathname !== '/trocar-senha') {
    return <Navigate to="/trocar-senha" replace />
  }

  return <>{children}</>
}
