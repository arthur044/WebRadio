import { useEffect, useId, useRef, useState } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { descreverErro, type ErroDescrito } from '../../shared/api/descreverErro'
import {
  chaveDoAlerta,
  useAlertasHarbor,
  type AlertaHarbor,
} from '../../shared/api/alertasHarborStore'
import { useAuthStore } from '../../shared/api/authStore'
import { formatarDataHoraBrasilia } from '../../shared/lib/timezone'
import { revogarCredencialDe, useCredenciaisAtivas } from './useCredencialTransmissao'

const botao = 'border-border rounded-md border px-2.5 py-1 text-xs font-medium'

/** `locutorNome` é texto do servidor: vai só como filho de elemento, nunca como HTML. */
function textoDoAdmin(a: AlertaHarbor): string {
  if (a.tipo === 'global') {
    return `${a.falhas} tentativas recusadas no ao vivo em ${a.janelaMinutos} min`
  }
  const nome = a.locutorNome ?? 'um locutor'
  if (a.credencialAtiva) {
    return `A credencial de transmissão de ${nome} teve ${a.falhas} tentativas recusadas em ${a.janelaMinutos} min. Confira o BUTT/Mixxx do locutor; se não for ele, revogue a credencial`
  }
  return `Uma credencial antiga de ${nome} teve ${a.falhas} tentativas recusadas em ${a.janelaMinutos} min. O BUTT/Mixxx do locutor provavelmente ainda usa o segredo anterior`
}

function textoDoLocutor(a: AlertaHarbor): string {
  if (a.credencialAtiva) {
    return `Sua credencial teve ${a.falhas} tentativas recusadas nos últimos ${a.janelaMinutos} min. Confira o segredo configurado no BUTT/Mixxx; se não foi você, revogue e gere outra`
  }
  return 'Seu BUTT/Mixxx está tentando conectar com uma credencial antiga, já revogada. Atualize-o com o identificador e o segredo atuais'
}

