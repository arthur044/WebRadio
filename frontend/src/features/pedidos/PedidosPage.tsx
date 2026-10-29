import { MeusPedidos } from './MeusPedidos'
import { PedidoForm } from './PedidoForm'

export function PedidosPage() {
  return (
    <section>
      <h1 className="font-display text-2xl font-bold">Pedir Música</h1>
      <p className="text-text-muted mt-1 text-sm">
        Peça uma música e, se o locutor aprovar, ela entra na programação. Não precisa de conta.
      </p>
      <PedidoForm />
      <MeusPedidos />
    </section>
  )
}
