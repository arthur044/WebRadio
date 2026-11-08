import { useEffect, useId, useRef, useState, type FormEvent } from 'react'
import type { TipoMidia } from '../../shared/api/types'
import { formatarEspera, useEspera } from '../pedidos/useEspera'
import { tamanhoDoTexto } from '../pedidos/limites'
import { useEnvios, type Envio } from './enviosStore'
import {
  formatarTamanho,
  NOMES_DO_TIPO,
  recusaLocal,
  TAMANHO_MAXIMO_BYTES,
  TEXTO_MAXIMO,
  TIPOS,
} from './limites'

const botao = 'border-border rounded-md border px-2.5 py-1 text-xs font-medium'
const campoCls = 'border-border bg-surface-raised rounded-md border px-3 py-2'

function EsperaDoEnvio({ ateEm }: { ateEm: number }) {
  const espera = useEspera()
  const { iniciar } = espera
  useEffect(() => {
    iniciar(Math.max(1, Math.ceil((ateEm - Date.now()) / 1000)))
  }, [ateEm, iniciar])
  return (
    <p role="status" className="text-text-muted text-sm">
      Muitos envios em pouco tempo. Tenta de novo em {formatarEspera(espera.restante)}.
    </p>
  )
}

function ItemDoEnvio({ envio }: { envio: Envio }) {
  const cancelar = useEnvios((s) => s.cancelar)
  const tentarDeNovo = useEnvios((s) => s.tentarDeNovo)
  const { estado } = envio
  const nome = envio.titulo.trim() || envio.arquivo.name
  const emCurso = ['aguardando', 'preparando', 'enviando', 'espera'].includes(estado.fase)

  return (
    <li className="border-border flex flex-col gap-1 border-b py-2 text-sm last:border-b-0">
      <p className="font-medium break-words">
        {nome}{' '}
        <span className="text-text-muted font-normal">
          · {NOMES_DO_TIPO[envio.tipo]} · {formatarTamanho(envio.arquivo.size)}
        </span>
      </p>
      {estado.fase === 'aguardando' && <p className="text-text-muted">Na fila</p>}
      {estado.fase === 'preparando' && <p className="text-text-muted">Preparando o envio…</p>}
      {estado.fase === 'enviando' && (
        <label className="flex items-center gap-2">
          <span className="text-text-muted">Enviando</span>
          <progress
            max={100}
            value={Math.round(estado.progresso * 100)}
            aria-label={`Progresso do envio de ${nome}`}
            className="flex-1"
          />
          <span className="text-text-muted w-10 text-right">
            {Math.round(estado.progresso * 100)}%
          </span>
        </label>
      )}
      {estado.fase === 'concluindo' && <p className="text-text-muted">Concluindo…</p>}
      {estado.fase === 'espera' && <EsperaDoEnvio ateEm={estado.ateEm} />}
      {estado.fase === 'enviado' && (
        <p role="status" className="text-text">
          Enviado. A análise começou: o status aparece na lista abaixo.
        </p>
      )}
      {estado.fase === 'erro' && (
        <p role="alert" className="text-on-air">
          {estado.mensagem}
        </p>
      )}
      {estado.fase === 'cancelado' && <p className="text-text-muted">Cancelado.</p>}
      <span className="flex gap-2">
        {emCurso && estado.fase !== 'preparando' && (
          <button
            type="button"
            className={botao}
            aria-label={`Cancelar o envio de ${nome}`}
            onClick={() => cancelar(envio.id)}
          >
            Cancelar
          </button>
        )}
        {estado.fase === 'erro' && (
          <button
            type="button"
            className={botao}
            aria-label={`Tentar de novo o envio de ${nome}`}
            onClick={() => tentarDeNovo(envio.id)}
          >
            Tentar de novo
          </button>
        )}
      </span>
    </li>
  )
}

/**
 * E4-P02: formulário de envio e fila do browser. O arquivo é checado aqui (tamanho do tipo, formato) antes de pedir a
 * policy; o servidor confere tudo de novo e decide o formato real pelos magic bytes (S-C01).
 */
