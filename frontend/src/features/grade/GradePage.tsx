import { useQuery } from '@tanstack/react-query'
import { apiFetch } from '../../shared/api/httpClient'
import type { ProgramaDto } from '../../shared/api/types'
import {
  chaveDataBrasilia,
  formatarDiaBrasilia,
  formatarHoraBrasilia,
  semanaAtualBrasiliaParaUtc,
} from '../../shared/lib/timezone'

function useGradeDaSemana() {
  const { deUtc, ateUtc } = semanaAtualBrasiliaParaUtc()
  return useQuery({
    queryKey: ['grade-semana', deUtc, ateUtc],
    queryFn: () =>
      apiFetch<ProgramaDto[]>(
        `/programas?deUtc=${encodeURIComponent(deUtc)}&ateUtc=${encodeURIComponent(ateUtc)}`,
      ),
  })
}

/** Agrupa por dia em Brasília, preservando a ordem cronológica que a API já devolve. */
function agruparPorDia(programas: ProgramaDto[]): Array<[string, ProgramaDto[]]> {
  const porDia = new Map<string, ProgramaDto[]>()
  for (const programa of programas) {
    const chave = chaveDataBrasilia(programa.inicioUtc)
    const grupo = porDia.get(chave)
    if (grupo) grupo.push(programa)
    else porDia.set(chave, [programa])
  }
  return [...porDia.entries()].sort(([a], [b]) => a.localeCompare(b))
}

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
        <span className="flex shrink-0 items-center gap-1.5">
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
  const { data, isPending, isError } = useGradeDaSemana()

  return (
    <section>
      <h1 className="font-display text-2xl font-bold">Grade</h1>

      {isPending && <p className="text-text-muted mt-4 text-sm">Carregando a programação…</p>}

      {isError && (
        <p role="alert" className="text-on-air mt-4 text-sm">
          Não foi possível carregar a grade agora.
        </p>
      )}

      {data && data.length === 0 && (
        <p className="text-text-muted mt-4 text-sm">Nenhum programa agendado esta semana.</p>
      )}

      {data && data.length > 0 && (
        <div className="mt-4 flex flex-col gap-6">
          {agruparPorDia(data).map(([chaveDia, programasDoDia]) => (
            <div key={chaveDia}>
              <h2 className="font-display text-text-muted text-xs font-bold tracking-wide uppercase">
                {formatarDiaBrasilia(chaveDia)}
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
