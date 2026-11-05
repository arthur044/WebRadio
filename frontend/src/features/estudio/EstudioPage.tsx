import { useAuthStore } from '../../shared/api/authStore'
import { AlertasDoHarbor } from './AlertasDoHarbor'
import { CredenciaisAtivasAdmin } from './CredenciaisAtivasAdmin'
import { EstadoNoArPainel } from './EstadoNoArPainel'
import { FilaDeModeracao } from './FilaDeModeracao'
import { PainelDeTransmissao } from './PainelDeTransmissao'

export function EstudioPage() {
  const role = useAuthStore((s) => s.usuario?.role)

  return (
    <section className="flex flex-col gap-4">
      <h1 className="font-display text-2xl font-bold">Estúdio</h1>
      <AlertasDoHarbor para="Locutor" />
      <EstadoNoArPainel />
      <FilaDeModeracao />
      <PainelDeTransmissao />
      {role === 'Admin' && <CredenciaisAtivasAdmin />}
    </section>
  )
}
