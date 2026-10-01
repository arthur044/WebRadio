import { useQuery } from '@tanstack/react-query'
import { apiFetch } from '../../shared/api/httpClient'
import type { DivulgacaoDto } from '../../shared/api/types'
import { urlHttpSegura } from '../../shared/lib/urlSegura'

/** Guardian M1-style: /divulgacoes/ativas é público, nunca dispara refresh em 401 — não tem 401 nessa rota. */
function useDivulgacoesAtivas() {
  return useQuery({
    queryKey: ['divulgacoes-ativas'],
    queryFn: () => apiFetch<DivulgacaoDto[]>('/divulgacoes/ativas'),
  })
}

function CardDivulgacao({ divulgacao }: { divulgacao: DivulgacaoDto }) {
  const imagemUrl = urlHttpSegura(divulgacao.imagemUrl)
  const linkDestino = urlHttpSegura(divulgacao.linkDestino)
  const conteudo = (
    <article className="border-border bg-surface-raised rounded-lg border p-4">
      {imagemUrl && (
        <img
          src={imagemUrl}
          alt=""
          className="mb-3 aspect-video w-full rounded-md object-cover"
          loading="lazy"
        />
      )}
      <h2 className="font-display text-lg font-bold">{divulgacao.titulo}</h2>
      <p className="text-text-muted mt-1 text-sm">{divulgacao.mensagem}</p>
    </article>
  )

  if (!linkDestino) return conteudo

  return (
    <a
      href={linkDestino}
      target="_blank"
      rel="noopener noreferrer"
      className="block rounded-lg transition-opacity hover:opacity-90"
    >
      {conteudo}
    </a>
  )
}

export function MuralPage() {
  const { data, isPending, isError } = useDivulgacoesAtivas()

  return (
    <section>
      <h1 className="font-display text-2xl font-bold">Mural</h1>

      {isPending && <p className="text-text-muted mt-4 text-sm">Carregando divulgações…</p>}

      {isError && (
        <p role="alert" className="text-on-air mt-4 text-sm">
          Não foi possível carregar o mural agora.
        </p>
      )}

      {data && data.length === 0 && (
        <p className="text-text-muted mt-4 text-sm">Nenhuma divulgação ativa no momento.</p>
      )}

      {data && data.length > 0 && (
        <div className="mt-4 grid grid-cols-1 gap-4 sm:grid-cols-2 lg:grid-cols-3">
          {data.map((divulgacao) => (
            <CardDivulgacao key={divulgacao.id} divulgacao={divulgacao} />
          ))}
        </div>
      )}
    </section>
  )
}
