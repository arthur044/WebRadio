import { useEffect, useId, useRef, useState } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { useAuthStore } from '../../shared/api/authStore'
import { descreverErro } from '../../shared/api/descreverErro'
import type { MidiaDto, StatusSanitizacao, TipoMidia } from '../../shared/api/types'
import { EnvioDeMidia } from './EnvioDeMidia'
import { useEnvios } from './enviosStore'
import { NOMES_DO_TIPO, TIPOS } from './limites'
import { MidiaItem } from './MidiaItem'
import { PreEscuta } from './PreEscuta'
import {
  FILTROS_INICIAIS,
  recarregarMidias,
  TAMANHO_DA_PAGINA,
  useMidias,
  type FiltrosDaBiblioteca,
} from './useMidias'

const botao = 'border-border rounded-md border px-2.5 py-1 text-xs font-medium'
const campoCls = 'border-border bg-surface-raised rounded-md border px-3 py-2'

const STATUS: { valor: StatusSanitizacao; nome: string }[] = [
  { valor: 'AguardandoUpload', nome: 'Aguardando o envio' },
  { valor: 'Pendente', nome: 'Na fila de análise' },
  { valor: 'EmAnalise', nome: 'Em análise' },
  { valor: 'Aprovado', nome: 'Aprovada' },
  { valor: 'Rejeitado', nome: 'Rejeitada' },
  { valor: 'Quarentena', nome: 'Em quarentena' },
]

const ATRASO_DA_BUSCA_MS = 300

/**
 * E4-P01: a biblioteca de mídia (Locutor e Admin). O status muda sozinho: o hub invalida `['midias']` no
 * `MidiaSanitizada` e ao reconectar (realtimeHub), então a lista relê por REST sem recarregar a página.
 */
