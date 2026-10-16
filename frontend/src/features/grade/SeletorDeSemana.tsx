import { formatarSemanaBrasilia } from '../../shared/lib/timezone'

interface SeletorDeSemanaProps {
  segunda: string
  ehSemanaAtual: boolean
  onAnterior: () => void
  onProxima: () => void
  onAtual: () => void
}

const botao = 'border-border rounded-md border px-3 py-1.5 text-sm font-medium disabled:opacity-50'

export function SeletorDeSemana({
  segunda,
  ehSemanaAtual,
  onAnterior,
  onProxima,
  onAtual,
}: SeletorDeSemanaProps) {
  return (
    <div className="mt-4 flex flex-wrap items-center gap-2">
      <button type="button" onClick={onAnterior} className={botao} aria-label="Semana anterior">
        ←
      </button>
      <span
        aria-live="polite"
        className="font-display min-w-32 text-center text-sm font-bold tabular-nums"
      >
        {formatarSemanaBrasilia(segunda)}
      </span>
      <button type="button" onClick={onProxima} className={botao} aria-label="Próxima semana">
        →
      </button>
      <button
        type="button"
        onClick={onAtual}
        disabled={ehSemanaAtual}
        className={`${botao} ml-2`}
        aria-label="Semana atual"
      >
        Hoje
      </button>
    </div>
  )
}
