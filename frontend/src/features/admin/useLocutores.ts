import { useQuery } from '@tanstack/react-query'
import { apiFetch } from '../../shared/api/httpClient'
import type { PaginaResposta, Role, UsuarioAdminDto } from '../../shared/api/types'

export interface LocutorOpcao {
  id: string
  nome: string
  role: Role
}

const PAPEIS_QUE_APRESENTAM: Role[] = ['Locutor', 'Admin']

/**
 * Quem pode apresentar um programa: usuário ATIVO com papel Locutor ou Admin (01-dominio.md §2.3).
 * `GET /usuarios` filtra por um papel só, então são duas chamadas (Admin-only, 100 por página).
 */
export function useLocutores() {
  return useQuery({
    queryKey: ['locutores'],
    staleTime: 60_000,
    queryFn: async (): Promise<LocutorOpcao[]> => {
      const paginas = await Promise.all(
        PAPEIS_QUE_APRESENTAM.map((role) =>
          apiFetch<PaginaResposta<UsuarioAdminDto>>(`/usuarios?role=${role}&tamanho=100`),
        ),
      )
      return paginas
        .flatMap((pagina) => pagina.itens)
        .filter((usuario) => usuario.ativo)
        .map(({ id, nome, role }) => ({ id, nome, role }))
        .sort((a, b) => a.nome.localeCompare(b.nome, 'pt-BR'))
    },
  })
}