export function BibliotecaPage() {
  const queryClient = useQueryClient()
  const ehAdmin = useAuthStore((s) => s.usuario?.role === 'Admin')
  const idTipo = useId()
  const idStatus = useId()
  const idAtivo = useId()
  const idBusca = useId()
  const [filtros, setFiltros] = useState<FiltrosDaBiblioteca>(FILTROS_INICIAIS)
  // A busca vai para a API só depois de uma pausa na digitação.
  const [textoBusca, setTextoBusca] = useState('')
  const [aviso, setAviso] = useState<string | null>(null)
  const [preEscuta, setPreEscuta] = useState<MidiaDto | null>(null)
  const { data, isPending, isError, error, refetch, isPlaceholderData } = useMidias(filtros)

  useEffect(() => {
    const id = window.setTimeout(
      () =>
        setFiltros((f) => (f.busca === textoBusca ? f : { ...f, busca: textoBusca, pagina: 1 })),
      ATRASO_DA_BUSCA_MS,
    )
    return () => window.clearTimeout(id)
  }, [textoBusca])

  // Cada envio que termina entra na lista como `Pendente`: relê (o hub só avisa quando a análise acaba).
  const enviados = useEnvios((s) => s.envios.filter((e) => e.estado.fase === 'enviado').length)
  const jaVistos = useRef(enviados)
  useEffect(() => {
    if (enviados > jaVistos.current) void recarregarMidias(queryClient)
    jaVistos.current = enviados
  }, [enviados, queryClient])

  function mudar(parcial: Partial<FiltrosDaBiblioteca>) {
    setFiltros((f) => ({ ...f, ...parcial, pagina: 1 }))
  }

  const totalDePaginas = data ? Math.max(1, Math.ceil(data.total / TAMANHO_DA_PAGINA)) : 1

  return (
    <section className="flex flex-col gap-4">
      <h1 className="font-display text-2xl font-bold">Biblioteca</h1>

      <EnvioDeMidia />

      <section aria-labelledby="titulo-lista" className="border-border rounded-lg border p-4">
        <h2 id="titulo-lista" className="font-display text-lg font-bold">
          Mídias
        </h2>

        <div role="search" className="mt-2 flex flex-wrap items-end gap-3 text-sm">
          <span className="flex flex-col gap-1">
            <label htmlFor={idBusca}>Buscar</label>
            <input
              id={idBusca}
              type="search"
              value={textoBusca}
              onChange={(e) => setTextoBusca(e.target.value)}
              placeholder="Título, artista ou arquivo"
              className={campoCls}
            />
          </span>
          <span className="flex flex-col gap-1">
            <label htmlFor={idTipo}>Tipo</label>
            <select
              id={idTipo}
              value={filtros.tipo}
              onChange={(e) => mudar({ tipo: e.target.value as TipoMidia | '' })}
              className={campoCls}
            >
              <option value="">Todos</option>
              {TIPOS.map((t) => (
                <option key={t} value={t}>
                  {NOMES_DO_TIPO[t]}
                </option>
              ))}
            </select>
          </span>
          <span className="flex flex-col gap-1">
            <label htmlFor={idStatus}>Status</label>
            <select
              id={idStatus}
              value={filtros.status}
              onChange={(e) => mudar({ status: e.target.value as StatusSanitizacao | '' })}
              className={campoCls}
            >
              <option value="">Todos</option>
              {STATUS.map((s) => (
                <option key={s.valor} value={s.valor}>
                  {s.nome}
                </option>
              ))}
            </select>
          </span>
          {ehAdmin && (
            <span className="flex flex-col gap-1">
              <label htmlFor={idAtivo}>Situação</label>
              <select
                id={idAtivo}
                value={filtros.ativo}
                onChange={(e) => mudar({ ativo: e.target.value as FiltrosDaBiblioteca['ativo'] })}
                className={campoCls}
              >
                <option value="todas">Todas</option>
                <option value="ativas">Ativas</option>
                <option value="desativadas">Desativadas</option>
              </select>
            </span>
          )}
        </div>

        {aviso && (
          <p role="status" className="text-text mt-3 text-sm">
            {aviso}
          </p>
        )}
        {preEscuta && (
          <PreEscuta key={preEscuta.id} midia={preEscuta} aoFechar={() => setPreEscuta(null)} />
        )}

        {isPending ? (
          <p className="text-text-muted mt-3 text-sm">Carregando…</p>
        ) : isError && !data ? (
          <div className="mt-3 flex flex-wrap items-center gap-2">
            <p role="alert" className="text-on-air text-sm">
              {descreverErro(error).mensagem}
            </p>
            <button type="button" className={botao} onClick={() => void refetch()}>
              Tentar de novo
            </button>
          </div>
        ) : data.itens.length === 0 ? (
          <p className="text-text-muted mt-3 text-sm">Nenhuma mídia encontrada.</p>
        ) : (
          <>
            <ul
              aria-live="polite"
              aria-busy={isPlaceholderData}
              className={`mt-2 ${isPlaceholderData ? 'opacity-60' : ''}`}
            >
              {data.itens.map((midia) => (
                <MidiaItem
                  key={midia.id}
                  midia={midia}
                  ehAdmin={ehAdmin}
                  preEscutando={preEscuta?.id === midia.id}
                  aoPreEscutar={setPreEscuta}
                  aoAvisar={setAviso}
                />
              ))}
            </ul>
            <nav
              aria-label="Páginas da biblioteca"
              className="mt-2 flex items-center gap-3 text-sm"
            >
              <button
                type="button"
                className={botao}
                disabled={filtros.pagina <= 1}
                onClick={() => setFiltros((f) => ({ ...f, pagina: f.pagina - 1 }))}
              >
                Anterior
              </button>
              <span className="text-text-muted">
                Página {filtros.pagina} de {totalDePaginas} · {data.total} mídias
              </span>
              <button
                type="button"
                className={botao}
                disabled={filtros.pagina >= totalDePaginas}
                onClick={() => setFiltros((f) => ({ ...f, pagina: f.pagina + 1 }))}
              >
                Próxima
              </button>
            </nav>
          </>
        )}
      </section>
    </section>
  )
}
