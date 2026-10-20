import { useState } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { apiFetch } from '../../shared/api/httpClient'
import { descreverErro, type ErroDescrito } from '../../shared/api/descreverErro'
import { formatarDataHoraBrasilia } from '../../shared/lib/timezone'
import { formatarIdade } from '../../shared/lib/idade'
import { GerarCredencialDialog } from './GerarCredencialDialog'
import { InstrucoesDeTransmissao } from './InstrucoesDeTransmissao'
import { useMinhaCredencial } from './useCredencialTransmissao'

const botao = 'border-border rounded-md border px-3 py-2 text-sm font-medium'

/** A credencial do próprio locutor: gerar (com a senha da conta), ver a idade e revogar. */
export function PainelDeTransmissao() {
  const queryClient = useQueryClient()
  const credencial = useMinhaCredencial()
  const [gerando, setGerando] = useState(false)
  const [confirmandoRevogacao, setConfirmandoRevogacao] = useState(false)
  const [revogando, setRevogando] = useState(false)
  const [erro, setErro] = useState<ErroDescrito | null>(null)
  const [aviso, setAviso] = useState<string | null>(null)

  function aoFecharDialog(gerou: boolean) {
    setGerando(false)
    if (gerou) {
      setAviso('Credencial gerada. A anterior, se havia, foi desativada.')
      void queryClient.invalidateQueries({ queryKey: ['credencial-transmissao'] })
    }
  }

  async function revogar() {
    setConfirmandoRevogacao(false)
    setRevogando(true)
    setErro(null)
    setAviso(null)
    try {
      await apiFetch<void>('/locutores/me/credencial-transmissao', { method: 'DELETE' })
      setAviso('Credencial revogada. Quem estiver no ar cai em até 15 segundos.')
    } catch (err) {
      setErro(descreverErro(err))
    } finally {
      setRevogando(false)
      void queryClient.invalidateQueries({ queryKey: ['credencial-transmissao'] })
    }
  }

  const ativa = credencial.data ?? null

  return (
    <section aria-labelledby="titulo-credencial" className="border-border rounded-lg border p-4">
      <h2 id="titulo-credencial" className="font-display text-lg font-bold">
        Credencial de transmissão
      </h2>

      {aviso && (
        <p role="status" className="mt-2 text-sm">
          {aviso}
        </p>
      )}
      {erro && (
        <p role="alert" className="text-on-air mt-2 text-sm">
          {erro.mensagem}
        </p>
      )}
      {credencial.isPending && <p className="text-text-muted mt-2 text-sm">Carregando…</p>}
      {credencial.isError && (
        <p role="alert" className="text-on-air mt-2 text-sm">
          Não foi possível ler a credencial agora.
        </p>
      )}

      {credencial.isSuccess && !ativa && (
        <p className="text-text-muted mt-2 text-sm">
          Você ainda não tem credencial. Gere uma para transmitir pelo BUTT ou pelo Mixxx.
        </p>
      )}

      {ativa && (
        <div className="mt-3 flex flex-col gap-4">
          <p className="text-sm">
            Criada {formatarIdade(ativa.criadaEmUtc)} ({formatarDataHoraBrasilia(ativa.criadaEmUtc)}
            ).{' '}
            {ativa.ultimoUsoEmUtc
              ? `Último uso ${formatarIdade(ativa.ultimoUsoEmUtc)}.`
              : 'Ainda não foi usada.'}
          </p>
          <InstrucoesDeTransmissao identificador={ativa.identificador} />
        </div>
      )}

      <div className="mt-4 flex flex-wrap items-center gap-2">
        <button
          type="button"
          onClick={() => {
            setAviso(null)
            setErro(null)
            setGerando(true)
          }}
          className="bg-accent text-accent-contrast rounded-md px-3 py-2 text-sm font-medium"
        >
          {ativa ? 'Gerar nova credencial' : 'Gerar credencial'}
        </button>
        {ativa && !confirmandoRevogacao && (
          <button
            type="button"
            className={botao}
            disabled={revogando}
            onClick={() => setConfirmandoRevogacao(true)}
          >
            Revogar
          </button>
        )}
        {ativa && confirmandoRevogacao && (
          <span className="flex items-center gap-2 text-sm">
            Revogar a credencial?
            <button type="button" className={`${botao} text-on-air`} onClick={() => void revogar()}>
              Confirmar revogação
            </button>
            <button type="button" className={botao} onClick={() => setConfirmandoRevogacao(false)}>
              Manter
            </button>
          </span>
        )}
      </div>

      {gerando && <GerarCredencialDialog onFechar={aoFecharDialog} />}
    </section>
  )
}
