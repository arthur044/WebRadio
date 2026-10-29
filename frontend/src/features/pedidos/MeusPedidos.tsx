import { descreverErro } from '../../shared/api/descreverErro'
import type { PedidoDto, PedidoStatus } from '../../shared/api/types'
import { formatarDataHoraBrasilia } from '../../shared/lib/timezone'
import { useMeusPedidos } from './useMeusPedidos'

const ROTULO: Record<PedidoStatus, string> = {
  Pendente: 'Aguardando o locutor',
  Aprovado: 'Aprovado',
  Rejeitado: 'Rejeitado',
  Tocado: 'Tocado',
  Expirado: 'Expirou sem resposta',
}

const COR: Record<PedidoStatus, string> = {
  Pendente: 'text-text-muted',
  Aprovado: 'text-text',
  Rejeitado: 'text-on-air',
  Tocado: 'text-text',
  Expirado: 'text-text-muted',
}

/** Nome, música e motivo são texto do ouvinte ou do locutor: vão só como filho de elemento, nunca como HTML. */
function ItemDoPedido({ pedido }: { pedido: PedidoDto }) {
  return (
    <li className="border-border flex flex-col gap-1 border-b py-3 last:border-b-0">
      <div className="flex flex-wrap items-baseline justify-between gap-2">
        <p className="min-w-0 font-medium break-words">
          {pedido.tituloMusica}{' '}
          <span className="text-text-muted font-normal">— {pedido.artista}</span>
        </p>
        <span className={`text-xs font-medium ${COR[pedido.status]}`}>{ROTULO[pedido.status]}</span>
      </div>
      <p className="text-text-muted text-xs">
        Pedido em {formatarDataHoraBrasilia(pedido.criadoEmUtc)}
      </p>
      {pedido.status === 'Rejeitado' && pedido.motivoRejeicao && (
        <p className="text-sm break-words">Motivo: {pedido.motivoRejeicao}</p>
      )}
    </li>
  )
}

/**
 * E3-P02: os pedidos do dispositivo. Muda sozinho por `PedidoModerado`/`PedidoTocado` (o hub invalida `pedidos-meus`)
 * e relê ao reconectar o hub (`invalidarEstadoDaRadio`).
 */
export function MeusPedidos() {
  const { data, isPending, isError, error, refetch } = useMeusPedidos()

  return (
    <section aria-labelledby="meus-pedidos" className="mt-8 max-w-md">
      <h2 id="meus-pedidos" className="font-display text-lg font-bold">
        Meus pedidos
      </h2>

      {isPending && <p className="text-text-muted mt-2 text-sm">Carregando seus pedidos…</p>}

      {isError && !data && (
        <div className="mt-2 flex flex-wrap items-center gap-2">
          <p role="alert" className="text-on-air text-sm">
            {descreverErro(error).mensagem}
          </p>
          <button
            type="button"
            className="border-border rounded-md border px-2.5 py-1 text-xs font-medium"
            onClick={() => void refetch()}
          >
            Tentar de novo
          </button>
        </div>
      )}

      {data && data.length === 0 && (
        <p className="text-text-muted mt-2 text-sm">Você ainda não fez nenhum pedido.</p>
      )}

      {data && data.length > 0 && (
        <ul aria-live="polite" className="mt-2">
          {data.map((pedido) => (
            <ItemDoPedido key={pedido.id} pedido={pedido} />
          ))}
        </ul>
      )}
    </section>
  )
}
