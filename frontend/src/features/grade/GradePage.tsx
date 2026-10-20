import type { ProgramaDto } from '../../shared/api/types'
import {
  chaveDataBrasilia,
  formatarDiaBrasilia,
  formatarHoraBrasilia,
} from '../../shared/lib/timezone'
import { agruparPorDia } from './agruparPorDia'
import { SeletorDeSemana } from './SeletorDeSemana'
import { useGradeDaSemana } from './useGradeDaSemana'
import { useSemanaSelecionada } from './useSemanaSelecionada'

function LinhaPrograma({ programa }: { programa: ProgramaDto }) {
  const aoVivo = programa.status === 'AoVivo'
  const cancelado = programa.status === 'Cancelado'

  return (
    <li
      className={`border-border flex items-center gap-3 border-b py-2 last:border-b-0 ${
        cancelado ? 'opacity-50' : ''
      }`}
    >
      <span className="font-display text-text-muted w-24 shrink-0 text-sm tabular-nums">
        {formatarHoraBrasilia(programa.inicioUtc)}
      </span>
      <div className="min-w-0 flex-1">
        <p className="truncate font-medium">
          {programa.titulo}
          {cancelado && <span className="text-text-muted ml-2 text-xs">(cancelado)</span>}
        </p>
        <p className="text-text-muted truncate text-xs">{programa.locutor.nome}</p>
      </div>
      {aoVivo && (
        <span data-testid="selo-ao-vivo" className="flex shrink-0 items-center gap-1.5">
          <span aria-hidden="true" className="bg-on-air h-2 w-2 animate-pulse rounded-full" />
          <span className="font-display text-on-air text-xs font-bold tracking-wide uppercase">
            Ao vivo
          </span>
        </span>
      )}
    </li>
  )
}

export function GradePage() {
  const semana = useSemanaSelecionada()
  const { data, isPending, isError } = useGradeDaSemana(semana.segunda)
  const hoje = chaveDataBrasilia(new Date().toISOString())

  return (
    <section>
      <h1 className="font-display text-2xl font-bold">Grade</h1>

      <SeletorDeSemana
        segunda={semana.segunda}
        ehSemanaAtual={semana.ehSemanaAtual}
        onAnterior={semana.anterior}
        onProxima={semana.proxima}
        onAtual={semana.voltarParaAtual}
      />

      {isPending && <p className="text-text-muted mt-4 text-sm">Carregando a programação…</p>}

      {isError && (
        <p role="alert" className="text-on-air mt-4 text-sm">
          Não foi possível carregar a grade agora.
        </p>
      )}

      {data && data.length === 0 && (
        <p className="text-text-muted mt-4 text-sm">
          {semana.ehSemanaAtual
            ? 'Nenhum programa agendado esta semana.'
            : 'Nenhum programa nesta semana.'}
        </p>
      )}

      {data && data.length > 0 && (
        <div className="mt-4 flex flex-col gap-6">
          {agruparPorDia(data).map(([chaveDia, programasDoDia]) => (
            <div key={chaveDia}>
              <h2 className="font-display text-text-muted text-xs font-bold tracking-wide uppercase">
                {formatarDiaBrasilia(chaveDia)}
                {chaveDia === hoje && <span className="text-accent ml-2">hoje</span>}
              </h2>
              <ul className="border-border bg-surface-raised mt-2 rounded-lg border px-3">
                {programasDoDia.map((programa) => (
                  <LinhaPrograma key={programa.id} programa={programa} />
                ))}
              </ul>
            </div>
          ))}
        </div>
      )}
    </section>
  )
}
