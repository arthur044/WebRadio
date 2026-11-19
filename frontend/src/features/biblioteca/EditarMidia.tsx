import { useId, useState, type FormEvent } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { descreverErro } from '../../shared/api/descreverErro'
import type { MidiaDto } from '../../shared/api/types'
import { tamanhoDoTexto } from '../pedidos/limites'
import { TEXTO_MAXIMO } from './limites'
import { atualizarMidia, recarregarMidias, useMidiaParaEditar } from './useMidias'

const botao = 'border-border rounded-md border px-2.5 py-1 text-xs font-medium'
const campoCls = 'border-border bg-surface-raised rounded-md border px-3 py-2'

function Formulario({
  midia,
  etag,
  aoFechar,
  aoPrecisarRecarregar,
}: {
  midia: MidiaDto
  etag: string | null
  aoFechar: (mensagem?: string) => void
  aoPrecisarRecarregar: (aviso: string) => Promise<void>
}) {
  const queryClient = useQueryClient()
  const idTitulo = useId()
  const idArtista = useId()
  const idErro = useId()
  const [titulo, setTitulo] = useState(midia.titulo ?? '')
  const [artista, setArtista] = useState(midia.artista ?? '')
  const [erro, setErro] = useState<string | null>(null)
  const [campos, setCampos] = useState<Record<string, string>>({})
  const [salvando, setSalvando] = useState(false)

  async function salvar(e: FormEvent) {
    e.preventDefault()
    if (etag === null) {
      return setErro('Não foi possível confirmar a versão. Feche e abra a edição de novo.')
    }
    const t = titulo.normalize('NFC').trim()
    const a = artista.normalize('NFC').trim()
    if (midia.tipoMidia === 'Musica' && t === '') {
      return setCampos({ titulo: 'Informe o título da música.' })
    }
    if (tamanhoDoTexto(titulo) > TEXTO_MAXIMO || tamanhoDoTexto(artista) > TEXTO_MAXIMO) {
      return setErro(`Título e artista aceitam no máximo ${TEXTO_MAXIMO} caracteres.`)
    }
    setSalvando(true)
    setErro(null)
    setCampos({})
    try {
      await atualizarMidia(
        midia.id,
        { titulo: t === '' ? null : t, artista: a === '' ? null : a },
        etag,
      )
      void recarregarMidias(queryClient)
      aoFechar('Mídia atualizada.')
    } catch (err) {
      const descrito = descreverErro(err)
      if (descrito.recarregar) {
        // 412: outra pessoa salvou antes. O formulário volta com os dados de agora (e o ETag novo).
        await aoPrecisarRecarregar(descrito.mensagem)
        return
      }
      setCampos(descrito.campos)
      setErro(descrito.mensagem)
    } finally {
      setSalvando(false)
    }
  }

  return (
    <form
      role="dialog"
      aria-label={`Editar ${midia.titulo ?? midia.nomeOriginal}`}
      onSubmit={(e) => void salvar(e)}
      noValidate
      className="mt-2 flex flex-col gap-2 text-sm"
    >
      <label htmlFor={idTitulo}>Título{midia.tipoMidia === 'Musica' ? ' (obrigatório)' : ''}</label>
      <input
        id={idTitulo}
        type="text"
        value={titulo}
        onChange={(e) => setTitulo(e.target.value)}
        className={campoCls}
        aria-invalid={campos.titulo ? true : undefined}
        aria-describedby={campos.titulo ? `${idTitulo}-erro` : undefined}
      />
      {campos.titulo && (
        <p id={`${idTitulo}-erro`} className="text-on-air text-xs">
          {campos.titulo}
        </p>
      )}
      <label htmlFor={idArtista}>Artista</label>
      <input
        id={idArtista}
        type="text"
        value={artista}
        onChange={(e) => setArtista(e.target.value)}
        className={campoCls}
        aria-invalid={campos.artista ? true : undefined}
        aria-describedby={campos.artista ? `${idArtista}-erro` : undefined}
      />
      {campos.artista && (
        <p id={`${idArtista}-erro`} className="text-on-air text-xs">
          {campos.artista}
        </p>
      )}
      {erro && (
        <p id={idErro} role="alert" className="text-on-air text-xs">
          {erro}
        </p>
      )}
      <span className="flex gap-2">
        <button type="submit" disabled={salvando} className={botao}>
          Salvar
        </button>
        <button type="button" disabled={salvando} className={botao} onClick={() => aoFechar()}>
          Cancelar
        </button>
      </span>
    </form>
  )
}

/**
 * E4-P03: edita título e artista com `If-Match`. O ETag só vem do `GET /midias/{id}`; num 412 a mídia é relida e o
 * formulário reaparece com os dados de agora, avisando que algo mudou (nada é sobrescrito em silêncio).
 */
export function EditarMidia({
  id,
  aoFechar,
}: {
  id: string
  aoFechar: (mensagem?: string) => void
}) {
  const detalhe = useMidiaParaEditar(id)
  const [versao, setVersao] = useState(0)
  const [aviso, setAviso] = useState<string | null>(null)

  async function recarregar(mensagem: string) {
    setAviso(mensagem)
    await detalhe.refetch()
    setVersao((v) => v + 1)
  }

  if (detalhe.isPending) return <p className="text-text-muted mt-2 text-sm">Carregando…</p>
  if (detalhe.isError) {
    return (
      <div className="mt-2 flex flex-wrap items-center gap-2">
        <p role="alert" className="text-on-air text-sm">
          {descreverErro(detalhe.error).mensagem}
        </p>
        <button type="button" className={botao} onClick={() => aoFechar()}>
          Fechar
        </button>
      </div>
    )
  }
  return (
    <>
      {aviso && (
        <p role="alert" className="text-on-air mt-2 text-sm">
          {aviso}
        </p>
      )}
      <Formulario
        key={versao}
        midia={detalhe.data.data}
        etag={detalhe.data.etag}
        aoFechar={aoFechar}
        aoPrecisarRecarregar={recarregar}
      />
    </>
  )
}
