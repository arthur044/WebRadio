import { useQuery } from '@tanstack/react-query'
import { apiFetch } from '../../shared/api/httpClient'
import type { CredencialAtivaDto, CredencialDto } from '../../shared/api/types'

/** A credencial ativa do próprio locutor (sem o segredo). 204 = ainda não gerou. Chave privada: sai do cache no logout. */
export function useMinhaCredencial() {
  return useQuery({
    queryKey: ['credencial-transmissao'],
    // O TanStack Query não aceita `undefined` como dado: o 204 vira `null`.
    queryFn: async () =>
      (await apiFetch<CredencialDto | undefined>('/locutores/me/credencial-transmissao')) ?? null,
  })
}

/** Admin: as credenciais ativas de todos os locutores. */
export function useCredenciaisAtivas(habilitado: boolean) {
  return useQuery({
    queryKey: ['credenciais-transmissao'],
    queryFn: () => apiFetch<CredencialAtivaDto[]>('/credenciais-transmissao'),
    enabled: habilitado,
  })
}
