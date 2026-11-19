import { create } from 'zustand'
import { apiFetch, ApiError } from '../../shared/api/httpClient'
import { descreverErro } from '../../shared/api/descreverErro'
import type { TipoMidia, UploadMidiaDto } from '../../shared/api/types'
import { mimeDeclarado, recusaLocal } from './limites'

/** Se o 429 não trouxer `retryAfterSeconds`, espera isto antes de tentar o mesmo envio de novo. */
const ESPERA_PADRAO_S = 60

export type FaseDoEnvio =
  | { fase: 'aguardando' }
  | { fase: 'preparando' }
  | { fase: 'enviando'; progresso: number }
  | { fase: 'concluindo' }
  | { fase: 'espera'; ateEm: number }
  | { fase: 'enviado'; midiaId: string }
  | { fase: 'erro'; mensagem: string }
  | { fase: 'cancelado' }

export interface Envio {
  id: string
  arquivo: File
  tipo: TipoMidia
  titulo: string
  artista: string
  estado: FaseDoEnvio
  /**
   * Preenchido quando o arquivo já está no storage e só falta o `concluir`: se ele falhar, "tentar de novo" repete só o
   * `concluir`, em vez de pedir outra policy (mais um dos 5 abertos) e reenviar até 250 MB.
   */
  midiaId?: string
  /**
   * `AguardandoUpload` aberto por este envio (um dos 5 do S-M06) enquanto o arquivo não chegou ao storage. Se o envio
   * falha ou é cancelado antes disso, a tela o devolve com `DELETE /midias/{id}` (spec 08 v1.5); fica aqui até o
   * `DELETE` dar certo, e o "tentar de novo" o repete antes de pedir uma policy nova.
   */
  slotAberto?: string
}

interface EnviosState {
  envios: Envio[]
  adicionar: (dados: Omit<Envio, 'id' | 'estado'>) => void
  cancelar: (id: string) => void
  tentarDeNovo: (id: string) => void
  /** Tira da lista os que já terminaram (enviado, erro ou cancelado). */
  limparConcluidos: () => void
}

function guardar(id: string, campos: Pick<Envio, 'midiaId' | 'slotAberto'>): void {
  useEnvios.setState((s) => ({
    envios: s.envios.map((e) => (e.id === id ? { ...e, ...campos } : e)),
  }))
}

/**
 * Devolve o slot de `AguardandoUpload` ao servidor (só no cancelamento, na recusa do storage ou na falha do upload; uma
 * falha do `concluir` repete o `concluir`, nunca chama isto). 204 libera e 404 (já saiu) não deixa nada a devolver.
 * 409 = um `concluir` ganhou a corrida, então o envio está concluído: a tela segue como concluído. Outra falha (rede,
 * 5xx) mantém o `slotAberto` para a próxima tentativa; o servidor limpa sozinho no `UploadExpiraEmUtc`.
 */
async function desistirDoSlot(id: string, slot: string): Promise<void> {
  try {
    await apiFetch<void>(`/midias/${encodeURIComponent(slot)}`, { method: 'DELETE' })
  } catch (err) {
    if (!(err instanceof ApiError) || ![404, 409].includes(err.status)) return
    if (err.status === 409) {
      guardar(id, { midiaId: slot, slotAberto: undefined })
      atualizar(id, { fase: 'enviado', midiaId: slot })
      return
    }
  }
  guardar(id, { slotAberto: undefined })
}

function atualizar(id: string, estado: FaseDoEnvio): void {
  useEnvios.setState((s) => ({
    envios: s.envios.map((e) => (e.id === id ? { ...e, estado } : e)),
  }))
}

/**
 * A fila vive no módulo (não no componente): o envio continua se a pessoa trocar de aba do Estúdio, como o player. Um
 * envio por vez (E4-P02): o servidor limita a 5 abertos por usuário (S-M06) e o upload de uma música usa a banda toda.
 */
