import { useState } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { descreverErro, type ErroDescrito } from '../../shared/api/descreverErro'
import { formatarIdade } from '../../shared/lib/idade'
import { chaveDoAlerta, useAlertasHarbor } from '../../shared/api/alertasHarborStore'
import { revogarCredencialDe, useCredenciaisAtivas } from './useCredencialTransmissao'

const botao = 'border-border rounded-md border px-2.5 py-1 text-xs font-medium'

/** Admin: credenciais ativas de todos, com revogação (`DELETE /usuarios/{id}/credencial-transmissao`). */
export function CredenciaisAtivasAdmin() {
  const queryClient = useQueryClient()
  const lista = useCredenciaisAtivas(true)
  const [confirmando, setConfirmando] = useState<string | null>(null)
  const [erro, setErro] = useState<ErroDescrito | null>(null)
  const alertas = useAlertasHarbor((s) => s.alertas)

  async function revogar(usuarioId: string) {
    setConfirmando(null)
    setErro(null)
    try {
      await revogarCredencialDe(usuarioId)
      useAlertasHarbor
        .getState()
        .dispensar(chaveDoAlerta({ tipo: 'credencial', locutorId: usuarioId }))
    } catch (err) {
      setErro(descreverErro(err))
    } finally {
      void queryClient.invalidateQueries({ queryKey: ['credenciais-transmissao'] })
      void queryClient.invalidateQueries({ queryKey: ['credencial-transmissao'] })
    }
  }

  return (
    <section aria-labelledby="titulo-credenciais" className="border-border rounded-lg border p-4">
      <h2 id="titulo-credenciais" className="font-display text-lg font-bold">
        Credenciais ativas (Admin)
      </h2>

      {erro && (
        <p role="alert" className="text-on-air mt-2 text-sm">
          {erro.mensagem}
        </p>
      )}
      {lista.isPending && <p className="text-text-muted mt-2 text-sm">Carregando…</p>}
      {lista.isError && (
        <p role="alert" className="text-on-air mt-2 text-sm">
          Não foi possível listar as credenciais agora.
        </p>
      )}
      {lista.data && lista.data.length === 0 && (
        <p className="text-text-muted mt-2 text-sm">Nenhuma credencial ativa.</p>
      )}

      {lista.data && lista.data.length > 0 && (
        <ul className="mt-2">
          {lista.data.map((c) => {
            const alerta = alertas[chaveDoAlerta({ tipo: 'credencial', locutorId: c.usuarioId })]
            const emAlerta = alerta?.credencialAtiva === true
            return (
              <li
                key={c.usuarioId}
                data-alerta={emAlerta ? 'true' : undefined}
                className={`flex flex-wrap items-center gap-3 border-b py-2 last:border-b-0 ${
                  emAlerta ? 'border-on-air border-l-4 pl-2' : 'border-border'
                }`}
              >
                <div className="min-w-0 flex-1">
                  <p className="truncate font-medium">{c.usuarioNome}</p>
                  {emAlerta && (
                    <p className="text-on-air text-xs font-medium">
                      {alerta.falhas} tentativas recusadas em {alerta.janelaMinutos} min.
                    </p>
                  )}
                  <p className="text-text-muted text-xs">
                    Criada {formatarIdade(c.criadaEmUtc)}.{' '}
                    {c.ultimoUsoEmUtc
                      ? `Último uso ${formatarIdade(c.ultimoUsoEmUtc)}.`
                      : 'Nunca usada.'}
                  </p>
                </div>
                {confirmando !== c.usuarioId ? (
                  <button
                    type="button"
                    className={botao}
                    aria-label={`Revogar a credencial de ${c.usuarioNome}`}
                    onClick={() => setConfirmando(c.usuarioId)}
                  >
                    Revogar
                  </button>
                ) : (
                  <span className="flex items-center gap-2 text-sm">
                    Revogar a credencial de {c.usuarioNome}?
                    <button
                      type="button"
                      className={`${botao} text-on-air`}
                      onClick={() => void revogar(c.usuarioId)}
                    >
                      Confirmar revogação
                    </button>
                    <button type="button" className={botao} onClick={() => setConfirmando(null)}>
                      Manter
                    </button>
                  </span>
                )}
              </li>
            )
          })}
        </ul>
      )}
    </section>
  )
}