/** Admin: revogar a credencial do alerta, avisando quando a ativa nasceu depois dele. */
function RevogarDoAlerta({ alerta, chave }: { alerta: AlertaHarbor; chave: string }) {
  const queryClient = useQueryClient()
  const lista = useCredenciaisAtivas(true)
  const [confirmando, setConfirmando] = useState(false)
  const [revogando, setRevogando] = useState(false)
  const [verificando, setVerificando] = useState(false)
  const [naoConferiu, setNaoConferiu] = useState(false)
  const [erro, setErro] = useState<ErroDescrito | null>(null)
  const idPergunta = useId()
  const idAviso = useId()
  const revogarRef = useRef<HTMLButtonElement>(null)
  const manterRef = useRef<HTMLButtonElement>(null)
  const devolverFoco = useRef(false)

  // O botão "Revogar" sai do DOM ao abrir a confirmação: o foco vai para "Manter" (o lado seguro) e, ao fechar sem
  // revogar, volta para "Revogar" em vez de cair no <body>.
  useEffect(() => {
    if (confirmando) manterRef.current?.focus()
    else if (devolverFoco.current) {
      devolverFoco.current = false
      revogarRef.current?.focus()
    }
  }, [confirmando])

  function fecharConfirmacao() {
    devolverFoco.current = true
    setConfirmando(false)
  }

  const locutorId = alerta.locutorId ?? ''
  const nome = alerta.locutorNome ?? 'o locutor'
  const credencial = lista.data?.find((c) => c.usuarioId === locutorId)
  // Credencial gerada depois do alerta: as falhas vieram da anterior, e revogar agora derruba a nova.
  const criadaDepois =
    credencial !== undefined &&
    new Date(credencial.criadaEmUtc).getTime() > new Date(alerta.ocorridoEmUtc).getTime()

  // A lista pode ter mudado desde o aviso (credencial nova, outra revogação): relê antes de pedir a confirmação, para
  // o aviso de "criada depois" valer para a credencial de agora. Se a leitura falhar, abre mesmo assim, sem a conferência.
  async function abrirConfirmacao() {
    setVerificando(true)
    setNaoConferiu(false)
    const resultado = await lista.refetch()
    setVerificando(false)
    if (resultado.isError) setNaoConferiu(true)
    // Já não está ativa: a releitura troca o botão pelo texto "Essa credencial já não está ativa.".
    else if (!resultado.data?.some((c) => c.usuarioId === locutorId)) return
    setConfirmando(true)
  }

  async function revogar() {
    setRevogando(true)
    setErro(null)
    try {
      await revogarCredencialDe(locutorId)
      useAlertasHarbor.getState().dispensar(chave)
    } catch (err) {
      setErro(descreverErro(err))
      fecharConfirmacao()
    } finally {
      setRevogando(false)
      void queryClient.invalidateQueries({ queryKey: ['credenciais-transmissao'] })
      void queryClient.invalidateQueries({ queryKey: ['credencial-transmissao'] })
    }
  }

  if (lista.isError && !credencial && !confirmando) {
    return (
      <div className="mt-2 flex flex-wrap items-center gap-2">
        <p role="alert" className="text-on-air text-sm">
          {descreverErro(lista.error).mensagem}
        </p>
        <button type="button" className={botao} onClick={() => void lista.refetch()}>
          Tentar de novo
        </button>
      </div>
    )
  }

  if (lista.isSuccess && !credencial) {
    return <p className="text-text-muted mt-2 text-sm">Essa credencial já não está ativa.</p>
  }

  return (
    <div className="mt-2">
      {erro && (
        <p role="alert" className="text-on-air mb-2 text-sm">
          {erro.mensagem}
        </p>
      )}
      {!confirmando ? (
        <button
          ref={revogarRef}
          type="button"
          className={botao}
          disabled={!credencial || revogando || verificando}
          onClick={() => void abrirConfirmacao()}
        >
          {verificando ? 'Conferindo a credencial…' : `Revogar a credencial de ${nome}`}
        </button>
      ) : (
        <div
          role="alertdialog"
          aria-label={`Revogar a credencial de ${nome}`}
          aria-describedby={criadaDepois || naoConferiu ? `${idPergunta} ${idAviso}` : idPergunta}
          className="text-sm"
        >
          <p id={idPergunta}>
            Revogar a credencial de {nome}? Quem estiver no ar cai em até 15 segundos.
          </p>
          {criadaDepois && credencial && (
            <p id={idAviso} className="text-on-air mt-1 font-medium">
              Atenção: a credencial ativa foi criada depois deste alerta (
              {formatarDataHoraBrasilia(credencial.criadaEmUtc)}). Revogar agora derruba a
              credencial nova, não a que gerou as falhas.
            </p>
          )}
          {naoConferiu && (
            <p id={idAviso} className="text-on-air mt-1 font-medium">
              Não foi possível conferir a credencial ativa agora. Se ela foi criada depois deste
              alerta, revogar derruba a credencial nova, não a que gerou as falhas.
            </p>
          )}
          <span className="mt-2 flex items-center gap-2">
            <button
              type="button"
              className={`${botao} text-on-air`}
              disabled={revogando}
              onClick={() => void revogar()}
            >
              Confirmar revogação
            </button>
            <button ref={manterRef} type="button" className={botao} onClick={fecharConfirmacao}>
              Manter
            </button>
          </span>
        </div>
      )}
    </div>
  )
}

/**
 * E2-P05: avisos do `AlertaHarborFalhas`. `para` diz de quem é o painel: o Admin vê todos (no layout, em qualquer
 * tela); o Locutor vê só o da própria credencial (no painel do Estúdio). Um aviso por chave; só na memória.
 */
export function AlertasDoHarbor({ para }: { para: 'Admin' | 'Locutor' }) {
  const usuario = useAuthStore((s) => s.usuario)
  const alertas = useAlertasHarbor((s) => s.alertas)
  if (!usuario || usuario.role !== para) return null

  const visiveis = Object.entries(alertas).filter(([, a]) =>
    para === 'Admin' ? true : a.tipo === 'credencial' && a.locutorId === usuario.id,
  )
  if (visiveis.length === 0) return null

  return (
    <div className="flex flex-col gap-2" aria-label="Avisos do ao vivo">
      {visiveis.map(([chave, a]) => (
        <div key={chave} role="alert" className="border-on-air rounded-lg border p-3">
          <div className="flex items-start gap-3">
            <p className="flex-1 text-sm">
              {para === 'Admin' ? textoDoAdmin(a) : textoDoLocutor(a)}
            </p>
            <button
              type="button"
              className={botao}
              onClick={() => useAlertasHarbor.getState().dispensar(chave)}
            >
              Dispensar
            </button>
          </div>
          {para === 'Admin' && a.tipo === 'credencial' && a.credencialAtiva && (
            <RevogarDoAlerta alerta={a} chave={chaveDoAlerta(a)} />
          )}
        </div>
      ))}
    </div>
  )
}
