import { useQuery } from '@tanstack/react-query'
import { apiFetch } from '../../shared/api/httpClient'
import type { PaginaResposta, Role, UsuarioAdminDto } from '../../shared/api/types'

export interface LocutorOpcao {
  id: string
  nome: string
  role: Role
}

const PAPEIS_QUE_APRESENTAM: Role[] = ['Locutor', 'Admin']

const TAMANHO_DA_PAGINA = 100

/** Lê todas as páginas de um papel: parar na primeira cortaria em silêncio quem passa de 100. */
async function listarPorPapel(role: Role): Promise<UsuarioAdminDto[]> {
  const todos: UsuarioAdminDto[] = []
  for (let pagina = 1; ; pagina++) {
    const resposta = await apiFetch<PaginaResposta<UsuarioAdminDto>>(
      `/usuarios?role=${role}&pagina=${pagina}&tamanho=${TAMANHO_DA_PAGINA}`,
    )
    todos.push(...resposta.itens)
    if (resposta.itens.length === 0 || todos.length >= resposta.total) return todos
  }
}

/**
 * Quem pode apresentar um programa: usuário ATIVO com papel Locutor ou Admin (01-dominio.md §2.3).
 * `GET /usuarios` filtra por um papel só, então são duas listagens (Admin-only), cada uma paginada até o `total`.
 */
export function useLocutores() {
  return useQuery({
    queryKey: ['locutores'],
    staleTime: 60_000,
    queryFn: async (): Promise<LocutorOpcao[]> => {
      const listas = await Promise.all(PAPEIS_QUE_APRESENTAM.map(listarPorPapel))
      return listas
        .flat()
        .filter((usuario) => usuario.ativo)
        .map(({ id, nome, role }) => ({ id, nome, role }))
        .sort((a, b) => a.nome.localeCompare(b.nome, 'pt-BR'))
    },
  })
}