export const useEnvios = create<EnviosState>()((set, get) => ({
  envios: [],

  adicionar: (dados) => {
    const id = crypto.randomUUID()
    set((s) => ({ envios: [...s.envios, { ...dados, id, estado: { fase: 'aguardando' } }] }))
    void processarFila()
  },

  cancelar: (id) => {
    const envio = get().envios.find((e) => e.id === id)
    if (!envio) return
    const { fase } = envio.estado
    if (fase === 'aguardando' || fase === 'espera') {
      limparEspera(id)
      atualizar(id, { fase: 'cancelado' })
      // Com este em `espera` a fila estava parada: os `aguardando` seguintes andam agora.
      void processarFila()
    } else if (fase === 'preparando' || fase === 'enviando') {
      controles.get(id)?.abort()
    }
    // `concluindo`: o objeto já está no storage e o servidor já pode estar analisando; não há o que desfazer aqui.
  },

  tentarDeNovo: (id) => {
    atualizar(id, { fase: 'aguardando' })
    void processarFila()
  },

  limparConcluidos: () =>
    set((s) => ({
      envios: s.envios.filter((e) => !['enviado', 'erro', 'cancelado'].includes(e.estado.fase)),
    })),
}))

const controles = new Map<string, AbortController>()
const timersDeEspera = new Map<string, ReturnType<typeof setTimeout>>()
let rodando = false

function limparEspera(id: string): void {
  const t = timersDeEspera.get(id)
  if (t !== undefined) clearTimeout(t)
  timersDeEspera.delete(id)
}

/** Consome a fila em ordem. Com algum envio esperando o 429 passar, a fila inteira espera (os outros bateriam no mesmo limite). */
export async function processarFila(): Promise<void> {
  if (rodando) return
  rodando = true
  try {
    for (;;) {
      const { envios } = useEnvios.getState()
      if (envios.some((e) => e.estado.fase === 'espera')) return
      const proximo = envios.find((e) => e.estado.fase === 'aguardando')
      if (!proximo) return
      await executar(proximo)
    }
  } finally {
    rodando = false
  }
}

/** Origem e prefixo da policy: o arquivo só vai para o storage da própria rádio, nunca para uma URL que a resposta mandar. */
export function urlDaPolicy(url: string): URL | null {
  try {
    const alvo = new URL(url, window.location.origin)
    if (alvo.origin !== window.location.origin) return null
    if (alvo.pathname !== '/storage/quarentena') return null
    return alvo
  } catch {
    return null
  }
}

function textoOuNulo(valor: string): string | null {
  const t = valor.normalize('NFC').trim()
  return t === '' ? null : t
}

/** Recusa do storage ao `POST` do arquivo (policy vencida, tamanho fora do que a policy permite...). */
export class ErroDoStorage extends Error {}

function mensagemDeErro(err: unknown): string {
  if (err instanceof ErroDoStorage) return err.message
  const descrito = descreverErro(err)
  return (
    descrito.campos.nomeOriginal ??
    descrito.campos.titulo ??
    descrito.campos.artista ??
    descrito.campos.tamanhoBytes ??
    descrito.campos.mimeTypeDeclarado ??
    descrito.mensagem
  )
}

