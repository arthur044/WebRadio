import { useEffect, useId, useRef, useState, type FormEvent } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { ApiError } from '../../shared/api/httpClient'
import { descreverErro } from '../../shared/api/descreverErro'
import type { PedidoDto } from '../../shared/api/types'
import { formatarDataHoraBrasilia } from '../../shared/lib/timezone'
import { tamanhoDoTexto } from '../pedidos/limites'
import {
  moderarPedido,
  removerDaFila,
  usePedidosDaFila,
  type AcaoDeModeracao,
} from './useFilaDePedidos'

const MOTIVO_MAXIMO = 280
const botao = 'border-border rounded-md border px-2.5 py-1 text-xs font-medium'

function RejeitarDialogo({
  pedido,
  ocupado,
  erro,
  onConfirmar,
  onCancelar,
}: {
  pedido: PedidoDto
  ocupado: boolean
  erro: string | null
  onConfirmar: (motivo: string) => void
  onCancelar: () => void
}) {
  const idCampo = useId()
  const idErro = useId()
  const campoRef = useRef<HTMLInputElement>(null)
  const [motivo, setMotivo] = useState('')
  const [invalido, setInvalido] = useState<string | null>(null)
  const tamanho = tamanhoDoTexto(motivo)

  useEffect(() => {
    campoRef.current?.focus()
  }, [])

  function confirmar(e: FormEvent) {
    e.preventDefault()
    if (motivo.trim() === '') return setInvalido('Informe o motivo da rejeição.')
    if (tamanho > MOTIVO_MAXIMO)
      return setInvalido(`O motivo aceita no máximo ${MOTIVO_MAXIMO} caracteres.`)
    setInvalido(null)
    onConfirmar(motivo.normalize('NFC').trim())
  }

  const mensagemDeErro = invalido ?? erro
  return (
    <form
      role="dialog"
      aria-label={`Rejeitar o pedido de ${pedido.tituloMusica}`}
      onSubmit={confirmar}
      noValidate
      className="mt-2 flex flex-col gap-2 text-sm"
    >
      <label htmlFor={idCampo}>Motivo da rejeição (o ouvinte vai ler)</label>
      {/* Linha única: o servidor recusa quebra de linha no motivo (E3-D7). */}
      <input
        ref={campoRef}
        id={idCampo}
        type="text"
        value={motivo}
        onChange={(e) => setMotivo(e.target.value)}
        className="border-border bg-surface-raised rounded-md border px-3 py-2"
        aria-invalid={mensagemDeErro ? true : undefined}
        aria-describedby={mensagemDeErro ? idErro : undefined}
      />
      <span className={`text-xs ${tamanho > MOTIVO_MAXIMO ? 'text-on-air' : 'text-text-muted'}`}>
        {tamanho}/{MOTIVO_MAXIMO}
      </span>
      {mensagemDeErro && (
        <p id={idErro} className="text-on-air text-xs">
          {mensagemDeErro}
        </p>
      )}
      <span className="flex gap-2">
        <button type="submit" disabled={ocupado} className={`${botao} text-on-air`}>
          Confirmar rejeição
        </button>
        <button type="button" disabled={ocupado} className={botao} onClick={onCancelar}>
          Cancelar
        </button>
      </span>
    </form>
  )
}

