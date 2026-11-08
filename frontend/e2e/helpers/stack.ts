import { execFileSync } from 'node:child_process'
import { resolve } from 'node:path'
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

/**
 * Locutor fictício do seed de dev (`DevDataSeeder`, e-mail fixo; a senha vem do arquivo de segredo `seed_dev_senha`, que o
 * CI repassa em `E2E_LOCUTOR_SENHA`). Exercita o papel `Locutor` de verdade: policy `PodeModerar`, grupo `locutores` do hub
 * e o link "Estúdio" do menu.
 */
export function dadosDoLocutor() {
  return {
    email: process.env.E2E_LOCUTOR_EMAIL ?? 'locutor1@example.com',
    senha: exigirVariavel('E2E_LOCUTOR_SENHA'),
  }
}

function autorizacao() {
  return { Authorization: `Bearer ${dadosDoAdmin().token}` }
}

/** Encerra um programa (Admin pode encerrar qualquer um). Não falha o teste se já não for encerrável. */
export async function encerrarPrograma(request: APIRequestContext, id: string): Promise<void> {
  await request.post(`/api/v1/programas/${id}/encerrar`, { headers: autorizacao() })
}

/** Tira o programa da grade: encerra se já começou; se o encerrar recusar (Agendado futuro), cancela. */
async function liberarJanela(request: APIRequestContext, id: string): Promise<void> {
  const encerrado = await request.post(`/api/v1/programas/${id}/encerrar`, {
    headers: autorizacao(),
  })
  if (encerrado.ok()) return
  const cancelado = await request.post(`/api/v1/programas/${id}/cancelar`, {
    headers: autorizacao(),
  })
  if (!cancelado.ok()) {
    throw new Error(
      `Não consegui liberar a janela: encerrar HTTP ${encerrado.status()}, cancelar HTTP ${cancelado.status()}`,
    )
  }
}

/**
 * Cria um programa cuja janela começa agora (o Worker o vira AoVivo no próximo ciclo de 10 s), com o
 * próprio Admin como locutor. O seed do dev ocupa uma janela por dia (a primeira começa amanhã às 12:00 UTC): se o
 * horário conflitar, libera a janela do conflitante e tenta de novo. `Encerrar` só vale para quem já começou
 * (ao vivo ou com início no passado); um `Agendado` que ainda não começou dá 409 no encerrar e é cancelado.
 */
export async function criarProgramaAgora(
  request: APIRequestContext,
  titulo: string,
  minutos = 10,
): Promise<string> {
  const inicio = new Date(Math.floor(Date.now() / 1000) * 1000)
  return criarProgramaEm(request, titulo, inicio, minutos)
}

/**
 * Cria um programa `Agendado` que só começa daqui a `minutosAteInicio` (o Worker não o toca antes disso), para os
 * specs que cancelam. Mesma regra de conflito do `criarProgramaAgora`. Devolve também o início, para o spec abrir a
 * semana certa da grade.
 */
export async function criarProgramaAgendado(
  request: APIRequestContext,
  titulo: string,
  minutosAteInicio = 120,
  minutos = 10,
): Promise<{ id: string; inicio: Date }> {
  const inicio = new Date(Math.floor(Date.now() / 1000) * 1000 + minutosAteInicio * 60_000)
  return { id: await criarProgramaEm(request, titulo, inicio, minutos), inicio }
}

/** Cancela um programa `Agendado` (Admin). Não falha o teste se já não for cancelável. */
export async function cancelarPrograma(request: APIRequestContext, id: string): Promise<void> {
  await request.post(`/api/v1/programas/${id}/cancelar`, { headers: autorizacao() })
}

async function criarProgramaEm(
  request: APIRequestContext,
  titulo: string,
  inicio: Date,
  minutos: number,
): Promise<string> {
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
    await liberarJanela(request, conflitante)
  }
  throw new Error('Não consegui abrir a janela de agora: o conflito persiste.')
}

/**
 * Apaga as chaves de throttle de pedidos (`wr:throttle:pedido:*`, prefixo do `RedisRequestThrottle`) no Redis do stack. O
 * orçamento é de 1 pedido a cada 5 min por dispositivo e 3 por IP: sem isto, os `retries: 2` do CI e o smoke que roda
 * antes estourariam o limite por IP (E3-F08). A senha do Redis é lida DENTRO do contêiner (`/run/secrets`), nunca em
 * argv nem em log do host. Só no modo stack; o E2E roda com `frontend/` como diretório, e o compose fica na raiz.
 */
export function limparThrottleDePedidos(): void {
  if (!process.env.E2E_BASE_URL) return
  const raiz = resolve(process.cwd(), '..')
  const script =
    'export REDISCLI_AUTH="$(cat /run/secrets/redis_password)"; ' +
    'redis-cli --scan --pattern "wr:throttle:pedido:*" | xargs -r redis-cli DEL'
  execFileSync(
    'docker',
    [
      'compose',
      '-f',
      resolve(raiz, 'docker-compose.yml'),
      '-f',
      resolve(raiz, 'docker-compose.dev.yml'),
      'exec',
      '-T',
      'redis',
      'sh',
      '-c',
      script,
    ],
    { stdio: ['ignore', 'ignore', 'inherit'], cwd: raiz },
  )
}
