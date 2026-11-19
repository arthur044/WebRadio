import { useId, useState } from 'react'
import { descreverErro } from '../../shared/api/descreverErro'
import { chaveDataBrasilia, somarDias } from '../../shared/lib/timezone'
import { NOMES_DO_TIPO } from '../biblioteca/limites'
import { PERIODO_PADRAO_DIAS, periodoParaUtc, useMaisTocadas } from './useMaisTocadas'

const campoCls = 'border-border bg-surface-raised rounded-md border px-3 py-2'

/**
 * E4-P05: relatório de mídias mais tocadas (Admin). O período padrão são os últimos 30 dias, hoje inclusive; o máximo é
 * 366. Título e artista são texto de usuário: só como filho de célula.
 */
export function MaisTocadasPage() {
  const idDe = useId()
  const idAte = useId()
  const [hoje] = useState(() => chaveDataBrasilia(new Date().toISOString()))
  const [de, setDe] = useState(() => somarDias(hoje, -(PERIODO_PADRAO_DIAS - 1)))
  const [ate, setAte] = useState(hoje)

  const validado = periodoParaUtc(de, ate)
  const { data, isPending, isError, error, refetch, isPlaceholderData } = useMaisTocadas(
    'periodo' in validado ? validado.periodo : null,
  )

  return (
    <section className="flex flex-col gap-4">
      <h1 className="font-display text-2xl font-bold">Mais tocadas</h1>

      <div role="search" className="flex flex-wrap items-end gap-3 text-sm">
        <span className="flex flex-col gap-1">
          <label htmlFor={idDe}>De</label>
          <input
            id={idDe}
            type="date"
            value={de}
            max={ate}
            onChange={(e) => setDe(e.target.value)}
            className={campoCls}
          />
        </span>
        <span className="flex flex-col gap-1">
          <label htmlFor={idAte}>Até</label>
          <input
            id={idAte}
            type="date"
            value={ate}
            min={de}
            onChange={(e) => setAte(e.target.value)}
            className={campoCls}
          />
        </span>
        <p className="text-text-muted text-xs">Datas em horário de Brasília, as duas inclusive.</p>
      </div>

      {'erro' in validado ? (
        <p role="alert" className="text-on-air text-sm">
          {validado.erro}
        </p>
      ) : isPending ? (
        <p className="text-text-muted text-sm">Carregando…</p>
      ) : isError && !data ? (
        <div className="flex flex-wrap items-center gap-2">
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
      ) : data.length === 0 ? (
        <p className="text-text-muted text-sm">Nenhuma reprodução neste período.</p>
      ) : (
        <table
          aria-busy={isPlaceholderData}
          className={`w-full text-left text-sm ${isPlaceholderData ? 'opacity-60' : ''}`}
        >
          <caption className="sr-only">Mídias por número de reproduções no período</caption>
          <thead>
            <tr className="border-border border-b">
              <th scope="col" className="py-2 pr-3 font-medium">
                #
              </th>
              <th scope="col" className="py-2 pr-3 font-medium">
                Título
              </th>
              <th scope="col" className="py-2 pr-3 font-medium">
                Artista
              </th>
              <th scope="col" className="py-2 pr-3 font-medium">
                Tipo
              </th>
              <th scope="col" className="py-2 text-right font-medium">
                Reproduções
              </th>
            </tr>
          </thead>
          <tbody>
            {data.map((linha, i) => (
              <tr key={linha.midiaId} className="border-border border-b last:border-b-0">
                <td className="text-text-muted py-2 pr-3">{i + 1}</td>
                <td className="py-2 pr-3 break-words">{linha.titulo ?? '—'}</td>
                <td className="py-2 pr-3 break-words">{linha.artista ?? '—'}</td>
                <td className="py-2 pr-3">{NOMES_DO_TIPO[linha.tipoMidia]}</td>
                <td className="py-2 text-right tabular-nums">{linha.reproducoes}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </section>
  )
}
