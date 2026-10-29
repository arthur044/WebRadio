import { ApiError } from './httpClient'

export interface ErroDescrito {
  /** Texto para o usuário. Nunca o `detail` cru do servidor (pode citar nomes internos). */
  mensagem: string
  /** Mensagens por campo do formulário, com a chave em camelCase (`titulo`, `inicioUtc`, ...). */
  campos: Record<string, string>
  /** Id do programa que ocupa o horário (409 `/erros/conflito-grade`). */
  conflitoComId: string | null
  /** O que a tela mostra está velho: vale recarregar antes de tentar de novo. */
  recarregar: boolean
  /** Segundos até o 429 deixar tentar de novo (`retryAfterSeconds` do ProblemDetails); `null` se não veio. */
  esperarSegundos: number | null
}

/** Teto da espera mostrada: o servidor manda no máximo 300 s, e um valor absurdo não pode travar a tela. */
const ESPERA_MAXIMA_S = 3600

function segundosDeEspera(valor: unknown): number | null {
  if (typeof valor !== 'number' || !Number.isFinite(valor) || valor <= 0) return null
  return Math.min(Math.ceil(valor), ESPERA_MAXIMA_S)
}

function descrito(mensagem: string, extras: Partial<ErroDescrito> = {}): ErroDescrito {
  return {
    mensagem,
    campos: {},
    conflitoComId: null,
    recarregar: false,
    esperarSegundos: null,
    ...extras,
  }
}

function versaoDesatualizada(): ErroDescrito {
  return descrito(
    'Isto foi alterado por outra pessoa. Os dados foram recarregados: confira e salve de novo.',
    { recarregar: true },
  )
}

/** Sem o ETag do recurso o `PUT` nem sai (evita o 428 e a sobrescrita). O texto serve a qualquer tela. */
export function semVersao(): ErroDescrito {
  return descrito('Não foi possível confirmar a versão. Recarregue e tente de novo.', {
    recarregar: true,
  })
}

function camel(chave: string): string {
  return chave.charAt(0).toLowerCase() + chave.slice(1)
}

/**
 * Traduz um erro da API (RFC 9457, 01-dominio.md §3) em mensagem de tela. Decide por `type` quando o
 * contrato o define e cai para o status HTTP nos demais casos.
 */
export function descreverErro(err: unknown): ErroDescrito {
  if (!(err instanceof ApiError)) {
    if (err instanceof TypeError) {
      return descrito('Sem conexão com o servidor. Confira sua internet e tente novamente.')
    }
    return descrito('Não foi possível concluir a ação. Tente novamente.')
  }

  const { problem, status } = err

  switch (problem.type) {
    case '/erros/conflito-grade':
      return descrito('Esse horário já está ocupado por outro programa.', {
        conflitoComId: problem.programaConflitanteId ?? null,
      })
    case '/erros/versao-desatualizada':
      return versaoDesatualizada()
    case '/erros/transicao-invalida':
      return descrito(
        'O programa mudou de estado (já começou, terminou ou foi cancelado) e não aceita mais esta ação.',
        { recarregar: true },
      )
    case '/erros/pedido-ja-moderado':
      return descrito('Outro locutor já moderou este pedido.', { recarregar: true })
    case '/erros/imagem-em-uso':
      return descrito('Esta imagem já está em uso por outra divulgação. Escolha outra.')
    case '/erros/credenciais':
      return descrito('Senha incorreta.', { campos: { senhaAtual: 'Senha incorreta.' } })
    case '/erros/conflito-credencial':
      return descrito(
        'Outra geração aconteceu ao mesmo tempo. Confira a credencial atual e, se precisar, gere de novo.',
        { recarregar: true },
      )
    case '/erros/validacao': {
      const campos: Record<string, string> = {}
      for (const [chave, mensagens] of Object.entries(problem.errors ?? {})) {
        campos[camel(chave)] = mensagens.join(' ')
      }
      // Sem `errors` não há campo destacado: o texto não pode mandar procurar um.
      return descrito(
        Object.keys(campos).length > 0
          ? 'Corrija os campos destacados e tente de novo.'
          : 'Os dados enviados não foram aceitos. Confira o que preencheu e tente de novo.',
        { campos },
      )
    }
  }

  switch (status) {
    case 401:
      return descrito('Sua sessão expirou. Entre de novo.')
    case 403:
      return descrito('Você não tem permissão para esta ação.')
    case 404:
      return descrito('Não encontrado. Pode ter sido removido.', { recarregar: true })
    case 412:
      return versaoDesatualizada()
    case 428:
      return semVersao()
    case 413:
      return descrito('O arquivo é maior do que o limite aceito.')
    case 429:
      return descrito('Muitas tentativas em pouco tempo. Aguarde um instante e tente de novo.', {
        esperarSegundos: segundosDeEspera(problem.retryAfterSeconds),
      })
    case 503:
      return descrito('O servidor está ocupado. Tente de novo em instantes.')
    default:
      return descrito('Não foi possível concluir a ação. Tente novamente.')
  }
}
