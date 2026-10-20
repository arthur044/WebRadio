import { useState, type FormEvent } from 'react'
import { apiFetchComEtag } from '../../shared/api/httpClient'
import { descreverErro, semVersao, type ErroDescrito } from '../../shared/api/descreverErro'
import type { ProgramaDto, ProgramaRequest } from '../../shared/api/types'
import { campoLocalBrasiliaParaUtc, utcParaCampoLocalBrasilia } from '../../shared/lib/timezone'
import { ConflitoDeGrade } from './ConflitoDeGrade'
import type { LocutorOpcao } from './useLocutores'

const HORA_MS = 3_600_000
const MINUTO_MS = 60_000
const DURACAO_MINIMA_MS = 5 * MINUTO_MS
const DURACAO_MAXIMA_MS = 12 * HORA_MS

type Campo = 'titulo' | 'descricao' | 'locutorId' | 'inicioUtc' | 'fimUtc'
type ErrosDeCampo = Partial<Record<Campo, string>>

interface ProgramaFormProps {
  /** `null` = programa novo. */
  programa: ProgramaDto | null
  /** ETag lido no `GET /programas/{id}`; vai no `If-Match` do `PUT`. `null` no programa novo. */
  etag: string | null
  locutores: LocutorOpcao[]
  /** Aviso que sobreviveu a uma recarga (412, transição inválida): o formulário é remontado. */
  avisoInicial: string | null
  onSalvo: (mensagem: string) => void
  onDescartar: () => void
  /** O que a tela mostra está velho: o pai relê o programa e remonta o formulário. */
  onRecarregar: (erro: ErroDescrito) => void
}

/** Próxima hora cheia de Brasília, no formato do `<input type="datetime-local">`. */
function proximaHoraCheia(): { inicio: string; fim: string } {
  const proxima = (Math.floor(Date.now() / HORA_MS) + 1) * HORA_MS
  return {
    inicio: utcParaCampoLocalBrasilia(new Date(proxima).toISOString()),
    fim: utcParaCampoLocalBrasilia(new Date(proxima + HORA_MS).toISOString()),
  }
}

/** As regras de texto e de horário do `01 §2.3`, para o Admin ver o erro sem uma ida à API. */
function validar(
  valores: { titulo: string; descricao: string; locutorId: string },
  inicioUtc: string | null,
  fimUtc: string | null,
): ErrosDeCampo {
  const erros: ErrosDeCampo = {}
  const titulo = valores.titulo.trim()
  if (titulo === '') erros.titulo = 'Informe o título.'
  else if (titulo.length > 120) erros.titulo = 'O título tem no máximo 120 caracteres.'
  if (valores.descricao.trim().length > 1000) {
    erros.descricao = 'A descrição tem no máximo 1000 caracteres.'
  }
  if (valores.locutorId === '') erros.locutorId = 'Escolha o locutor.'
  if (inicioUtc === null) erros.inicioUtc = 'Informe o início.'
  if (fimUtc === null) erros.fimUtc = 'Informe o fim.'

  if (inicioUtc !== null && fimUtc !== null) {
    const duracao = new Date(fimUtc).getTime() - new Date(inicioUtc).getTime()
    if (duracao <= 0) erros.fimUtc = 'O fim precisa ser depois do início.'
    else if (duracao < DURACAO_MINIMA_MS || duracao > DURACAO_MAXIMA_MS) {
      erros.fimUtc = 'A duração precisa estar entre 5 minutos e 12 horas.'
    }
  }
  return erros
}

const entrada = 'border-border bg-surface-raised rounded-md border px-3 py-2'

