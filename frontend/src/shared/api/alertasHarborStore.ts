import { create } from 'zustand'

/**
 * `AlertaHarborFalhas` (06 §SignalR, E2-F12). O payload nunca traz segredo, identificador nem IP; mesmo assim só os
 * campos conhecidos entram na store, para um campo a mais vindo do servidor nunca chegar à tela.
 */
export interface AlertaHarbor {
  tipo: 'credencial' | 'global'
  falhas: number
  janelaMinutos: number
  /** Só no tipo `credencial`. */
  locutorId?: string
  locutorNome?: string
  credencialAtiva?: boolean
  /** Instante do alerta; o Admin compara com a `criadaEmUtc` da credencial ativa antes de revogar. */
  ocorridoEmUtc: string
}

interface AlertasHarborState {
  /** Um aviso por chave (tipo + `locutorId`): o alerta novo da mesma chave substitui o anterior. */
  alertas: Record<string, AlertaHarbor>
  registrar: (dados: unknown) => void
  dispensar: (chave: string) => void
  limpar: () => void
}

export function chaveDoAlerta(alerta: Pick<AlertaHarbor, 'tipo' | 'locutorId'>): string {
  return `${alerta.tipo}:${alerta.locutorId ?? ''}`
}

function lerNumero(valor: unknown): number | null {
  return typeof valor === 'number' && Number.isFinite(valor) ? valor : null
}

function lerTexto(valor: unknown): string | undefined {
  return typeof valor === 'string' && valor !== '' ? valor : undefined
}

/** Devolve o alerta com os campos conhecidos, ou `null` se o payload não é um alerta válido. */
export function lerAlerta(dados: unknown): AlertaHarbor | null {
  if (typeof dados !== 'object' || dados === null) return null
  const d = dados as Record<string, unknown>
  const falhas = lerNumero(d.falhas)
  const janelaMinutos = lerNumero(d.janelaMinutos)
  const ocorridoEmUtc = lerTexto(d.ocorridoEmUtc)
  if (falhas === null || janelaMinutos === null || !ocorridoEmUtc) return null

  if (d.tipo === 'global') return { tipo: 'global', falhas, janelaMinutos, ocorridoEmUtc }
  if (d.tipo !== 'credencial') return null

  const locutorId = lerTexto(d.locutorId)
  if (!locutorId) return null
  return {
    tipo: 'credencial',
    falhas,
    janelaMinutos,
    locutorId,
    locutorNome: lerTexto(d.locutorNome),
    credencialAtiva: d.credencialAtiva === true,
    ocorridoEmUtc,
  }
}

/** Só em memória: o aviso não é histórico e não vai para `localStorage` nem `sessionStorage`. */
export const useAlertasHarbor = create<AlertasHarborState>((set) => ({
  alertas: {},
  registrar: (dados) => {
    const alerta = lerAlerta(dados)
    if (!alerta) return
    set((estado) => ({ alertas: { ...estado.alertas, [chaveDoAlerta(alerta)]: alerta } }))
  },
  dispensar: (chave) =>
    set((estado) => {
      const resto = { ...estado.alertas }
      delete resto[chave]
      return { alertas: resto }
    }),
  limpar: () => set({ alertas: {} }),
}))
