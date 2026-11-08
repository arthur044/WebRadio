import { useEffect, useId, useRef, useState } from 'react'
import { descreverErro } from '../../shared/api/descreverErro'
import type { PedidoDto } from '../../shared/api/types'
import { formatarDuracao } from '../biblioteca/limites'
import { useMusicasParaPedido } from '../biblioteca/useMidias'

const botao = 'border-border rounded-md border px-2.5 py-1 text-xs font-medium'
const ATRASO_DA_BUSCA_MS = 300

/**
 * E4-P04: aprovar ligando o pedido a uma música da biblioteca (só `Musica`, `Aprovado` e ativa: é o que o servidor aceita,
 * E4-D6). O playout toca a mídia sozinho e marca o pedido como tocado. Aprovar sem mídia segue pelo botão "Aprovar".
 * Título e artista da biblioteca são texto de usuário: só como filho de elemento.
 */
export function AprovarComMidiaDialogo({
  pedido,
  ocupado,
  erro,
  onConfirmar,
  onCancelar,
}: {
  pedido: PedidoDto
  ocupado: boolean
  erro: string | null
  onConfirmar: (midiaId: string) => void
  onCancelar: () => void
}) {
  const idBusca = useId()
  const idErro = useId()
  const campoRef = useRef<HTMLInputElement>(null)
  const [texto, setTexto] = useState('')
  const [busca, setBusca] = useState('')
  const [escolhida, setEscolhida] = useState<string | null>(null)
  const musicas = useMusicasParaPedido(busca)

  useEffect(() => {
    campoRef.current?.focus()
  }, [])

  useEffect(() => {
    const id = window.setTimeout(() => setBusca(texto), ATRASO_DA_BUSCA_MS)
    return () => window.clearTimeout(id)
  }, [texto])

  return (
    <form
      role="dialog"
      aria-label={`Aprovar o pedido de ${pedido.tituloMusica} com mídia`}
      onSubmit={(e) => {
        e.preventDefault()
        if (escolhida) onConfirmar(escolhida)
      }}
      noValidate
      className="mt-2 flex flex-col gap-2 text-sm"
    >
      <label htmlFor={idBusca}>Buscar na biblioteca (só músicas aprovadas e ativas)</label>
      <input
        ref={campoRef}
        id={idBusca}
        type="search"
        value={texto}
        onChange={(e) => {
          setTexto(e.target.value)
          setEscolhida(null)
        }}
        placeholder="Título, artista ou arquivo"
        className="border-border bg-surface-raised rounded-md border px-3 py-2"
      />

      {musicas.isPending ? (
        <p className="text-text-muted">Carregando…</p>
      ) : musicas.isError && !musicas.data ? (
        <p role="alert" className="text-on-air text-xs">
          {descreverErro(musicas.error).mensagem}
        </p>
      ) : musicas.data.itens.length === 0 ? (
        <p className="text-text-muted">Nenhuma música encontrada.</p>
      ) : (
        <fieldset className="flex flex-col gap-1">
          <legend className="sr-only">Músicas da biblioteca</legend>
          {musicas.data.itens.map((m) => {
            const nome = m.titulo ?? m.nomeOriginal
            return (
              <label key={m.id} className="flex items-start gap-2 break-words">
                <input
                  type="radio"
                  name={`midia-${pedido.id}`}
                  checked={escolhida === m.id}
                  onChange={() => setEscolhida(m.id)}
                  className="mt-1"
                />
                <span>
                  {nome}
                  {m.artista && <span className="text-text-muted"> — {m.artista}</span>}
                  {m.duracaoSegundos !== null && (
                    <span className="text-text-muted"> · {formatarDuracao(m.duracaoSegundos)}</span>
                  )}
                </span>
              </label>
            )
          })}
          {musicas.data.total > musicas.data.itens.length && (
            <p className="text-text-muted text-xs">
              Mostrando {musicas.data.itens.length} de {musicas.data.total}. Refine a busca.
            </p>
          )}
        </fieldset>
      )}

      {erro && (
        <p id={idErro} role="alert" className="text-on-air text-xs">
          {erro}
        </p>
      )}
      <span className="flex gap-2">
        <button type="submit" disabled={ocupado || escolhida === null} className={botao}>
          Aprovar com esta mídia
        </button>
        <button type="button" disabled={ocupado} className={botao} onClick={onCancelar}>
          Cancelar
        </button>
      </span>
    </form>
  )
}
