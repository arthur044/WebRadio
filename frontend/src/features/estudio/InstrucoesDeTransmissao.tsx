import type { ConexaoDeTransmissaoDto } from '../../shared/api/types'

interface Props {
  identificador: string
  /** Vem da API (só o dono vê). Sem host configurado, a tela manda pedir o endereço à administração. */
  conexao: ConexaoDeTransmissaoDto
  /** Só o diálogo de geração tem o segredo; fora dele a linha da senha explica onde ele está. */
  segredo?: string
}

function Linha({ rotulo, valor }: { rotulo: string; valor: string }) {
  return (
    <div className="flex flex-wrap gap-x-2">
      <dt className="text-text-muted w-24 shrink-0">{rotulo}</dt>
      <dd className="min-w-0 font-mono break-all">{valor}</dd>
    </div>
  )
}

/** Os campos que o BUTT e o Mixxx pedem. O "usuário" do source client é o identificador, não o e-mail. */
export function InstrucoesDeTransmissao({ identificador, conexao, segredo }: Props) {
  return (
    <div>
      <h3 className="font-display text-text-muted text-xs font-bold tracking-wide uppercase">
        Configurar o BUTT ou o Mixxx
      </h3>
      <dl className="mt-2 flex flex-col gap-1 text-sm">
        <Linha rotulo="Servidor" valor={conexao.host ?? 'peça o endereço à administração'} />
        <Linha rotulo="Porta" valor={String(conexao.porta)} />
        <Linha rotulo="Mount" valor={conexao.mount} />
        <Linha rotulo="Usuário" valor={identificador} />
        <Linha rotulo="Senha" valor={segredo ?? 'o segredo mostrado ao gerar'} />
      </dl>
      <p className="text-text-muted mt-2 text-xs">
        Tipo de servidor: Icecast/Shoutcast com “source” (BUTT: <em>Icecast</em>; Mixxx:{' '}
        <em>Icecast 2</em>). Formato MP3. Só conecta durante a janela do seu programa.
      </p>
    </div>
  )
}
