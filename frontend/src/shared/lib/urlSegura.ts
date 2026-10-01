interface OpcoesUrlSegura {
  /** Protocolos aceitos (com os dois pontos, ex. `'https:'`). */
  permitidos: readonly string[]
  /** Aceita caminho same-origin (`/uploads/a.png`). `//host` (protocol-relative) continua recusado. */
  aceitaCaminhoRelativo?: boolean
}

/**
 * Devolve a URL normalizada só se o esquema estiver em `permitidos`; senão `null` (o chamador
 * renderiza sem link/imagem). `javascript:`/`data:`/`vbscript:` vindos da API virariam XSS em
 * `href`/`src`; o front não confia na validação do backend nem na CSP (defesa em profundidade).
 */
export function urlSegura(
  valor: string | null | undefined,
  { permitidos, aceitaCaminhoRelativo = false }: OpcoesUrlSegura,
): string | null {
  if (!valor) return null
  const texto = valor.trim()

  if (aceitaCaminhoRelativo && /^\/(?![/\\])/.test(texto)) {
    try {
      const url = new URL(texto, window.location.origin)
      return url.origin === window.location.origin ? url.href : null
    } catch {
      return null
    }
  }

  try {
    const url = new URL(texto) // sem base: relativo/`//host` lançam e viram null
    return permitidos.includes(url.protocol) ? url.href : null
  } catch {
    return null
  }
}

/** `linkDestino`: só https absoluto. */
export const linkSeguro = (valor: string | null | undefined) =>
  urlSegura(valor, { permitidos: ['https:'] })

/** `imagemUrl`: https absoluto ou caminho same-origin; nunca `data:`. */
export const imagemSegura = (valor: string | null | undefined) =>
  urlSegura(valor, { permitidos: ['https:'], aceitaCaminhoRelativo: true })
