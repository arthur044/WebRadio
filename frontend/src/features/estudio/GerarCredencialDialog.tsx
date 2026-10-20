import { useEffect, useRef, useState, type FormEvent } from 'react'
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
  const [senhaAtual, setSenhaAtual] = useState('')
  const [enviando, setEnviando] = useState(false)
  const [erro, setErro] = useState<ErroDescrito | null>(null)
  const [gerada, setGerada] = useState<CredencialGeradaDto | null>(null)
  const [copiado, setCopiado] = useState<'sim' | 'falhou' | null>(null)
  const senhaRef = useRef<HTMLInputElement>(null)
  const geradaRef = useRef(false)

  useEffect(() => {
    senhaRef.current?.focus()
  }, [])

  // Antes de gerar, Esc cancela. Depois, só o botão fecha: um Esc sem querer perderia um segredo que não volta.
  useEffect(() => {
    function aoTeclar(e: KeyboardEvent) {
      if (e.key === 'Escape' && !geradaRef.current) onFechar(false)
    }
    document.addEventListener('keydown', aoTeclar)
    return () => document.removeEventListener('keydown', aoTeclar)
  }, [onFechar])

  async function gerar(e: FormEvent) {
    e.preventDefault()
    if (enviando || senhaAtual === '') return
    setEnviando(true)
    setErro(null)
    try {
      // skipRefreshRetry: um 401 aqui é, em regra, senha errada. Repetir a chamada depois de um refresh gastaria
      // mais uma das 5 tentativas por hora (06 §Geração).
      const resposta = await apiFetch<CredencialGeradaDto>('/locutores/me/credencial-transmissao', {
        method: 'POST',
        body: { senhaAtual },
        skipRefreshRetry: true,
      })
      geradaRef.current = true
      setGerada(resposta)
    } catch (err) {
      setErro(descreverErro(err))
    } finally {
      setSenhaAtual('')
      setEnviando(false)
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
                className="border-border bg-surface rounded-md border px-3 py-2"
              />
            </label>
            {erro && (
              <p role="alert" className="text-on-air text-sm">
                {erro.mensagem}
              </p>
            )}
            <div className="flex justify-end gap-2">
              <button
                type="button"
                onClick={() => onFechar(false)}
                className="border-border rounded-md border px-3 py-2 text-sm font-medium"
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
