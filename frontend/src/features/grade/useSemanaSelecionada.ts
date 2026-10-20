import { useSearchParams } from 'react-router-dom'
import {
  chaveSegundaDaSemanaBrasilia,
  ehChaveDeSegunda,
  somarDias,
} from '../../shared/lib/timezone'

/**
 * Semana em exibição, guardada na URL (`?semana=YYYY-MM-DD`, sempre uma segunda-feira): o link da
 * semana é compartilhável e o botão Voltar do navegador anda pelas semanas. Sem o parâmetro, ou com
 * um valor que não é uma segunda-feira real, vale a semana atual de Brasília.
 */
export function useSemanaSelecionada() {
  const [params, setParams] = useSearchParams()
  const atual = chaveSegundaDaSemanaBrasilia()
  const pedida = params.get('semana')
  const segunda = pedida !== null && ehChaveDeSegunda(pedida) ? pedida : atual

  function irPara(chave: string) {
    setParams(chave === atual ? {} : { semana: chave })
  }

  return {
    segunda,
    ehSemanaAtual: segunda === atual,
    anterior: () => irPara(somarDias(segunda, -7)),
    proxima: () => irPara(somarDias(segunda, 7)),
    voltarParaAtual: () => irPara(atual),
  }
}
