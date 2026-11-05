import { useState } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { descreverErro, type ErroDescrito } from '../../shared/api/descreverErro'
import type { DivulgacaoDto } from '../../shared/api/types'
import { formatarDataHoraBrasilia } from '../../shared/lib/timezone'
import { imagemSegura } from '../../shared/lib/urlSegura'
import { DivulgacaoForm } from './DivulgacaoForm'
import {
  recarregarDivulgacoes,
  removerDivulgacao,
  useDivulgacaoParaEditar,
  useListaDeDivulgacoes,
} from './useDivulgacoes'

type Modo = { tipo: 'fechado' } | { tipo: 'novo' } | { tipo: 'editar'; id: string }

const botaoLinha = 'border-border rounded-md border px-2.5 py-1 text-xs font-medium'

/** O que o público vê agora: `Ativo` e dentro da janela (01 §2.6), pelo relógio do navegador. */
function situacao(d: DivulgacaoDto, agora = Date.now()): string {
  if (!d.ativo) return 'Inativa'
  if (d.inicioExibicaoUtc && new Date(d.inicioExibicaoUtc).getTime() > agora) {
    return `Começa em ${formatarDataHoraBrasilia(d.inicioExibicaoUtc)}`
  }
  if (d.fimExibicaoUtc && new Date(d.fimExibicaoUtc).getTime() <= agora) return 'Expirada'
  return 'No ar'
}

function Miniatura({ divulgacao }: { divulgacao: DivulgacaoDto }) {
  const [quebrada, setQuebrada] = useState(false)
  const url = imagemSegura(divulgacao.imagemUrl)
  if (!divulgacao.imagemChave) return null
  // Imagem apagada pela Manutencao antes de ligar (E3-D5): o Admin vê o aviso e reenvia pelo "Editar".
  if (!url || quebrada) {
    return <span className="text-on-air text-xs">Imagem indisponível</span>
  }
  return (
    <img
      src={url}
      alt={`Imagem de ${divulgacao.titulo}`}
      loading="lazy"
      onError={() => setQuebrada(true)}
      className="h-12 w-20 shrink-0 rounded-md object-cover"
    />
  )
}

