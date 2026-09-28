/**
 * Tipos hand-written a partir de docs/specs/02-api-rest.md v1.1. Substituir por
 * `openapi-typescript` contra /openapi/v1.json assim que o Forge publicar (E1-P04,
 * dependência F06) — a forma dos tipos foi feita pra ficar parecida com o que o
 * gerador produziria, pra a troca não mexer nos call-sites.
 */

export type Role = 'Admin' | 'Locutor' | 'Ouvinte'

export interface UsuarioDto {
  id: string
  nome: string
  email: string
  role: Role
  deveTrocarSenha: boolean
}

export interface LoginResponse {
  accessToken: string
  expiraEmUtc: string
  deveTrocarSenha: boolean
  usuario: UsuarioDto
}

export type ProgramaStatus = 'Agendado' | 'AoVivo' | 'Finalizado' | 'Cancelado'

export interface ProgramaDto {
  id: string
  titulo: string
  descricao: string | null
  locutor: { id: string; nome: string }
  inicioUtc: string
  fimUtc: string
  status: ProgramaStatus
}

export type PedidoStatus = 'Pendente' | 'Aprovado' | 'Rejeitado' | 'Tocado' | 'Expirado'

export interface PedidoDto {
  id: string
  nomeOuvinte: string
  tituloMusica: string
  artista: string
  mensagem: string | null
  status: PedidoStatus
  criadoEmUtc: string
  moderadoEmUtc: string | null
  motivoRejeicao: string | null
  tocadoEmUtc: string | null
  programaId: string | null
}

export type TipoMidia = 'Musica' | 'Vinheta' | 'Comercial'
export type StatusSanitizacao =
  'AguardandoUpload' | 'Pendente' | 'EmAnalise' | 'Aprovado' | 'Rejeitado' | 'Quarentena'

export interface MidiaDto {
  id: string
  nomeOriginal: string
  titulo: string | null
  artista: string | null
  tipoMidia: TipoMidia
  mimeType: string | null
  tamanhoBytes: number
  duracaoSegundos: number | null
  statusSanitizacao: StatusSanitizacao
  motivoRejeicao: string | null
  dataUploadUtc: string
  ativo: boolean
}

export interface DivulgacaoDto {
  id: string
  titulo: string
  mensagem: string
  imagemUrl: string | null
  linkDestino: string | null
  prioridade: number
  inicioExibicaoUtc: string | null
  fimExibicaoUtc: string | null
  ativo: boolean
}

export interface StreamInfoDto {
  url: string
  formato: string
  bitrateKbps: number
  tocandoAgora: { titulo: string; artista: string | null; tipo: TipoMidia } | null
}

export interface PaginaResposta<T> {
  itens: T[]
  total: number
  pagina: number
  tamanho: number
}

/** RFC 9457. `type` é um path relativo (`/erros/...`), ver 01-dominio.md §3. */
export interface ProblemDetails {
  type: string
  title: string
  status: number
  detail?: string
  errors?: Record<string, string[]>
  retryAfterSeconds?: number
  programaConflitanteId?: string
}