export function EnvioDeMidia() {
  const envios = useEnvios((s) => s.envios)
  const adicionar = useEnvios((s) => s.adicionar)
  const limparConcluidos = useEnvios((s) => s.limparConcluidos)
  const idTipo = useId()
  const idTitulo = useId()
  const idArtista = useId()
  const idArquivo = useId()
  const idErro = useId()
  const arquivoRef = useRef<HTMLInputElement>(null)
  const [tipo, setTipo] = useState<TipoMidia>('Musica')
  const [titulo, setTitulo] = useState('')
  const [artista, setArtista] = useState('')
  const [erro, setErro] = useState<string | null>(null)

  function enviar(e: FormEvent) {
    e.preventDefault()
    const arquivo = arquivoRef.current?.files?.[0]
    if (!arquivo) return setErro('Escolha o arquivo de áudio.')
    if (tipo === 'Musica' && titulo.trim() === '') return setErro('Informe o título da música.')
    if (tamanhoDoTexto(titulo) > TEXTO_MAXIMO || tamanhoDoTexto(artista) > TEXTO_MAXIMO) {
      return setErro(`Título e artista aceitam no máximo ${TEXTO_MAXIMO} caracteres.`)
    }
    const recusa = recusaLocal(arquivo, tipo)
    if (recusa) return setErro(recusa)

    setErro(null)
    adicionar({ arquivo, tipo, titulo, artista })
    setTitulo('')
    setArtista('')
    if (arquivoRef.current) arquivoRef.current.value = ''
  }

  const haConcluidos = envios.some((e) => ['enviado', 'erro', 'cancelado'].includes(e.estado.fase))

  return (
    <section aria-labelledby="titulo-envio" className="border-border rounded-lg border p-4">
      <h2 id="titulo-envio" className="font-display text-lg font-bold">
        Enviar áudio
      </h2>
      <form onSubmit={enviar} noValidate className="mt-2 flex flex-col gap-2 text-sm">
        <label htmlFor={idTipo}>Tipo</label>
        <select
          id={idTipo}
          value={tipo}
          onChange={(e) => setTipo(e.target.value as TipoMidia)}
          className={campoCls}
        >
          {TIPOS.map((t) => (
            <option key={t} value={t}>
              {NOMES_DO_TIPO[t]} (até {formatarTamanho(TAMANHO_MAXIMO_BYTES[t])})
            </option>
          ))}
        </select>

        <label htmlFor={idTitulo}>Título{tipo === 'Musica' ? ' (obrigatório)' : ''}</label>
        <input
          id={idTitulo}
          type="text"
          value={titulo}
          onChange={(e) => setTitulo(e.target.value)}
          className={campoCls}
        />

        <label htmlFor={idArtista}>Artista</label>
        <input
          id={idArtista}
          type="text"
          value={artista}
          onChange={(e) => setArtista(e.target.value)}
          className={campoCls}
        />

        <label htmlFor={idArquivo}>Arquivo (MP3, OGG, WAV ou FLAC)</label>
        <input
          ref={arquivoRef}
          id={idArquivo}
          type="file"
          accept=".mp3,.ogg,.wav,.flac,audio/mpeg,audio/ogg,audio/wav,audio/x-wav,audio/flac"
          className={campoCls}
          aria-describedby={erro ? idErro : undefined}
        />

        {erro && (
          <p id={idErro} role="alert" className="text-on-air text-xs">
            {erro}
          </p>
        )}
        <button
          type="submit"
          className="bg-accent text-accent-contrast w-fit rounded-md px-3 py-2 text-sm font-medium"
        >
          Enviar
        </button>
      </form>

      {envios.length > 0 && (
        <>
          <h3 className="font-display text-text-muted mt-4 text-xs font-bold tracking-wide uppercase">
            Envios
          </h3>
          <ul aria-live="polite">
            {envios.map((envio) => (
              <ItemDoEnvio key={envio.id} envio={envio} />
            ))}
          </ul>
          {haConcluidos && (
            <button type="button" className={`${botao} mt-2`} onClick={limparConcluidos}>
              Limpar concluídos
            </button>
          )}
        </>
      )}
    </section>
  )
}
