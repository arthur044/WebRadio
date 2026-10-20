import { useState } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { useAuthStore } from '../../shared/api/authStore'
import { apiFetch } from '../../shared/api/httpClient'
import { descreverErro, type ErroDescrito } from '../../shared/api/descreverErro'
import { useStreamInfo } from '../player/useStreamInfo'
import { useProgramaAoVivo } from '../grade/useProgramaAoVivo'

const botao = 'border-border rounded-md border px-3 py-2 text-sm font-medium'

/** Se há transmissão ao vivo (`TransmissaoAoVivoAlterada`) e o botão de encerrar o programa em curso. */
export function EstadoNoArPainel() {
  const queryClient = useQueryClient()
  const usuario = useAuthStore((s) => s.usuario)
  const info = useStreamInfo()
  const programa = useProgramaAoVivo()
  const [confirmando, setConfirmando] = useState(false)
  const [encerrando, setEncerrando] = useState(false)
  const [erro, setErro] = useState<ErroDescrito | null>(null)

  const noAr = info.data?.aoVivo?.noAr === true
  const emCurso = programa.data ?? null
  // O servidor também confere (403 para quem não é o dono); aqui só evita mostrar o botão a quem não pode usá-lo.
  const podeEncerrar =
    emCurso !== null && (usuario?.role === 'Admin' || emCurso.locutor.id === usuario?.id)

  async function encerrar() {
    if (!emCurso) return
    setConfirmando(false)
    setEncerrando(true)
    setErro(null)
    try {
      await apiFetch(`/programas/${encodeURIComponent(emCurso.id)}/encerrar`, { method: 'POST' })
    } catch (err) {
      setErro(descreverErro(err))
    } finally {
      setEncerrando(false)
      void queryClient.invalidateQueries({ queryKey: ['programa-ao-vivo'] })
      void queryClient.invalidateQueries({ queryKey: ['grade-semana'] })
    }
  }

  return (
    <section aria-labelledby="titulo-no-ar" className="border-border rounded-lg border p-4">
      <h2 id="titulo-no-ar" className="font-display text-lg font-bold">
        No ar
      </h2>

      <p className="mt-2 flex items-center gap-2 text-sm" data-testid="estado-no-ar">
        <span
          aria-hidden="true"
          className={`bg-on-air h-2.5 w-2.5 rounded-full ${noAr ? 'animate-pulse' : 'opacity-30'}`}
        />
        {noAr
          ? `Transmissão ao vivo${info.data?.aoVivo?.locutorNome ? ` com ${info.data.aoVivo.locutorNome}` : ''}.`
          : 'Ninguém está transmitindo ao vivo agora.'}
      </p>

      {emCurso ? (
        <p className="text-text-muted mt-1 text-sm">Programa em curso: {emCurso.titulo}</p>
      ) : (
        <p className="text-text-muted mt-1 text-sm">Nenhum programa em curso.</p>
      )}

      {erro && (
        <p role="alert" className="text-on-air mt-2 text-sm">
          {erro.mensagem}
        </p>
      )}

      {podeEncerrar && !confirmando && (
        <button
          type="button"
          className={`${botao} mt-3`}
          disabled={encerrando}
          onClick={() => setConfirmando(true)}
        >
          Encerrar programa
        </button>
      )}
      {podeEncerrar && confirmando && (
        <span className="mt-3 flex flex-wrap items-center gap-2 text-sm">
          Encerrar “{emCurso.titulo}” agora? A transmissão cai e a rádio volta para a rotação.
          <button type="button" className={`${botao} text-on-air`} onClick={() => void encerrar()}>
            Confirmar encerramento
          </button>
          <button type="button" className={botao} onClick={() => setConfirmando(false)}>
            Manter no ar
          </button>
        </span>
      )}
    </section>
  )
}