async function executar(envio: Envio): Promise<void> {
  const { id, arquivo, tipo } = envio
  const controle = new AbortController()
  controles.set(id, controle)
  let slot = envio.slotAberto
  let midiaId = envio.midiaId
  try {
    // Sobra de uma tentativa anterior cujo `DELETE` não chegou: devolve antes de ocupar outro dos 5 abertos.
    if (midiaId === undefined && slot !== undefined) {
      await desistirDoSlot(id, slot)
      slot = useEnvios.getState().envios.find((e) => e.id === id)?.slotAberto
    }
    if (midiaId === undefined) {
      const recusa = recusaLocal(arquivo, tipo)
      if (recusa) return atualizar(id, { fase: 'erro', mensagem: recusa })

      atualizar(id, { fase: 'preparando' })
      const policy = await apiFetch<UploadMidiaDto>('/midias/uploads', {
        method: 'POST',
        signal: controle.signal,
        body: {
          nomeOriginal: arquivo.name,
          tipoMidia: tipo,
          tamanhoBytes: arquivo.size,
          mimeTypeDeclarado: mimeDeclarado(arquivo),
          titulo: textoOuNulo(envio.titulo),
          artista: textoOuNulo(envio.artista),
        },
      })

      slot = policy.midiaId
      guardar(id, { slotAberto: slot })

      const destino = urlDaPolicy(policy.upload.url)
      if (destino === null) {
        atualizar(id, {
          fase: 'erro',
          mensagem: 'O servidor devolveu um destino de envio inesperado.',
        })
        return await desistirDoSlot(id, slot)
      }

      atualizar(id, { fase: 'enviando', progresso: 0 })
      await enviarAoStorage(
        destino.href,
        policy.upload.campos,
        arquivo,
        (progresso) => atualizar(id, { fase: 'enviando', progresso }),
        controle.signal,
      )
      // O arquivo já está no storage: daqui em diante, uma falha no `concluir` não refaz o envio.
      midiaId = policy.midiaId
      slot = undefined
      guardar(id, { midiaId, slotAberto: undefined })
    }

    atualizar(id, { fase: 'concluindo' })
    await apiFetch<void>(`/midias/${encodeURIComponent(midiaId)}/concluir`, { method: 'POST' })
    atualizar(id, { fase: 'enviado', midiaId })
  } catch (err) {
    if (controle.signal.aborted) {
      atualizar(id, { fase: 'cancelado' })
      if (slot !== undefined) await desistirDoSlot(id, slot)
      return
    }
    if (err instanceof ApiError && err.status === 429) {
      const segundos = descreverErro(err).esperarSegundos ?? ESPERA_PADRAO_S
      atualizar(id, { fase: 'espera', ateEm: Date.now() + segundos * 1000 })
      timersDeEspera.set(
        id,
        setTimeout(() => {
          timersDeEspera.delete(id)
          atualizar(id, { fase: 'aguardando' })
          void processarFila()
        }, segundos * 1000),
      )
      return
    }
    atualizar(id, { fase: 'erro', mensagem: mensagemDeErro(err) })
    // Falha antes do `concluir` (storage recusou, rede): o slot volta agora, antes de qualquer "tentar de novo".
    if (slot !== undefined) await desistirDoSlot(id, slot)
  } finally {
    controles.delete(id)
  }
}

/**
 * `multipart/form-data` direto ao storage (POST policy, D10): os `campos` da policy primeiro e o `file` por último (o
 * S3 ignora o que vem depois dele). XHR e não `fetch`, porque só o XHR informa o progresso do envio. Sem `Authorization`:
 * a policy assinada é a credencial.
 */
export function enviarAoStorage(
  url: string,
  campos: Record<string, string>,
  arquivo: File,
  aoProgredir: (progresso: number) => void,
  sinal: AbortSignal,
): Promise<void> {
  return new Promise((resolve, reject) => {
    const corpo = new FormData()
    for (const [chave, valor] of Object.entries(campos)) corpo.append(chave, valor)
    corpo.append('file', arquivo)

    const xhr = new XMLHttpRequest()
    const abortar = () => xhr.abort()
    sinal.addEventListener('abort', abortar, { once: true })
    const terminar = () => sinal.removeEventListener('abort', abortar)

    xhr.upload.onprogress = (e) => {
      if (e.lengthComputable && e.total > 0) aoProgredir(e.loaded / e.total)
    }
    xhr.onload = () => {
      terminar()
      if (xhr.status >= 200 && xhr.status < 300) return resolve()
      reject(new ErroDoStorage(`O armazenamento recusou o arquivo (HTTP ${xhr.status}).`))
    }
    xhr.onerror = () => {
      terminar()
      reject(new TypeError('Falha de rede no envio do arquivo.'))
    }
    xhr.onabort = () => {
      terminar()
      reject(new DOMException('Envio cancelado', 'AbortError'))
    }
    xhr.open('POST', url)
    xhr.send(corpo)
  })
}
