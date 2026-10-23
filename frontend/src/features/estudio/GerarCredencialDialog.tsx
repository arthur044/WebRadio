import { useEffect, useRef, useState, type FormEvent } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { apiFetch } from '../../shared/api/httpClient'
import { descreverErro, type ErroDescrito } from '../../shared/api/descreverErro'
import type { CredencialGeradaDto } from '../../shared/api/types'
import { InstrucoesDeTransmissao } from './InstrucoesDeTransmissao'

interface Props {
  /** Chamado ao fechar (nunca com o segredo) e, se algo foi gerado, `gerou` é true para o pai reler a credencial. */
  onFechar: (gerou: boolean) => void
}

/**
 * Gera a credencial de transmissão e mostra o segredo UMA vez (06 §Credencial). O segredo vive só neste
 * `useState`: a chamada não passa por `useMutation`/`useQuery` (o cache do TanStack guardaria a resposta) nem
 * por storage. O pai desmonta o diálogo ao fechar, e o estado vai junto.
 */
export function GerarCredencialDialog({ onFechar }: Props) {
  const queryClient = useQueryClient()
  const [senhaAtual, setSenhaAtual] = useState('')
  const [enviando, setEnviando] = useState(false)
  const [erro, setErro] = useState<ErroDescrito | null>(null)
  const [gerada, setGerada] = useState<CredencialGeradaDto | null>(null)
  const [copiado, setCopiado] = useState<'sim' | 'falhou' | null>(null)
  const senhaRef = useRef<HTMLInputElement>(null)
  const copiarRef = useRef<HTMLButtonElement>(null)
  const dialogoRef = useRef<HTMLDivElement>(null)
  const geradaRef = useRef(false)
  const enviandoRef = useRef(false)

  // Foco: entra no campo da senha, e ao fechar volta para quem abriu (o botão "Gerar" do painel).
  useEffect(() => {
    const abriu = document.activeElement instanceof HTMLElement ? document.activeElement : null
    senhaRef.current?.focus()
    return () => abriu?.focus()
  }, [])

  // O campo da senha some quando o segredo chega: o foco vai para "Copiar" em vez de cair no <body>.
  useEffect(() => {
    if (gerada) copiarRef.current?.focus()
  }, [gerada])

  // Antes de gerar, Esc cancela. Durante o envio e depois de gerar, só o fluxo normal fecha: com o POST em voo, fechar
  // perderia o segredo de uma credencial que já revogou a anterior; depois de gerar, perderia um segredo que não volta.
  // O Tab fica preso no diálogo (aria-modal não prende o foco sozinho).
  useEffect(() => {
    function aoTeclar(e: KeyboardEvent) {
      if (e.key === 'Escape' && !geradaRef.current && !enviandoRef.current) onFechar(false)
      if (e.key !== 'Tab') return
      const focaveis = dialogoRef.current?.querySelectorAll<HTMLElement>(
        'button:not([disabled]), input:not([disabled]), [href], [tabindex]:not([tabindex="-1"])',
      )
      if (!focaveis || focaveis.length === 0) return
      const primeiro = focaveis[0]
      const ultimo = focaveis[focaveis.length - 1]
      const ativo = document.activeElement
      if (!dialogoRef.current?.contains(ativo)) {
        e.preventDefault()
        primeiro.focus()
      } else if (e.shiftKey && ativo === primeiro) {
        e.preventDefault()
        ultimo.focus()
      } else if (!e.shiftKey && ativo === ultimo) {
        e.preventDefault()
        primeiro.focus()
      }
    }
    document.addEventListener('keydown', aoTeclar)
    return () => document.removeEventListener('keydown', aoTeclar)
  }, [onFechar])

  async function gerar(e: FormEvent) {
    e.preventDefault()
    if (enviando || senhaAtual === '') return
    setEnviando(true)
    enviandoRef.current = true
    setErro(null)
    try {
      // 401 de senha errada (/erros/credenciais) não é repetido: gastaria mais uma das 5 tentativas por hora
      // (06 §Geração). Já o 401 do token vencido não chega ao handler (não gasta tentativa): renova e repete.
      const resposta = await apiFetch<CredencialGeradaDto>('/locutores/me/credencial-transmissao', {
        method: 'POST',
        body: { senhaAtual },
        semRefreshSeCredenciais: true,
      })
      geradaRef.current = true
      setGerada(resposta)
    } catch (err) {
      setErro(descreverErro(err))
    } finally {
      setSenhaAtual('')
      enviandoRef.current = false
      setEnviando(false)
      // Um POST que saiu pode ter gerado a nova credencial e revogado a anterior, mesmo se a resposta se perdeu (rede,
      // timeout) ou o diálogo já desmontou: o painel relê o estado real. O QueryClient sobrevive ao componente.
      void queryClient.invalidateQueries({ queryKey: ['credencial-transmissao'] })
    }
  }

  async function copiar(segredo: string) {
    try {
      await navigator.clipboard.writeText(segredo)
      setCopiado('sim')
    } catch {
      setCopiado('falhou')
    }
  }

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/50 p-4">
      <div
        ref={dialogoRef}
        role="dialog"
        aria-modal="true"
        aria-labelledby="titulo-gerar-credencial"
        className="border-border bg-surface-raised text-text max-h-full w-full max-w-md overflow-y-auto rounded-lg border p-4"
      >
        <h2 id="titulo-gerar-credencial" className="font-display text-lg font-bold">
          {gerada ? 'Credencial gerada' : 'Gerar credencial de transmissão'}
        </h2>

        {!gerada && (
          <form onSubmit={(e) => void gerar(e)} className="mt-3 flex flex-col gap-3">
            <p className="text-text-muted text-sm">
              Gerar uma nova credencial desativa a anterior. Confirme com a senha da sua conta.
            </p>
            <label className="flex flex-col gap-1 text-sm">
              Senha da conta
              <input
                ref={senhaRef}
                type="password"
                autoComplete="current-password"
                value={senhaAtual}
                onChange={(e) => setSenhaAtual(e.target.value)}
                aria-invalid={erro?.campos.senhaAtual ? true : undefined}
                aria-describedby={erro?.campos.senhaAtual ? 'erro-senha-conta' : undefined}
                className="border-border bg-surface rounded-md border px-3 py-2"
              />
            </label>
            {/* O motivo do 400 (`/erros/validacao`) e o "Senha incorreta." do 401 vêm por campo; o texto genérico fica
                para o erro sem campo (429, rede, 5xx). */}
            {erro && (
              <p id="erro-senha-conta" role="alert" className="text-on-air text-sm">
                {erro.campos.senhaAtual ?? erro.mensagem}
              </p>
            )}
            <div className="flex justify-end gap-2">
              <button
                type="button"
                onClick={() => onFechar(false)}
                disabled={enviando}
                className="border-border rounded-md border px-3 py-2 text-sm font-medium disabled:opacity-50"
              >
                Cancelar
              </button>
              <button
                type="submit"
                disabled={enviando || senhaAtual === ''}
                className="bg-accent text-accent-contrast rounded-md px-3 py-2 text-sm font-medium disabled:opacity-50"
              >
                {enviando ? 'Gerando…' : 'Gerar'}
              </button>
            </div>
          </form>
        )}

        {gerada && (
          <div className="mt-3 flex flex-col gap-3">
            <p role="alert" className="text-on-air text-sm font-medium">
              Copie o segredo agora. Ele não será mostrado de novo: se perder, gere outra
              credencial.
            </p>
            <div>
              <p className="text-text-muted text-xs">Segredo (a senha do BUTT/Mixxx)</p>
              <div className="mt-1 flex items-center gap-2">
                <code
                  data-testid="segredo-gerado"
                  className="bg-surface border-border min-w-0 flex-1 rounded-md border px-2 py-1.5 font-mono text-sm break-all"
                >
                  {gerada.segredo}
                </code>
                <button
                  ref={copiarRef}
                  type="button"
                  onClick={() => void copiar(gerada.segredo)}
                  className="border-border shrink-0 rounded-md border px-3 py-2 text-sm font-medium"
                >
                  Copiar
                </button>
              </div>
              {copiado === 'sim' && (
                <p role="status" className="text-text-muted mt-1 text-xs">
                  Copiado.
                </p>
              )}
              {copiado === 'falhou' && (
                <p role="status" className="text-on-air mt-1 text-xs">
                  Não deu para copiar sozinho: selecione o texto e copie à mão.
                </p>
              )}
            </div>
            <InstrucoesDeTransmissao
              identificador={gerada.identificador}
              conexao={gerada.conexao}
              segredo={gerada.segredo}
            />
            <div className="flex justify-end">
              <button
                type="button"
                onClick={() => onFechar(true)}
                className="bg-accent text-accent-contrast rounded-md px-3 py-2 text-sm font-medium"
              >
                Já copiei, fechar
              </button>
            </div>
          </div>
        )}
      </div>
    </div>
  )
}