/** Nome, música, artista e dedicatória são texto de anônimo: só como filho de elemento, nunca como HTML. */
function ItemDaFila({
  pedido,
  aprovado,
  onJaModerado,
}: {
  pedido: PedidoDto
  aprovado: boolean
  onJaModerado: (mensagem: string) => void
}) {
  const queryClient = useQueryClient()
  const [ocupado, setOcupado] = useState(false)
  const [rejeitando, setRejeitando] = useState(false)
  const [aviso, setAviso] = useState<string | null>(null)
  const rejeitarRef = useRef<HTMLButtonElement>(null)
  const devolverFoco = useRef(false)

  // O botão "Rejeitar" continua no DOM; ao cancelar, o foco volta a ele em vez de cair no <body>.
  useEffect(() => {
    if (!rejeitando && devolverFoco.current) {
      devolverFoco.current = false
      rejeitarRef.current?.focus()
    }
  }, [rejeitando])

  async function agir(acao: AcaoDeModeracao, motivo?: string) {
    setOcupado(true)
    setAviso(null)
    try {
      await moderarPedido(pedido.id, acao, motivo)
      setRejeitando(false)
      void queryClient.invalidateQueries({ queryKey: ['pedidos-moderacao'] })
    } catch (err) {
      const descrito = descreverErro(err)
      if (err instanceof ApiError && err.problem.type === '/erros/pedido-ja-moderado') {
        // Outro locutor chegou antes: o item sai da lista e o aviso sobe para a seção (o item some com ele).
        removerDaFila(queryClient, pedido.id)
        onJaModerado(descrito.mensagem)
        return
      }
      setAviso(descrito.campos.motivo ?? descrito.mensagem)
    } finally {
      setOcupado(false)
    }
  }

  function cancelarRejeicao() {
    devolverFoco.current = true
    setRejeitando(false)
    setAviso(null)
  }

  return (
    <li className="border-border flex flex-col gap-1 border-b py-3 last:border-b-0">
      <p className="font-medium break-words">
        {pedido.tituloMusica}{' '}
        <span className="text-text-muted font-normal">— {pedido.artista}</span>
      </p>
      <p className="text-text-muted text-xs break-words">
        De {pedido.nomeOuvinte} · {formatarDataHoraBrasilia(pedido.criadoEmUtc)}
      </p>
      {pedido.mensagem && <p className="text-sm break-words">&ldquo;{pedido.mensagem}&rdquo;</p>}
      {aviso && !rejeitando && (
        <p role="alert" className="text-on-air text-sm">
          {aviso}
        </p>
      )}

      {aprovado ? (
        <span className="mt-1 flex gap-2">
          <button
            type="button"
            className={botao}
            disabled={ocupado}
            aria-label={`Marcar ${pedido.tituloMusica} como tocado`}
            onClick={() => void agir('marcar-tocado')}
          >
            Marcar como tocado
          </button>
        </span>
      ) : rejeitando ? (
        <RejeitarDialogo
          pedido={pedido}
          ocupado={ocupado}
          erro={aviso}
          onConfirmar={(motivo) => void agir('rejeitar', motivo)}
          onCancelar={cancelarRejeicao}
        />
      ) : (
        <span className="mt-1 flex gap-2">
          <button
            type="button"
            className={botao}
            disabled={ocupado}
            aria-label={`Aprovar ${pedido.tituloMusica}`}
            onClick={() => void agir('aprovar')}
          >
            Aprovar
          </button>
          <button
            ref={rejeitarRef}
            type="button"
            className={botao}
            disabled={ocupado}
            aria-label={`Rejeitar ${pedido.tituloMusica}`}
            onClick={() => {
              setAviso(null)
              setRejeitando(true)
            }}
          >
            Rejeitar
          </button>
        </span>
      )}
    </li>
  )
}

function Lista({
  status,
  vazio,
  onJaModerado,
}: {
  status: 'Pendente' | 'Aprovado'
  vazio: string
  onJaModerado: (mensagem: string) => void
}) {
  const { data, isPending, isError, error, refetch } = usePedidosDaFila(status)

  if (isPending) return <p className="text-text-muted mt-2 text-sm">Carregando…</p>
  if (isError && !data) {
    return (
      <div className="mt-2 flex flex-wrap items-center gap-2">
        <p role="alert" className="text-on-air text-sm">
          {descreverErro(error).mensagem}
        </p>
        <button type="button" className={botao} onClick={() => void refetch()}>
          Tentar de novo
        </button>
      </div>
    )
  }
  if (data.itens.length === 0) return <p className="text-text-muted mt-2 text-sm">{vazio}</p>

  return (
    <>
      <ul aria-live="polite" className="mt-1">
        {data.itens.map((pedido) => (
          <ItemDaFila
            key={pedido.id}
            pedido={pedido}
            aprovado={status === 'Aprovado'}
            onJaModerado={onJaModerado}
          />
        ))}
      </ul>
      {data.total > data.itens.length && (
        <p className="text-text-muted mt-1 text-xs">
          Mostrando {data.itens.length} de {data.total}. Os demais aparecem quando estes forem
          moderados.
        </p>
      )}
    </>
  )
}

const AVISO_JA_MODERADO_MS = 8000

/**
 * E3-P03: a fila de moderação (Locutor e Admin). Os pendentes chegam por `PedidoCriado` e saem por `PedidoModerado`
 * (o hub invalida `pedidos-moderacao`), mais antigos primeiro, como o servidor ordena. Os aprovados são os das últimas
 * 24 h (E3-D9), para marcar como tocado.
 */
export function FilaDeModeracao() {
  const [jaModerado, setJaModerado] = useState<string | null>(null)

  // O aviso é informativo: some sozinho, e um novo aviso reinicia a contagem.
  useEffect(() => {
    if (jaModerado === null) return
    const id = window.setTimeout(() => setJaModerado(null), AVISO_JA_MODERADO_MS)
    return () => window.clearTimeout(id)
  }, [jaModerado])

  return (
    <section aria-labelledby="titulo-pedidos" className="border-border rounded-lg border p-4">
      <h2 id="titulo-pedidos" className="font-display text-lg font-bold">
        Pedidos de música
      </h2>

      {jaModerado && (
        <p role="status" className="text-text mt-2 text-sm">
          {jaModerado}
        </p>
      )}

      <h3 className="font-display text-text-muted mt-3 text-xs font-bold tracking-wide uppercase">
        Aguardando moderação
      </h3>
      <Lista status="Pendente" vazio="Nenhum pedido aguardando." onJaModerado={setJaModerado} />

      <h3 className="font-display text-text-muted mt-4 text-xs font-bold tracking-wide uppercase">
        Aprovados (últimas 24 h)
      </h3>
      <Lista
        status="Aprovado"
        vazio="Nenhum pedido aprovado nas últimas 24 horas."
        onJaModerado={setJaModerado}
      />
    </section>
  )
}
