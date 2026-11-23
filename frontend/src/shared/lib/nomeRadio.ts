/** Nome da rádio, da variável de build `VITE_NOME_RADIO` (E5-P01); o `vite.config.ts` usa o mesmo valor no `<title>`. */
export const NOME_RADIO: string = import.meta.env.VITE_NOME_RADIO?.trim() || 'WebRadio'
