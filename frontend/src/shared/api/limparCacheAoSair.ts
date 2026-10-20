import type { QueryClient } from '@tanstack/react-query'
import { useAuthStore } from './authStore'

/** Chaves cujo conteúdo é público (mesma resposta para anônimo e logado): sobrevivem à troca de sessão. */
const PUBLICAS = new Set(['divulgacoes-ativas', 'programa-ao-vivo', 'stream-info'])

function ehPublica(chave: readonly unknown[]): boolean {
  if (typeof chave[0] === 'string' && PUBLICAS.has(chave[0])) return true
  return chave[0] === 'grade-semana' && chave[1] === 'publica'
}

/**
 * Descarta do cache tudo que não é público (grade com cancelados, locutores, pedidos...). Sem isto, o que o Admin viu
 * continua em memória depois do logout, para quem entrar em seguida na mesma aba (Sentinel L1 do #42).
 * Nega por padrão: uma chave nova de tela privada já nasce limpa, sem precisar entrar numa lista.
 */
export function limparCachePrivado(queryClient: QueryClient): void {
  queryClient.removeQueries({ predicate: (query) => !ehPublica(query.queryKey) })
}

/**
 * Limpa o cache privado quando o usuário da sessão sai, muda ou muda de papel (logout, expiração, outro login,
 * rebaixamento). O papel conta: um Admin rebaixado mantém o `id`, e o cache do papel antigo (credenciais de todos,
 * grade com cancelados) apareceria de imediato antes de a busca refazer (Sentinel #2 do #57).
 */
export function ligarLimpezaDeCache(queryClient: QueryClient): () => void {
  return useAuthStore.subscribe((estado, anterior) => {
    const antes = anterior.usuario
    if (antes && (antes.id !== estado.usuario?.id || antes.role !== estado.usuario?.role)) {
      limparCachePrivado(queryClient)
      queryClient.getMutationCache().clear()
    }
  })
}