export function ProgramaForm({
  programa,
  etag,
  locutores,
  avisoInicial,
  onSalvo,
  onDescartar,
  onRecarregar,
}: ProgramaFormProps) {
  const [padrao] = useState(() => (programa ? null : proximaHoraCheia()))
  const [titulo, setTitulo] = useState(programa?.titulo ?? '')
  const [descricao, setDescricao] = useState(programa?.descricao ?? '')
  const [locutorId, setLocutorId] = useState(programa?.locutor.id ?? '')
  const [inicio, setInicio] = useState(
    programa ? utcParaCampoLocalBrasilia(programa.inicioUtc) : padrao!.inicio,
  )
  const [fim, setFim] = useState(
    programa ? utcParaCampoLocalBrasilia(programa.fimUtc) : padrao!.fim,
  )
  const [erros, setErros] = useState<ErrosDeCampo>({})
  const [erro, setErro] = useState<ErroDescrito | null>(
    avisoInicial
      ? { mensagem: avisoInicial, campos: {}, conflitoComId: null, recarregar: false }
      : null,
  )
  const [enviando, setEnviando] = useState(false)

  // O locutor do programa continua selecionável mesmo que já não esteja na lista (desativado, rebaixado).
  const opcoes =
    programa && !locutores.some((l) => l.id === programa.locutor.id)
      ? [
          ...locutores,
          { id: programa.locutor.id, nome: programa.locutor.nome, role: 'Locutor' as const },
        ]
      : locutores
  const titulos = programa ? 'Editar programa' : 'Novo programa'

  async function enviar(e: FormEvent) {
    e.preventDefault()
    setErro(null)

    const inicioUtc = campoLocalBrasiliaParaUtc(inicio)
    const fimUtc = campoLocalBrasiliaParaUtc(fim)
    const locais = validar({ titulo, descricao, locutorId }, inicioUtc, fimUtc)
    setErros(locais)
    if (Object.keys(locais).length > 0 || inicioUtc === null || fimUtc === null) return

    if (programa && etag === null) {
      // Sem ETag não há como mandar o If-Match: não arrisca sobrescrever uma edição alheia.
      onRecarregar(semVersao())
      return
    }

    const corpo: ProgramaRequest = {
      titulo: titulo.trim(),
      descricao: descricao.trim() === '' ? null : descricao.trim(),
      locutorId,
      inicioUtc,
      fimUtc,
    }

    setEnviando(true)
    try {
      await apiFetchComEtag<ProgramaDto>(
        programa ? `/programas/${encodeURIComponent(programa.id)}` : '/programas',
        {
          method: programa ? 'PUT' : 'POST',
          headers: programa ? { 'If-Match': etag! } : undefined,
          body: corpo,
        },
      )
      onSalvo(programa ? 'Programa atualizado.' : 'Programa criado.')
    } catch (err) {
      const descrito = descreverErro(err)
      setErro(descrito)
      setErros(descrito.campos)
      if (descrito.recarregar) onRecarregar(descrito)
    } finally {
      setEnviando(false)
    }
  }

  function campo(nome: Campo) {
    return {
      'aria-invalid': erros[nome] ? true : undefined,
      'aria-describedby': erros[nome] ? `erro-${nome}` : undefined,
    } as const
  }

  function mensagemDoCampo(nome: Campo) {
    return erros[nome] ? (
      <p id={`erro-${nome}`} className="text-on-air text-xs">
        {erros[nome]}
      </p>
    ) : null
  }

  return (
    <form
      aria-label={titulos}
      onSubmit={enviar}
      noValidate
      className="border-border bg-surface-raised mt-4 flex flex-col gap-3 rounded-lg border p-4"
    >
      <h2 className="font-display text-lg font-bold">{titulos}</h2>

      {erro && (
        <div role="alert" className="text-on-air text-sm">
          <p>{erro.mensagem}</p>
          {erro.conflitoComId && <ConflitoDeGrade programaId={erro.conflitoComId} />}
        </div>
      )}

      <label className="flex flex-col gap-1 text-sm">
        Título
        <input
          value={titulo}
          onChange={(e) => setTitulo(e.target.value)}
          className={entrada}
          {...campo('titulo')}
        />
        {mensagemDoCampo('titulo')}
      </label>

      <label className="flex flex-col gap-1 text-sm">
        Descrição
        <textarea
          value={descricao}
          onChange={(e) => setDescricao(e.target.value)}
          rows={3}
          className={entrada}
          {...campo('descricao')}
        />
        {mensagemDoCampo('descricao')}
      </label>

      <label className="flex flex-col gap-1 text-sm">
        Locutor
        <select
          value={locutorId}
          onChange={(e) => setLocutorId(e.target.value)}
          className={entrada}
          {...campo('locutorId')}
        >
          <option value="" disabled>
            Selecione…
          </option>
          {opcoes.map((l) => (
            <option key={l.id} value={l.id}>
              {l.role === 'Admin' ? `${l.nome} (Admin)` : l.nome}
            </option>
          ))}
        </select>
        {mensagemDoCampo('locutorId')}
      </label>

      <div className="grid gap-3 sm:grid-cols-2">
        <label className="flex flex-col gap-1 text-sm">
          Início (horário de Brasília)
          <input
            type="datetime-local"
            value={inicio}
            onChange={(e) => setInicio(e.target.value)}
            className={entrada}
            {...campo('inicioUtc')}
          />
          {mensagemDoCampo('inicioUtc')}
        </label>
        <label className="flex flex-col gap-1 text-sm">
          Fim (horário de Brasília)
          <input
            type="datetime-local"
            value={fim}
            onChange={(e) => setFim(e.target.value)}
            className={entrada}
            {...campo('fimUtc')}
          />
          {mensagemDoCampo('fimUtc')}
        </label>
      </div>

      <div className="flex gap-2">
        <button
          type="submit"
          disabled={enviando}
          className="bg-accent text-accent-contrast rounded-md px-4 py-2 text-sm font-medium disabled:opacity-50"
        >
          {programa ? 'Salvar alterações' : 'Criar programa'}
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
