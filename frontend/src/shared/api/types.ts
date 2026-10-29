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

/** `GET /usuarios` (Admin): `UsuarioDto & { ativo, criadoEmUtc, versao }` (02-api-rest.md §3). */
export interface UsuarioAdminDto extends UsuarioDto {
  ativo: boolean
  criadoEmUtc: string
  versao: string
}

/** Corpo de `POST /programas` e de `PUT /programas/{id}` (02-api-rest.md §4). */
export interface ProgramaRequest {
  titulo: string
  descricao: string | null
  locutorId: string
  inicioUtc: string
  fimUtc: string
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

/** Corpo de `POST /pedidos` (02-api-rest.md §5). */
export interface PedidoRequest {
  nomeOuvinte: string
  tituloMusica: string
  artista: string
  mensagem: string | null
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
  /** Chave no bucket; o `PUT` a reenvia para manter a imagem (02 §7). `imagemUrl` é só para exibir. */
  imagemChave: string | null
  imagemUrl: string | null
  linkDestino: string | null
  prioridade: number
  inicioExibicaoUtc: string | null
  fimExibicaoUtc: string | null
  ativo: boolean
}

/** Corpo de `POST /divulgacoes` e de `PUT /divulgacoes/{id}`; o `PUT` substitui tudo (02 §7). */
export interface DivulgacaoRequest {
  titulo: string
  mensagem: string
  imagemChave: string | null
  linkDestino: string | null
  prioridade: number
  inicioExibicaoUtc: string | null
  fimExibicaoUtc: string | null
  ativo: boolean
}

/** `POST /divulgacoes/imagens` (201). */
export interface ImagemEnviadaDto {
  chave: string
  imagemUrl: string
}

/** Transmissão ao vivo de um locutor (06-epico-2-tarefas.md §SignalR); sem `programaId`/`locutorNome` quando `noAr` é false. */
export interface AoVivoDto {
  noAr: boolean
  programaId?: string | null
  locutorNome?: string | null
}

export interface StreamInfoDto {
  url: string
  formato: string
  bitrateKbps: number
  tocandoAgora: { titulo: string | null; artista: string | null; tipo: TipoMidia | null } | null
  aoVivo: AoVivoDto
}

/**
 * Onde o source client conecta (06 §Conexão): `host` vem de `Harbor__HostPublico` e é `null` sem ele; `porta` de
 * `Harbor__PortaPublica` (padrão 8005); `mount` é fixo, `/live`. Não é segredo; só o dono a recebe.
 */
export interface ConexaoDeTransmissaoDto {
  host: string | null
  porta: number
  mount: string
}

/** `GET /locutores/me/credencial-transmissao` (200; o 204 não tem corpo): nunca traz o segredo. */
export interface CredencialDto {
  identificador: string
  criadaEmUtc: string
  ultimoUsoEmUtc: string | null
  conexao: ConexaoDeTransmissaoDto
}

/** `POST /locutores/me/credencial-transmissao`: a ÚNICA resposta com o segredo. Não vai para o cache de queries. */
export interface CredencialGeradaDto {
  identificador: string
  segredo: string
  criadaEmUtc: string
  conexao: ConexaoDeTransmissaoDto
}

/** `GET /credenciais-transmissao` (Admin). */
export interface CredencialAtivaDto extends Omit<CredencialDto, 'conexao'> {
  usuarioId: string
  usuarioNome: string
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
