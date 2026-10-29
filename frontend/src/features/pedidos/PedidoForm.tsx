import { useId, useState, type FormEvent } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { descreverErro, type ErroDescrito } from '../../shared/api/descreverErro'
import { apiFetch } from '../../shared/api/httpClient'
import type { PedidoDto, PedidoRequest } from '../../shared/api/types'
import { LIMITES_DO_PEDIDO, tamanhoDoTexto } from './limites'
import { formatarEspera, useEspera } from './useEspera'

type Campo = keyof typeof LIMITES_DO_PEDIDO
type Valores = Record<Campo, string>

const CAMPOS: { nome: Campo; rotulo: string; obrigatorio: boolean; autoComplete?: string }[] = [
  { nome: 'nomeOuvinte', rotulo: 'Seu nome', obrigatorio: true, autoComplete: 'nickname' },
  { nome: 'tituloMusica', rotulo: 'Música', obrigatorio: true },
  { nome: 'artista', rotulo: 'Artista', obrigatorio: true },
  { nome: 'mensagem', rotulo: 'Dedicatória (opcional)', obrigatorio: false },
]

const VAZIO: Valores = { nomeOuvinte: '', tituloMusica: '', artista: '', mensagem: '' }

const entrada = 'border-border bg-surface-raised rounded-md border px-3 py-2'

/** As regras de tamanho do `01 §2.4`, para o ouvinte ver o erro sem gastar o pedido de 5 minutos. */
function validar(valores: Valores): Partial<Record<Campo, string>> {
  const erros: Partial<Record<Campo, string>> = {}
  for (const { nome, rotulo, obrigatorio } of CAMPOS) {
    const texto = valores[nome].trim()
    if (obrigatorio && texto === '') erros[nome] = 'Preencha este campo.'
    else if (tamanhoDoTexto(valores[nome]) > LIMITES_DO_PEDIDO[nome]) {
      erros[nome] =
        `${rotulo.replace(' (opcional)', '')} aceita no máximo ${LIMITES_DO_PEDIDO[nome]} caracteres.`
    }
  }
  return erros
}

export function PedidoForm() {
  const queryClient = useQueryClient()
  const idBase = useId()
  const [valores, setValores] = useState<Valores>(VAZIO)
  const [erros, setErros] = useState<Partial<Record<Campo, string>>>({})
  const [erro, setErro] = useState<ErroDescrito | null>(null)
  const [enviado, setEnviado] = useState(false)
  const [enviando, setEnviando] = useState(false)
  const espera = useEspera()

  function alterar(nome: Campo, valor: string) {
    setValores((v) => ({ ...v, [nome]: valor }))
    setEnviado(false)
  }

  async function enviar(e: FormEvent) {
    e.preventDefault()
    setErro(null)
    setEnviado(false)

    const locais = validar(valores)
    setErros(locais)
    if (Object.keys(locais).length > 0) return

    const mensagem = valores.mensagem.normalize('NFC').trim()
    const corpo: PedidoRequest = {
      nomeOuvinte: valores.nomeOuvinte.normalize('NFC').trim(),
      tituloMusica: valores.tituloMusica.normalize('NFC').trim(),
      artista: valores.artista.normalize('NFC').trim(),
      mensagem: mensagem === '' ? null : mensagem,
    }

    setEnviando(true)
    try {
      await apiFetch<PedidoDto>('/pedidos', {
        method: 'POST',
        body: corpo,
        includeListenerId: true,
      })
      setValores(VAZIO)
      setEnviado(true)
      void queryClient.invalidateQueries({ queryKey: ['pedidos-meus'] })
    } catch (err) {
      const descrito = descreverErro(err)
      setErro(descrito)
      setErros(descrito.campos as Partial<Record<Campo, string>>)
      if (descrito.esperarSegundos !== null) espera.iniciar(descrito.esperarSegundos)
    } finally {
      setEnviando(false)
    }
  }

  const aguardando = espera.restante > 0

  return (
    <form
      aria-label="Pedir uma música"
      onSubmit={enviar}
      noValidate
      className="mt-4 flex max-w-md flex-col gap-3"
    >
      {enviado && (
        <p role="status" className="text-text text-sm">
          Pedido enviado! Acompanhe a resposta do locutor em &quot;Meus pedidos&quot;.
        </p>
      )}
      {/* Depois da contagem do 429 o aviso perde o sentido: o botão volta e o ouvinte pode tentar. */}
      {erro && (erro.esperarSegundos === null || aguardando) && (
        <p role="alert" className="text-on-air text-sm">
          {erro.mensagem}
        </p>
      )}

      {CAMPOS.map(({ nome, rotulo, autoComplete }) => {
        const limite = LIMITES_DO_PEDIDO[nome]
        const tamanho = tamanhoDoTexto(valores[nome])
        const idErro = `${idBase}-${nome}-erro`
        const idContador = `${idBase}-${nome}-contador`
        return (
          <div key={nome} className="flex flex-col gap-1 text-sm">
            <label htmlFor={`${idBase}-${nome}`}>{rotulo}</label>
            {/* Linha única de propósito (E3-D7): o servidor recusa quebra de linha no pedido. */}
            <input
              id={`${idBase}-${nome}`}
              type="text"
              value={valores[nome]}
              onChange={(e) => alterar(nome, e.target.value)}
              autoComplete={autoComplete ?? 'off'}
              className={entrada}
              aria-invalid={erros[nome] ? true : undefined}
              aria-describedby={erros[nome] ? `${idErro} ${idContador}` : idContador}
            />
            <span
              id={idContador}
              className={`text-xs ${tamanho > limite ? 'text-on-air' : 'text-text-muted'}`}
            >
              {tamanho}/{limite}
            </span>
            {erros[nome] && (
              <p id={idErro} className="text-on-air text-xs">
                {erros[nome]}
              </p>
            )}
          </div>
        )
      })}

      <button
        type="submit"
        disabled={enviando || aguardando}
        className="bg-accent text-accent-contrast rounded-md px-3 py-2 text-sm font-medium disabled:opacity-50"
      >
        {aguardando
          ? `Aguarde ${formatarEspera(espera.restante)} para pedir de novo`
          : 'Enviar pedido'}
      </button>
    </form>
  )
}
