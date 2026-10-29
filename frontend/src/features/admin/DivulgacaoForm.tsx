import { useEffect, useId, useRef, useState, type ChangeEvent, type FormEvent } from 'react'
import { ApiError } from '../../shared/api/httpClient'
import { descreverErro, semVersao, type ErroDescrito } from '../../shared/api/descreverErro'
import type { DivulgacaoDto, DivulgacaoRequest } from '../../shared/api/types'
import { campoLocalBrasiliaParaUtc, utcParaCampoLocalBrasilia } from '../../shared/lib/timezone'
import { imagemSegura } from '../../shared/lib/urlSegura'
import { tamanhoDoTexto } from '../pedidos/limites'
import { enviarImagem, salvarDivulgacao } from './useDivulgacoes'

const IMAGEM_MAXIMA_BYTES = 2 * 1024 * 1024
const TIPOS_DE_IMAGEM = ['image/jpeg', 'image/png']

type Campo = 'titulo' | 'mensagem' | 'imagem' | 'linkDestino' | 'prioridade' | 'inicio' | 'fim'
type ErrosDeCampo = Partial<Record<Campo, string>>

/** O que o formulário vai mandar como `imagemChave`: nada, a que já existe (reenviada) ou um arquivo novo. */
type Imagem =
  | { tipo: 'nenhuma' }
  | { tipo: 'mantida'; chave: string; url: string | null }
  | { tipo: 'nova'; arquivo: File; previa: string }

interface DivulgacaoFormProps {
  /** `null` = divulgação nova. */
  divulgacao: DivulgacaoDto | null
  /** ETag lido no `GET /divulgacoes/{id}`; vai no `If-Match` do `PUT`. `null` na divulgação nova. */
  etag: string | null
  /** Aviso que sobreviveu a uma recarga (412): o formulário é remontado. */
  avisoInicial: string | null
  onSalvo: (mensagem: string) => void
  onDescartar: () => void
  /** O que a tela mostra está velho: o pai relê a divulgação e remonta o formulário. */
  onRecarregar: (erro: ErroDescrito) => void
}

const entrada = 'border-border bg-surface-raised rounded-md border px-3 py-2'

function linkInvalido(link: string): boolean {
  if (link.length > 2048 || /\s/.test(link)) return true
  try {
    return new URL(link).protocol !== 'https:'
  } catch {
    return true
  }
}

/** Mensagens do upload (E3-F06): o status diz o motivo, e o texto genérico do 400 não diria o que corrigir. */
function descreverErroDaImagem(err: unknown): ErroDescrito {
  const base = descreverErro(err)
  if (!(err instanceof ApiError)) return base
  if (err.status === 400) {
    return {
      ...base,
      mensagem:
        'A imagem não foi aceita. Use um JPEG ou PNG, sem animação, com no máximo 4096 px de lado.',
    }
  }
  if (err.status === 413) return { ...base, mensagem: 'A imagem passa de 2 MB.' }
  if (err.status === 429) {
    return {
      ...base,
      mensagem: 'Muitas imagens enviadas por hora (o limite é 30). Tente mais tarde.',
    }
  }
  return base
}

