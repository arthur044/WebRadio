/** Limites do `PedidoMusica` (01-dominio.md §2.4), em unidades UTF-16: o `nvarchar` e o `string.length` contam igual. */
export const LIMITES_DO_PEDIDO = {
  nomeOuvinte: 60,
  tituloMusica: 200,
  artista: 200,
  mensagem: 280,
} as const

/**
 * Tamanho como o servidor mede (E3-D7): depois de normalizar para NFC e tirar as pontas, em unidades UTF-16. Sem o NFC,
 * um texto decomposto (`e` + acento combinante) contaria mais aqui do que lá; sem o `trim`, um espaço no fim faria o
 * texto ser recusado aqui e aceito lá.
 */
export function tamanhoDoTexto(valor: string): number {
  return valor.normalize('NFC').trim().length
}