export function AdminDivulgacoesPage() {
  const queryClient = useQueryClient()
  const lista = useListaDeDivulgacoes()

  const [modo, setModo] = useState<Modo>({ tipo: 'fechado' })
  const [versaoDoForm, setVersaoDoForm] = useState(0)
  const [avisoDeRecarga, setAvisoDeRecarga] = useState<string | null>(null)
  const [sucesso, setSucesso] = useState<string | null>(null)
  const [erroDeAcao, setErroDeAcao] = useState<ErroDescrito | null>(null)
  const [removendo, setRemovendo] = useState<string | null>(null)
  const [apagando, setApagando] = useState(false)

  const idEmEdicao = modo.tipo === 'editar' ? modo.id : null
  const detalhe = useDivulgacaoParaEditar(idEmEdicao)

  function abrir(novoModo: Modo) {
    setModo(novoModo)
    setSucesso(null)
    setErroDeAcao(null)
    setAvisoDeRecarga(null)
    setRemovendo(null)
  }

  function aoSalvar(mensagem: string) {
    setModo({ tipo: 'fechado' })
    setSucesso(mensagem)
    void recarregarDivulgacoes(queryClient)
  }

  async function aoPrecisarRecarregar(erro: ErroDescrito) {
    setAvisoDeRecarga(erro.mensagem)
    void recarregarDivulgacoes(queryClient)
    if (idEmEdicao !== null) {
      await detalhe.refetch()
      setVersaoDoForm((v) => v + 1)
    }
  }

  async function confirmarRemocao(id: string) {
    setApagando(true)
    setSucesso(null)
    setErroDeAcao(null)
    try {
      await removerDivulgacao(id)
      setSucesso('Divulgação removida.')
    } catch (err) {
      setErroDeAcao(descreverErro(err))
    } finally {
      setApagando(false)
      setRemovendo(null)
      void recarregarDivulgacoes(queryClient)
    }
  }

  const lida = detalhe.data?.data ?? null

  return (
    <section>
      <div className="flex flex-wrap items-center justify-between gap-2">
        <h1 className="font-display text-2xl font-bold">Administrar divulgações</h1>
        <button
          type="button"
          onClick={() => abrir({ tipo: 'novo' })}
          className="bg-accent text-accent-contrast rounded-md px-3 py-2 text-sm font-medium"
        >
          Nova divulgação
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

      {modo.tipo === 'novo' && (
        <DivulgacaoForm
          key="novo"
          divulgacao={null}
          etag={null}
          avisoInicial={null}
          onSalvo={aoSalvar}
          onDescartar={() => abrir({ tipo: 'fechado' })}
          onRecarregar={aoPrecisarRecarregar}
        />
      )}

      {modo.tipo === 'editar' && detalhe.isPending && (
        <p className="text-text-muted mt-4 text-sm">Carregando a divulgação…</p>
      )}
      {modo.tipo === 'editar' && lida && (
        <DivulgacaoForm
          key={`${modo.id}-${versaoDoForm}`}
          divulgacao={lida}
          etag={detalhe.data?.etag ?? null}
          avisoInicial={avisoDeRecarga}
          onSalvo={aoSalvar}
          onDescartar={() => abrir({ tipo: 'fechado' })}
          onRecarregar={aoPrecisarRecarregar}
        />
      )}

      {lista.isPending && (
        <p className="text-text-muted mt-4 text-sm">Carregando as divulgações…</p>
      )}
      {lista.isError && (
        <p role="alert" className="text-on-air mt-4 text-sm">
          Não foi possível carregar as divulgações agora.
        </p>
      )}
      {lista.data && lista.data.itens.length === 0 && (
        <p className="text-text-muted mt-4 text-sm">Nenhuma divulgação cadastrada.</p>
      )}

      {lista.data && lista.data.itens.length > 0 && (
        <ul className="border-border bg-surface-raised mt-4 rounded-lg border px-3">
          {lista.data.itens.map((d) => (
            <li
              key={d.id}
              className="border-border flex flex-wrap items-center gap-3 border-b py-2 last:border-b-0"
            >
              <Miniatura divulgacao={d} />
              <div className="min-w-0 flex-1">
                <p className="truncate font-medium">{d.titulo}</p>
                <p className="text-text-muted truncate text-xs">
                  Prioridade {d.prioridade} · {situacao(d)}
                </p>
              </div>
              {removendo !== d.id ? (
                <span className="flex gap-2">
                  <button
                    type="button"
                    className={botaoLinha}
                    aria-label={`Editar ${d.titulo}`}
                    onClick={() => abrir({ tipo: 'editar', id: d.id })}
                  >
                    Editar
                  </button>
                  <button
                    type="button"
                    className={botaoLinha}
                    aria-label={`Remover ${d.titulo}`}
                    onClick={() => {
                      setSucesso(null)
                      setErroDeAcao(null)
                      setRemovendo(d.id)
                    }}
                  >
                    Remover
                  </button>
                </span>
              ) : (
                <span className="flex items-center gap-2 text-sm">
                  Remover esta divulgação?
                  <button
                    type="button"
                    className={`${botaoLinha} text-on-air`}
                    disabled={apagando}
                    onClick={() => void confirmarRemocao(d.id)}
                  >
                    Confirmar remoção
                  </button>
                  <button type="button" className={botaoLinha} onClick={() => setRemovendo(null)}>
                    Manter
                  </button>
                </span>
              )}
            </li>
          ))}
        </ul>
      )}
      {lista.data && lista.data.total > lista.data.itens.length && (
        <p className="text-text-muted mt-2 text-xs">
          Mostrando {lista.data.itens.length} de {lista.data.total}.
        </p>
      )}
    </section>
  )
}
