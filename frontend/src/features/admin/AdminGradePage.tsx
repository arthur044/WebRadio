import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { apiFetch, apiFetchComEtag } from '../../shared/api/httpClient'
import { descreverErro, type ErroDescrito } from '../../shared/api/descreverErro'
import type { ProgramaDto, ProgramaStatus } from '../../shared/api/types'
import { formatarDiaBrasilia, formatarHoraBrasilia } from '../../shared/lib/timezone'
import { agruparPorDia } from '../grade/agruparPorDia'
import { SeletorDeSemana } from '../grade/SeletorDeSemana'
import { useGradeDaSemana } from '../grade/useGradeDaSemana'
import { useSemanaSelecionada } from '../grade/useSemanaSelecionada'
import { ProgramaForm } from './ProgramaForm'
import { useLocutores } from './useLocutores'

type Modo = { tipo: 'fechado' } | { tipo: 'novo' } | { tipo: 'editar'; id: string }

const ROTULO_STATUS: Record<ProgramaStatus, string> = {
  Agendado: 'Agendado',
  AoVivo: 'Ao vivo',
  Finalizado: 'Finalizado',
  Cancelado: 'Cancelado',
}

const botaoLinha = 'border-border rounded-md border px-2.5 py-1 text-xs font-medium'

export function AdminGradePage() {
  const queryClient = useQueryClient()
  const semana = useSemanaSelecionada()
  const grade = useGradeDaSemana(semana.segunda, true)
  const locutores = useLocutores()

  const [modo, setModo] = useState<Modo>({ tipo: 'fechado' })
  const [versaoDoForm, setVersaoDoForm] = useState(0)
  const [avisoDeRecarga, setAvisoDeRecarga] = useState<string | null>(null)
  const [sucesso, setSucesso] = useState<string | null>(null)
  const [erroDeAcao, setErroDeAcao] = useState<ErroDescrito | null>(null)
  const [confirmandoCancelamento, setConfirmandoCancelamento] = useState<string | null>(null)

  const idEmEdicao = modo.tipo === 'editar' ? modo.id : null
  // O ETag só vem do GET do programa. Cada abertura lê de novo (gcTime 0) e nada o relê sozinho
  // (foco da janela, reconexão): remontar o formulário no meio da digitação perderia o que o Admin escreveu.
  const detalhe = useQuery({
    queryKey: ['programa-detalhe', idEmEdicao],
    queryFn: () => apiFetchComEtag<ProgramaDto>(`/programas/${encodeURIComponent(idEmEdicao!)}`),
    enabled: idEmEdicao !== null,
    gcTime: 0,
    staleTime: Infinity,
    refetchOnWindowFocus: false,
    refetchOnReconnect: false,
    retry: false,
  })

  const cancelar = useMutation({
    mutationFn: (id: string) =>
      apiFetch<ProgramaDto>(`/programas/${encodeURIComponent(id)}/cancelar`, { method: 'POST' }),
  })

  function recarregarGrade() {
    return queryClient.invalidateQueries({ queryKey: ['grade-semana'] })
  }

  function abrir(novoModo: Modo) {
    setModo(novoModo)
    setSucesso(null)
    setErroDeAcao(null)
    setAvisoDeRecarga(null)
    setConfirmandoCancelamento(null)
  }

  function aoSalvar(mensagem: string) {
    setModo({ tipo: 'fechado' })
    setSucesso(mensagem)
    void recarregarGrade()
  }

  async function aoPrecisarRecarregar(erro: ErroDescrito) {
    setAvisoDeRecarga(erro.mensagem)
    void recarregarGrade()
    if (idEmEdicao !== null) {
      await detalhe.refetch()
      setVersaoDoForm((v) => v + 1)
    }
  }

  async function confirmarCancelamento(id: string) {
    setConfirmandoCancelamento(null)
    setSucesso(null)
    setErroDeAcao(null)
    try {
      await cancelar.mutateAsync(id)
      setSucesso('Programa cancelado.')
    } catch (err) {
      setErroDeAcao(descreverErro(err))
    } finally {
      void recarregarGrade()
    }
  }

  const programaLido = detalhe.data?.data ?? null
  // Entre o PUT que deu 412 e a releitura, o programa pode ter deixado de ser Agendado.
  const editavel = programaLido !== null && programaLido.status === 'Agendado'

  return (
    <section>
      <div className="flex flex-wrap items-center justify-between gap-2">
        <h1 className="font-display text-2xl font-bold">Administrar grade</h1>
        <button
          type="button"
          onClick={() => abrir({ tipo: 'novo' })}
          className="bg-accent text-accent-contrast rounded-md px-3 py-2 text-sm font-medium"
        >
          Novo programa
        </button>
      </div>

      {sucesso && (
        <p role="status" className="text-text mt-3 text-sm">
          {sucesso}
        </p>
      )}
      {erroDeAcao && (
        <p role="alert" className="text-on-air mt-3 text-sm">
          {erroDeAcao.mensagem}
        </p>
      )}
      {modo.tipo === 'editar' && detalhe.isError && (
        <p role="alert" className="text-on-air mt-3 text-sm">
          {descreverErro(detalhe.error).mensagem}
        </p>
      )}
      {modo.tipo === 'editar' && detalhe.isSuccess && !editavel && (
        <p role="alert" className="text-on-air mt-3 text-sm">
          Este programa não está mais agendado e não pode ser editado.
        </p>
      )}

      {modo.tipo === 'novo' && (
        <ProgramaForm
          key="novo"
          programa={null}
          etag={null}
          locutores={locutores.data ?? []}
          avisoInicial={null}
          onSalvo={aoSalvar}
          onDescartar={() => abrir({ tipo: 'fechado' })}
          onRecarregar={aoPrecisarRecarregar}
        />
      )}

      {modo.tipo === 'editar' && detalhe.isPending && (
        <p className="text-text-muted mt-4 text-sm">Carregando o programa…</p>
      )}
      {modo.tipo === 'editar' && editavel && (
        <ProgramaForm
          key={`${modo.id}-${versaoDoForm}`}
          programa={programaLido}
          etag={detalhe.data?.etag ?? null}
          locutores={locutores.data ?? []}
          avisoInicial={avisoDeRecarga}
          onSalvo={aoSalvar}
          onDescartar={() => abrir({ tipo: 'fechado' })}
          onRecarregar={aoPrecisarRecarregar}
        />
      )}

      <SeletorDeSemana
        segunda={semana.segunda}
        ehSemanaAtual={semana.ehSemanaAtual}
        onAnterior={semana.anterior}
        onProxima={semana.proxima}
        onAtual={semana.voltarParaAtual}
      />

      {grade.isPending && <p className="text-text-muted mt-4 text-sm">Carregando a programação…</p>}
      {grade.isError && (
        <p role="alert" className="text-on-air mt-4 text-sm">
          Não foi possível carregar a grade agora.
        </p>
      )}
      {grade.data && grade.data.length === 0 && (
        <p className="text-text-muted mt-4 text-sm">Nenhum programa nesta semana.</p>
      )}

      {grade.data && grade.data.length > 0 && (
        <div className="mt-4 flex flex-col gap-6">
          {agruparPorDia(grade.data).map(([chaveDia, programasDoDia]) => (
            <div key={chaveDia}>
              <h2 className="font-display text-text-muted text-xs font-bold tracking-wide uppercase">
                {formatarDiaBrasilia(chaveDia)}
              </h2>
              <ul className="border-border bg-surface-raised mt-2 rounded-lg border px-3">
                {programasDoDia.map((programa) => {
                  const agendado = programa.status === 'Agendado'
                  return (
                    <li
                      key={programa.id}
                      className={`border-border flex flex-wrap items-center gap-3 border-b py-2 last:border-b-0 ${
                        programa.status === 'Cancelado' ? 'opacity-50' : ''
                      }`}
                    >
                      <span className="font-display text-text-muted w-28 shrink-0 text-sm tabular-nums">
                        {formatarHoraBrasilia(programa.inicioUtc)}–
                        {formatarHoraBrasilia(programa.fimUtc)}
                      </span>
                      <div className="min-w-0 flex-1">
                        <p className="truncate font-medium">{programa.titulo}</p>
                        <p className="text-text-muted truncate text-xs">{programa.locutor.nome}</p>
                      </div>
                      <span className="text-text-muted text-xs">
                        {ROTULO_STATUS[programa.status]}
                      </span>
                      {agendado && confirmandoCancelamento !== programa.id && (
                        <span className="flex gap-2">
                          <button
                            type="button"
                            className={botaoLinha}
                            aria-label={`Editar ${programa.titulo}`}
                            onClick={() => abrir({ tipo: 'editar', id: programa.id })}
                          >
                            Editar
                          </button>
                          <button
                            type="button"
                            className={botaoLinha}
                            aria-label={`Cancelar ${programa.titulo}`}
                            onClick={() => {
                              setSucesso(null)
                              setErroDeAcao(null)
                              setConfirmandoCancelamento(programa.id)
                            }}
                          >
                            Cancelar
                          </button>
                        </span>
                      )}
                      {confirmandoCancelamento === programa.id && (
                        <span className="flex items-center gap-2 text-sm">
                          Cancelar este programa?
                          <button
                            type="button"
                            className={`${botaoLinha} text-on-air`}
                            disabled={cancelar.isPending}
                            onClick={() => void confirmarCancelamento(programa.id)}
                          >
                            Confirmar cancelamento
                          </button>
                          <button
                            type="button"
                            className={botaoLinha}
                            onClick={() => setConfirmandoCancelamento(null)}
                          >
                            Manter
                          </button>
                        </span>
                      )}
                    </li>
                  )
                })}
              </ul>
            </div>
          ))}
        </div>
      )}
    </section>
  )
}
