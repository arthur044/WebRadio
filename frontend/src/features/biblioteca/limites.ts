import type { TipoMidia } from '../../shared/api/types'

const MB = 1024 * 1024

/** Tamanho máximo do arquivo por tipo (01-dominio.md §2.5, `Midia:Limites`). O servidor confere de novo (400). */
export const TAMANHO_MAXIMO_BYTES: Record<TipoMidia, number> = {
  Vinheta: 10 * MB,
  Comercial: 20 * MB,
  Musica: 250 * MB,
}

export const NOMES_DO_TIPO: Record<TipoMidia, string> = {
  Vinheta: 'Vinheta',
  Comercial: 'Comercial',
  Musica: 'Música',
}

export const TIPOS: readonly TipoMidia[] = ['Musica', 'Vinheta', 'Comercial']

/** `titulo` e `artista` pelo `TextoSeguro` (02 §6): até 200 unidades UTF-16. */
export const TEXTO_MAXIMO = 200

/** Os únicos `mimeTypeDeclarado` que `POST /midias/uploads` aceita (02 §6). */
const MIMES_ACEITOS = new Set(['audio/mpeg', 'audio/ogg', 'audio/wav', 'audio/x-wav', 'audio/flac'])

const MIME_POR_EXTENSAO: Record<string, string> = {
  mp3: 'audio/mpeg',
  ogg: 'audio/ogg',
  wav: 'audio/wav',
  flac: 'audio/flac',
}

/**
 * O MIME a declarar: o do navegador quando está na lista; senão o da extensão (alguns sistemas deixam `file.type`
 * vazio). `null` = formato que o servidor não aceita. É só a declaração: quem decide o formato real é o servidor, pelos
 * magic bytes (S-C01).
 */
export function mimeDeclarado(arquivo: Pick<File, 'name' | 'type'>): string | null {
  const tipo = arquivo.type.toLowerCase()
  if (MIMES_ACEITOS.has(tipo)) return tipo
  const ponto = arquivo.name.lastIndexOf('.')
  if (ponto < 0) return null
  return MIME_POR_EXTENSAO[arquivo.name.slice(ponto + 1).toLowerCase()] ?? null
}

export function formatarTamanho(bytes: number): string {
  if (bytes >= MB) return `${(bytes / MB).toFixed(bytes >= 10 * MB ? 0 : 1).replace('.', ',')} MB`
  return `${Math.max(1, Math.round(bytes / 1024))} KB`
}

/** `95` -> `1:35`. */
export function formatarDuracao(segundos: number): string {
  const total = Math.round(segundos)
  return `${Math.floor(total / 60)}:${String(total % 60).padStart(2, '0')}`
}

/** Mensagem de recusa antes de pedir a policy, ou `null` se o arquivo passa na checagem local. */
export function recusaLocal(
  arquivo: Pick<File, 'name' | 'type' | 'size'>,
  tipo: TipoMidia,
): string | null {
  if (arquivo.size <= 0) return 'O arquivo está vazio.'
  if (mimeDeclarado(arquivo) === null) {
    return 'Formato não aceito. Envie MP3, OGG, WAV ou FLAC.'
  }
  const maximo = TAMANHO_MAXIMO_BYTES[tipo]
  if (arquivo.size > maximo) {
    return `O arquivo tem ${formatarTamanho(arquivo.size)}; ${NOMES_DO_TIPO[tipo].toLowerCase()} aceita até ${formatarTamanho(maximo)}.`
  }
  return null
}
