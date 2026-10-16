import type { APIRequestContext } from '@playwright/test'

/** Variável de ambiente obrigatória do modo stack; a mensagem diz qual falta, sem imprimir valor. */
export function exigirVariavel(nome: string): string {
  const valor = process.env[nome]
  if (!valor) {
    throw new Error(
      `Defina ${nome}: os specs de stack precisam do Admin semeado (ver ci.yml, compose-smoke).`,
    )
  }
  return valor
}

export function dadosDoAdmin() {
  return {
    email: exigirVariavel('E2E_ADMIN_EMAIL'),
    senha: exigirVariavel('E2E_ADMIN_SENHA_NOVA'),
    token: exigirVariavel('E2E_ADMIN_TOKEN'),
    id: exigirVariavel('E2E_ADMIN_ID'),
  }
}

function autorizacao() {
  return { Authorization: `Bearer ${dadosDoAdmin().token}` }
}

/** Encerra um programa (Admin pode encerrar qualquer um). Não falha o teste se já não for encerrável. */
export async function encerrarPrograma(request: APIRequestContext, id: string): Promise<void> {
  await request.post(`/api/v1/programas/${id}/encerrar`, { headers: autorizacao() })
}

/**
 * Cria um programa cuja janela começa agora (o Worker o vira AoVivo no próximo ciclo de 10 s), com o
 * próprio Admin como locutor. O seed do dev ocupa uma janela por dia: se o horário conflitar,
 * encerra quem conflita (ao vivo ou já começado) e tenta de novo.
 */
export async function criarProgramaAgora(
  request: APIRequestContext,
  titulo: string,
  minutos = 10,
): Promise<string> {
  const inicio = new Date(Math.floor(Date.now() / 1000) * 1000)
  const fim = new Date(inicio.getTime() + minutos * 60_000)
  const iso = (d: Date) => d.toISOString().replace('.000Z', 'Z')

  for (let tentativa = 0; tentativa < 3; tentativa++) {
    const resposta = await request.post('/api/v1/programas', {
      headers: autorizacao(),
      data: {
        titulo,
        descricao: null,
        locutorId: dadosDoAdmin().id,
        inicioUtc: iso(inicio),
        fimUtc: iso(fim),
      },
    })
    if (resposta.status() === 201) return (await resposta.json()).id as string
    if (resposta.status() !== 409) {
      throw new Error(`POST /programas respondeu HTTP ${resposta.status()}`)
    }
    const conflitante = (await resposta.json()).programaConflitanteId as string | undefined
    if (!conflitante) throw new Error('409 sem programaConflitanteId')
    await encerrarPrograma(request, conflitante)
  }
  throw new Error('Não consegui abrir a janela de agora: o conflito persiste.')
}
