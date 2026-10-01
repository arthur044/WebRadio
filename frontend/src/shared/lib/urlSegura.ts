const PROTOCOLOS_PERMITIDOS = new Set(['http:', 'https:'])

/**
 * Devolve a URL normalizada só se for http/https; senão `null` (o chamador renderiza sem
 * link/imagem). `javascript:`/`data:`/`vbscript:` vindos da API virariam XSS em `href`/`src`.
 * URLs relativas são resolvidas contra a origem atual (continuam same-origin, http/https).
 */
export function urlHttpSegura(valor: string | null | undefined): string | null {
  if (!valor) return null
  try {
    const url = new URL(valor.trim(), window.location.origin)
    return PROTOCOLOS_PERMITIDOS.has(url.protocol) ? url.href : null
  } catch {
    return null
  }
}
