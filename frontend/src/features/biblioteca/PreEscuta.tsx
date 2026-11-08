import { useEffect, useState } from 'react'
import { descreverErro } from '../../shared/api/descreverErro'
import type { MidiaDto } from '../../shared/api/types'
import { resolverUrlDePreview } from './useMidias'

type Estado =
  { tipo: 'carregando' } | { tipo: 'pronto'; url: string } | { tipo: 'erro'; mensagem: string }

/**
 * Pré-escuta da biblioteca (E4-P01): um `<audio>` próprio da tela, nunca o player global, que segue tocando a rádio.
 * A URL pré-assinada vale 5 min; o pai monta este componente com `key` da mídia, então escolher outra (ou reabrir a mesma) busca uma nova. Sair da tela desmonta o
 * elemento e o áudio para.
 */
export function PreEscuta({ midia, aoFechar }: { midia: MidiaDto; aoFechar: () => void }) {
  const [estado, setEstado] = useState<Estado>({ tipo: 'carregando' })

  useEffect(() => {
    let cancelado = false
    resolverUrlDePreview(midia.id)
      .then((url) => {
        if (!cancelado) setEstado({ tipo: 'pronto', url })
      })
      .catch((err: unknown) => {
        if (!cancelado) setEstado({ tipo: 'erro', mensagem: descreverErro(err).mensagem })
      })
    return () => {
      cancelado = true
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
          controls
          autoPlay
          src={estado.url}
          aria-label={`Pré-escuta de ${midia.titulo ?? midia.nomeOriginal}`}
          className="w-full"
          onError={() =>
            setEstado({
              tipo: 'erro',
              mensagem: 'Não foi possível tocar este arquivo. Tente de novo.',
            })
          }
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
