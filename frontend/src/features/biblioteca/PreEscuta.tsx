import { useCallback, useEffect, useRef, useState } from 'react'
import { descreverErro } from '../../shared/api/descreverErro'
import type { MidiaDto } from '../../shared/api/types'
import { resolverUrlDePreview, type UrlDePreview } from './useMidias'

type Estado =
  | { tipo: 'carregando' }
  | { tipo: 'pronto'; url: string; expiraEm: number | null; retomarEm: number }
  | { tipo: 'erro'; mensagem: string }

/** Quanto antes do vencimento a próxima URL já é buscada. */
const ANTECEDENCIA_MS = 30_000
/** Piso e teto da espera até essa busca: um relógio local errado não pode virar um laço de chamadas. */
const ESPERA_MINIMA_MS = 60_000
const ESPERA_MAXIMA_MS = 270_000
/** `error` seguidos, sem o áudio chegar a tocar, antes de desistir (arquivo ou rede com problema de verdade). */
const FALHAS_SEGUIDAS_MAXIMAS = 2
/** URL guardada que vence em menos que isto não vale mais: o `<audio>` ainda precisa de tempo para pedi-la. */
const MARGEM_DE_USO_MS = 10_000

const MENSAGEM_FALHA = 'Não foi possível tocar este arquivo. Tente de novo.'

/**
 * Pré-escuta da biblioteca (E4-P01): um `<audio>` próprio da tela, nunca o player global, que segue tocando a rádio.
 * O pai monta este componente com `key` da mídia, então escolher outra (ou reabrir a mesma) busca uma URL nova. Sair da tela desmonta o elemento e o áudio para.
 *
 * A URL pré-assinada vale 5 min e a música até 20: depois de vencida, qualquer nova requisição ao storage (um seek, um
 * rebuffer, a reconexão) toma 403. Por isso (a) perto de `expiraEm` a próxima URL já é buscada e fica guardada, sem
 * trocar o `src` com o áudio tocando, e (b) no `error` do `<audio>` troca-se o `src` por ela (ou por uma recém-buscada)
 * e retoma-se do `currentTime`. A URL mora só no estado e no `src`: nunca vai a `localStorage`, log ou telemetria, e uma
 * URL vencida nunca é reaproveitada.
 */
export function PreEscuta({ midia, aoFechar }: { midia: MidiaDto; aoFechar: () => void }) {
  const [estado, setEstado] = useState<Estado>({ tipo: 'carregando' })
  const audio = useRef<HTMLAudioElement>(null)
  const montado = useRef(true)
  const proxima = useRef<UrlDePreview | null>(null)
  const renovando = useRef(false)
  const falhasSeguidas = useRef(0)

  useEffect(() => {
    montado.current = true
    return () => {
      montado.current = false
    }
  }, [])

  useEffect(() => {
    let cancelado = false
    resolverUrlDePreview(midia.id)
      .then(({ url, expiraEm }) => {
        if (!cancelado) setEstado({ tipo: 'pronto', url, expiraEm, retomarEm: 0 })
      })
      .catch((err: unknown) => {
        if (!cancelado) setEstado({ tipo: 'erro', mensagem: descreverErro(err).mensagem })
      })
    return () => {
      cancelado = true
    }
  }, [midia.id])

  // Perto do vencimento da URL atual, busca a próxima e a guarda (o `src` só troca no `error`).
  const urlAtual = estado.tipo === 'pronto' ? estado.url : null
  const expiraEm = estado.tipo === 'pronto' ? estado.expiraEm : null
  useEffect(() => {
    if (urlAtual === null) return
    const faltam = (expiraEm ?? Date.now() + ESPERA_MAXIMA_MS) - Date.now() - ANTECEDENCIA_MS
    const espera = Math.min(Math.max(faltam, ESPERA_MINIMA_MS), ESPERA_MAXIMA_MS)
    let cancelado = false
    const timer = setTimeout(() => {
      resolverUrlDePreview(midia.id)
        .then((nova) => {
          if (!cancelado) proxima.current = nova
        })
        .catch(() => {
          // Sem a URL antecipada, o `error` do `<audio>` busca outra na hora.
        })
    }, espera)
    return () => {
      cancelado = true
      clearTimeout(timer)
    }
  }, [midia.id, urlAtual, expiraEm])

  const renovar = useCallback(async () => {
    const elemento = audio.current
    if (!elemento || renovando.current) return
    if (falhasSeguidas.current >= FALHAS_SEGUIDAS_MAXIMAS) {
      setEstado({ tipo: 'erro', mensagem: MENSAGEM_FALHA })
      return
    }
    falhasSeguidas.current += 1
    renovando.current = true
    const ponto = elemento.currentTime
    try {
      const guardada = proxima.current
      proxima.current = null
      const usavel =
        guardada !== null &&
        guardada.expiraEm !== null &&
        guardada.expiraEm - Date.now() > MARGEM_DE_USO_MS
      const nova = usavel ? guardada : await resolverUrlDePreview(midia.id)
      if (montado.current) {
        setEstado({ tipo: 'pronto', url: nova.url, expiraEm: nova.expiraEm, retomarEm: ponto })
      }
    } catch (err: unknown) {
      if (montado.current) setEstado({ tipo: 'erro', mensagem: descreverErro(err).mensagem })
    } finally {
      renovando.current = false
    }
  }, [midia.id])

  return (
    <div
      role="region"
      aria-label="Pré-escuta"
      className="border-border bg-surface-raised mt-3 flex flex-col gap-2 rounded-lg border p-3"
    >
      <p className="text-sm break-words">
        Pré-escuta: <span className="font-medium">{midia.titulo ?? midia.nomeOriginal}</span>
      </p>
      {estado.tipo === 'carregando' && <p className="text-text-muted text-sm">Preparando…</p>}
      {estado.tipo === 'erro' && (
        <p role="alert" className="text-on-air text-sm">
          {estado.mensagem}
        </p>
      )}
      {estado.tipo === 'pronto' && (
        <audio
          ref={audio}
          controls
          autoPlay
          src={estado.url}
          aria-label={`Pré-escuta de ${midia.titulo ?? midia.nomeOriginal}`}
          className="w-full"
          onLoadedMetadata={(e) => {
            if (estado.retomarEm > 0) e.currentTarget.currentTime = estado.retomarEm
          }}
          onPlaying={() => {
            falhasSeguidas.current = 0
          }}
          onError={() => void renovar()}
        />
      )}
      <button
        type="button"
        onClick={aoFechar}
        className="border-border w-fit rounded-md border px-2.5 py-1 text-xs font-medium"
      >
        Fechar pré-escuta
      </button>
    </div>
  )
}
