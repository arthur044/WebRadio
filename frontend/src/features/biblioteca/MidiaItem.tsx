import { useEffect, useRef, useState } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { descreverErro } from '../../shared/api/descreverErro'
import type { MidiaDto, StatusSanitizacao } from '../../shared/api/types'
import { formatarDataHoraBrasilia } from '../../shared/lib/timezone'
import { EditarMidia } from './EditarMidia'
import { formatarDuracao, formatarTamanho, NOMES_DO_TIPO } from './limites'
import { alterarAtivo, excluirMidia, recarregarMidias } from './useMidias'

const botao = 'border-border rounded-md border px-2.5 py-1 text-xs font-medium'

const NOME_DO_STATUS: Record<StatusSanitizacao, string> = {
  AguardandoUpload: 'Aguardando o envio',
  Pendente: 'Na fila de análise',
  EmAnalise: 'Em análise',
  Aprovado: 'Aprovada',
  Rejeitado: 'Rejeitada',
  Quarentena: 'Em quarentena',
}

/** Cor só reforça o texto do status (nunca é a única pista). */
const COR_DO_STATUS: Record<StatusSanitizacao, string> = {
  AguardandoUpload: 'text-text-muted',
  Pendente: 'text-text-muted',
  EmAnalise: 'text-text-muted',
  Aprovado: 'text-accent',
  Rejeitado: 'text-on-air',
  Quarentena: 'text-on-air',
}

/**
 * Título, artista, nome do arquivo, quem enviou e o motivo da rejeição são texto de usuário (o motivo nasce do
 * sanitizer, mas o nome do arquivo é do remetente): só como filho de elemento, nunca como HTML.
 */
export function MidiaItem({
  midia,
  ehAdmin,
  preEscutando,
  aoPreEscutar,
  aoAvisar,
}: {
  midia: MidiaDto
  ehAdmin: boolean
  preEscutando: boolean
  aoPreEscutar: (midia: MidiaDto) => void
  aoAvisar: (mensagem: string) => void
}) {
  const queryClient = useQueryClient()
  const [editando, setEditando] = useState(false)
  const [excluindo, setExcluindo] = useState(false)
  const [ocupado, setOcupado] = useState(false)
  const [erro, setErro] = useState<string | null>(null)
  const editarRef = useRef<HTMLButtonElement>(null)
  const devolverFoco = useRef(false)

  // O botão "Editar" continua no DOM; ao fechar o formulário o foco volta a ele em vez de cair no <body>.
  useEffect(() => {
    if (!editando && devolverFoco.current) {
      devolverFoco.current = false
      editarRef.current?.focus()
    }
  }, [editando])

  const nome = midia.titulo ?? midia.nomeOriginal

  function fecharEdicao(mensagem?: string) {
    devolverFoco.current = true
    setEditando(false)
    if (mensagem) aoAvisar(mensagem)
  }

  async function agir(acao: () => Promise<void>) {
    setOcupado(true)
    setErro(null)
    try {
      await acao()
    } catch (err) {
      setErro(descreverErro(err).mensagem)
    } finally {
      setOcupado(false)
      setExcluindo(false)
      void recarregarMidias(queryClient)
    }
  }

  return (
    <li className="border-border flex flex-col gap-1 border-b py-3 last:border-b-0">
      <p className="font-medium break-words">
        {nome}
        {midia.artista && <span className="text-text-muted font-normal"> — {midia.artista}</span>}
      </p>
      <p className="text-text-muted text-xs break-words">
        {NOMES_DO_TIPO[midia.tipoMidia]}
        {midia.duracaoSegundos !== null && ` · ${formatarDuracao(midia.duracaoSegundos)}`}
        {` · ${formatarTamanho(midia.tamanhoBytes)}`}
        {midia.enviadoPorNome && ` · enviada por ${midia.enviadoPorNome}`}
        {` · ${formatarDataHoraBrasilia(midia.dataUploadUtc)}`}
      </p>
      {midia.titulo && (
        <p className="text-text-muted text-xs break-words">Arquivo: {midia.nomeOriginal}</p>
      )}
      <p className="text-sm">
        <span className={`font-medium ${COR_DO_STATUS[midia.statusSanitizacao]}`}>
          {NOME_DO_STATUS[midia.statusSanitizacao]}
        </span>
        {!midia.ativo && <span className="text-text-muted"> · Desativada</span>}
      </p>
      {midia.motivoRejeicao && (
        <p className="text-on-air text-sm break-words">Motivo: {midia.motivoRejeicao}</p>
      )}
      {erro && (
        <p role="alert" className="text-on-air text-sm">
          {erro}
        </p>
      )}

      {editando ? (
        <EditarMidia id={midia.id} aoFechar={fecharEdicao} />
      ) : excluindo ? (
        <span className="mt-1 flex flex-wrap items-center gap-2 text-sm">
          Excluir {nome}? Se já tocou, ela só será desativada.
          <button
            type="button"
            className={`${botao} text-on-air`}
            disabled={ocupado}
            onClick={() =>
              void agir(async () => {
                const resultado = await excluirMidia(midia.id)
                aoAvisar(
                  resultado === 'excluida'
                    ? `${nome} foi excluída.`
                    : `${nome} já tocou, então só foi desativada (o histórico fica).`,
                )
              })
            }
          >
            Confirmar exclusão
          </button>
          <button type="button" className={botao} onClick={() => setExcluindo(false)}>
            Manter
          </button>
        </span>
      ) : (
        <span className="mt-1 flex flex-wrap gap-2">
          {midia.statusSanitizacao === 'Aprovado' && (
            <button
              type="button"
              className={botao}
              aria-pressed={preEscutando}
              aria-label={`Pré-escutar ${nome}`}
              onClick={() => aoPreEscutar(midia)}
            >
              Pré-escutar
            </button>
          )}
          <button
            ref={editarRef}
            type="button"
            className={botao}
            aria-label={`Editar ${nome}`}
            onClick={() => {
              setErro(null)
              setEditando(true)
            }}
          >
            Editar
          </button>
          {ehAdmin && (
            <>
              <button
                type="button"
                className={botao}
                disabled={ocupado}
                aria-label={`${midia.ativo ? 'Desativar' : 'Reativar'} ${nome}`}
                onClick={() =>
                  void agir(async () => {
                    await alterarAtivo(midia.id, !midia.ativo)
                  })
                }
              >
                {midia.ativo ? 'Desativar' : 'Reativar'}
              </button>
              <button
                type="button"
                className={botao}
                aria-label={`Excluir ${nome}`}
                onClick={() => {
                  setErro(null)
                  setExcluindo(true)
                }}
              >
                Excluir
              </button>
            </>
          )}
        </span>
      )}
    </li>
  )
}