export function DivulgacaoForm({
  divulgacao,
  etag,
  avisoInicial,
  onSalvo,
  onDescartar,
  onRecarregar,
}: DivulgacaoFormProps) {
  const idBase = useId()
  const [titulo, setTitulo] = useState(divulgacao?.titulo ?? '')
  const [mensagem, setMensagem] = useState(divulgacao?.mensagem ?? '')
  const [link, setLink] = useState(divulgacao?.linkDestino ?? '')
  const [prioridade, setPrioridade] = useState(String(divulgacao?.prioridade ?? 0))
  const [inicio, setInicio] = useState(
    divulgacao?.inicioExibicaoUtc ? utcParaCampoLocalBrasilia(divulgacao.inicioExibicaoUtc) : '',
  )
  const [fim, setFim] = useState(
    divulgacao?.fimExibicaoUtc ? utcParaCampoLocalBrasilia(divulgacao.fimExibicaoUtc) : '',
  )
  const [ativo, setAtivo] = useState(divulgacao?.ativo ?? true)
  const [imagem, setImagem] = useState<Imagem>(
    divulgacao?.imagemChave
      ? { tipo: 'mantida', chave: divulgacao.imagemChave, url: divulgacao.imagemUrl }
      : { tipo: 'nenhuma' },
  )
  const [erros, setErros] = useState<ErrosDeCampo>({})
  const [erro, setErro] = useState<ErroDescrito | null>(
    avisoInicial
      ? {
          mensagem: avisoInicial,
          campos: {},
          conflitoComId: null,
          recarregar: false,
          esperarSegundos: null,
        }
      : null,
  )
  const [enviando, setEnviando] = useState(false)

  // O arquivo novo só sobe ao salvar (a `Manutencao` apaga imagem sem divulgação depois de 24 h). Se o upload deu certo
  // e o salvar falhou por rede ou validação, a tentativa seguinte reusa a chave: o limite é de 30 envios por hora.
  // No 412 o formulário é remontado e este ref se perde: o Admin escolhe o arquivo de novo, e a imagem já enviada fica
  // órfã até a `Manutencao` apagá-la.
  const jaEnviada = useRef<{ arquivo: File; chave: string } | null>(null)
  const previaAtual = useRef<string | null>(null)
  useEffect(
    () => () => {
      if (previaAtual.current) URL.revokeObjectURL(previaAtual.current)
    },
    [],
  )

  function escolherImagem(e: ChangeEvent<HTMLInputElement>) {
    const arquivo = e.target.files?.[0]
    e.target.value = ''
    if (!arquivo) return
    setErro(null)
    if (!TIPOS_DE_IMAGEM.includes(arquivo.type)) {
      setErros((v) => ({ ...v, imagem: 'Escolha uma imagem JPEG ou PNG.' }))
      return
    }
    if (arquivo.size > IMAGEM_MAXIMA_BYTES) {
      setErros((v) => ({ ...v, imagem: 'A imagem passa de 2 MB. Escolha uma menor.' }))
      return
    }
    if (previaAtual.current) URL.revokeObjectURL(previaAtual.current)
    const previa = URL.createObjectURL(arquivo)
    previaAtual.current = previa
    setErros((v) => ({ ...v, imagem: undefined }))
    setImagem({ tipo: 'nova', arquivo, previa })
  }

  function removerImagem() {
    if (previaAtual.current) URL.revokeObjectURL(previaAtual.current)
    previaAtual.current = null
    setErros((v) => ({ ...v, imagem: undefined }))
    setImagem({ tipo: 'nenhuma' })
  }

  function validar(inicioUtc: string | null, fimUtc: string | null): ErrosDeCampo {
    const e: ErrosDeCampo = {}
    const t = titulo.trim()
    if (t === '') e.titulo = 'Informe o título.'
    else if (tamanhoDoTexto(titulo) > 120) e.titulo = 'O título tem no máximo 120 caracteres.'
    const m = mensagem.trim()
    if (m === '') e.mensagem = 'Informe a mensagem.'
    else if (tamanhoDoTexto(mensagem) > 1000)
      e.mensagem = 'A mensagem tem no máximo 1000 caracteres.'
    const p = Number(prioridade)
    if (prioridade.trim() === '' || !Number.isInteger(p) || p < 0 || p > 100) {
      e.prioridade = 'A prioridade é um número inteiro de 0 a 100.'
    }
    if (link.trim() !== '' && linkInvalido(link.trim())) {
      e.linkDestino = 'O link precisa começar com https:// e não pode ter espaços.'
    }
    if (inicio !== '' && inicioUtc === null) e.inicio = 'Data de início inválida.'
    if (fim !== '' && fimUtc === null) e.fim = 'Data de fim inválida.'
    if (inicioUtc !== null && fimUtc !== null && new Date(fimUtc) <= new Date(inicioUtc)) {
      e.fim = 'O fim precisa ser depois do início.'
    }
    return e
  }

  async function chaveDaImagem(): Promise<string | null> {
    if (imagem.tipo === 'nenhuma') return null
    if (imagem.tipo === 'mantida') return imagem.chave
    if (jaEnviada.current?.arquivo === imagem.arquivo) return jaEnviada.current.chave
    const { chave } = await enviarImagem(imagem.arquivo)
    jaEnviada.current = { arquivo: imagem.arquivo, chave }
    return chave
  }

  async function enviar(e: FormEvent) {
    e.preventDefault()
    setErro(null)

    const inicioUtc = inicio === '' ? null : campoLocalBrasiliaParaUtc(inicio)
    const fimUtc = fim === '' ? null : campoLocalBrasiliaParaUtc(fim)
    const locais = validar(inicioUtc, fimUtc)
    setErros(locais)
    if (Object.keys(locais).length > 0) return

    if (divulgacao && etag === null) {
      // Sem ETag não há como mandar o If-Match: não arrisca sobrescrever uma edição alheia.
      onRecarregar(semVersao())
      return
    }

    setEnviando(true)
    let subindo = true
    try {
      const imagemChave = await chaveDaImagem()
      subindo = false
      const corpo: DivulgacaoRequest = {
        titulo: titulo.normalize('NFC').trim(),
        mensagem: mensagem.normalize('NFC').trim(),
        imagemChave,
        linkDestino: link.trim() === '' ? null : link.trim(),
        prioridade: Number(prioridade),
        inicioExibicaoUtc: inicioUtc,
        fimExibicaoUtc: fimUtc,
        ativo,
      }
      await salvarDivulgacao(divulgacao?.id ?? null, corpo, etag)
      onSalvo(divulgacao ? 'Divulgação atualizada.' : 'Divulgação criada.')
    } catch (err) {
      const descrito = subindo ? descreverErroDaImagem(err) : descreverErro(err)
      setErro(descrito)
      setErros({
        titulo: descrito.campos.titulo,
        mensagem: descrito.campos.mensagem,
        linkDestino: descrito.campos.linkDestino,
        prioridade: descrito.campos.prioridade,
        inicio: descrito.campos.inicioExibicaoUtc,
        fim: descrito.campos.fimExibicaoUtc,
        imagem: descrito.campos.imagemChave,
      })
      if (descrito.recarregar) onRecarregar(descrito)
    } finally {
      setEnviando(false)
    }
  }

  function campo(nome: Campo) {
    return {
      id: `${idBase}-${nome}`,
      'aria-invalid': erros[nome] ? true : undefined,
      'aria-describedby': erros[nome] ? `${idBase}-${nome}-erro` : undefined,
    } as const
  }

  function mensagemDoCampo(nome: Campo) {
    return erros[nome] ? (
      <p id={`${idBase}-${nome}-erro`} className="text-on-air text-xs">
        {erros[nome]}
      </p>
    ) : null
  }

  const titulos = divulgacao ? 'Editar divulgação' : 'Nova divulgação'
  const previa =
    imagem.tipo === 'nova'
      ? imagem.previa
      : imagem.tipo === 'mantida'
        ? imagemSegura(imagem.url)
        : null

  return (
    <form
      aria-label={titulos}
      onSubmit={enviar}
      noValidate
      className="border-border bg-surface-raised mt-4 flex flex-col gap-3 rounded-lg border p-4"
    >
      <h2 className="font-display text-lg font-bold">{titulos}</h2>

      {erro && (
        <p role="alert" className="text-on-air text-sm">
          {erro.mensagem}
        </p>
      )}

      <div className="flex flex-col gap-1 text-sm">
        <label htmlFor={`${idBase}-titulo`}>Título</label>
        <input
          value={titulo}
          onChange={(e) => setTitulo(e.target.value)}
          className={entrada}
          {...campo('titulo')}
        />
        <span className="text-text-muted text-xs">{tamanhoDoTexto(titulo)}/120</span>
        {mensagemDoCampo('titulo')}
      </div>

      <div className="flex flex-col gap-1 text-sm">
        <label htmlFor={`${idBase}-mensagem`}>Mensagem</label>
        <textarea
          value={mensagem}
          onChange={(e) => setMensagem(e.target.value)}
          rows={4}
          className={entrada}
          {...campo('mensagem')}
        />
        <span className="text-text-muted text-xs">{tamanhoDoTexto(mensagem)}/1000</span>
        {mensagemDoCampo('mensagem')}
      </div>

      <div className="flex flex-col gap-1 text-sm">
        <label htmlFor={`${idBase}-imagem`}>Imagem (JPEG ou PNG, até 2 MB)</label>
        {previa && (
          <img
            src={previa}
            alt="Prévia da imagem da divulgação"
            className="aspect-video w-full max-w-xs rounded-md object-cover"
          />
        )}
        <input
          type="file"
          accept="image/jpeg,image/png"
          onChange={escolherImagem}
          className="text-xs"
          {...campo('imagem')}
        />
        {imagem.tipo !== 'nenhuma' && (
          <button
            type="button"
            onClick={removerImagem}
            className="border-border w-fit rounded-md border px-2.5 py-1 text-xs font-medium"
          >
            Remover imagem
          </button>
        )}
        {imagem.tipo === 'nova' && (
          <span className="text-text-muted text-xs">A imagem é enviada quando você salvar.</span>
        )}
        {mensagemDoCampo('imagem')}
      </div>

      <div className="flex flex-col gap-1 text-sm">
        <label htmlFor={`${idBase}-linkDestino`}>Link (opcional, só https://)</label>
        <input
          type="url"
          value={link}
          onChange={(e) => setLink(e.target.value)}
          className={entrada}
          autoComplete="off"
          {...campo('linkDestino')}
        />
        {mensagemDoCampo('linkDestino')}
      </div>

      <div className="flex flex-col gap-1 text-sm sm:max-w-40">
        <label htmlFor={`${idBase}-prioridade`}>Prioridade (0 a 100)</label>
        <input
          type="number"
          min={0}
          max={100}
          step={1}
          value={prioridade}
          onChange={(e) => setPrioridade(e.target.value)}
          className={entrada}
          {...campo('prioridade')}
        />
        {mensagemDoCampo('prioridade')}
      </div>

      <div className="grid gap-3 sm:grid-cols-2">
        <div className="flex flex-col gap-1 text-sm">
          <label htmlFor={`${idBase}-inicio`}>Início da exibição (horário de Brasília)</label>
          <input
            type="datetime-local"
            value={inicio}
            onChange={(e) => setInicio(e.target.value)}
            className={entrada}
            {...campo('inicio')}
          />
          {mensagemDoCampo('inicio')}
        </div>
        <div className="flex flex-col gap-1 text-sm">
          <label htmlFor={`${idBase}-fim`}>Fim da exibição (horário de Brasília)</label>
          <input
            type="datetime-local"
            value={fim}
            onChange={(e) => setFim(e.target.value)}
            className={entrada}
            {...campo('fim')}
          />
          {mensagemDoCampo('fim')}
        </div>
      </div>

      <label className="flex items-center gap-2 text-sm">
        <input type="checkbox" checked={ativo} onChange={(e) => setAtivo(e.target.checked)} />
        Ativa (aparece no Mural dentro da janela)
      </label>

      <div className="flex gap-2">
        <button
          type="submit"
          disabled={enviando}
          className="bg-accent text-accent-contrast rounded-md px-4 py-2 text-sm font-medium disabled:opacity-50"
        >
          {divulgacao ? 'Salvar alterações' : 'Criar divulgação'}
        </button>
        <button
          type="button"
          onClick={onDescartar}
          disabled={enviando}
          className="border-border rounded-md border px-4 py-2 text-sm font-medium"
        >
          Descartar
        </button>
      </div>
    </form>
  )
}
