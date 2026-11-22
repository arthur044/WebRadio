import { describe, expect, it } from 'vitest'
import { formatarDuracao, formatarTamanho, mimeDeclarado, recusaLocal } from './limites'

const MB = 1024 * 1024
const arquivo = (name: string, type: string, size = 1000) => ({ name, type, size })

describe('mimeDeclarado', () => {
  it('usa o tipo do navegador quando ele está na lista aceita', () => {
    expect(mimeDeclarado(arquivo('a.bin', 'audio/flac'))).toBe('audio/flac')
    expect(mimeDeclarado(arquivo('a.wav', 'audio/x-wav'))).toBe('audio/x-wav')
  })

  it('cai para a extensão quando o navegador não informa o tipo', () => {
    expect(mimeDeclarado(arquivo('Som.MP3', ''))).toBe('audio/mpeg')
    expect(mimeDeclarado(arquivo('a.ogg', 'application/octet-stream'))).toBe('audio/ogg')
  })

  it('recusa formato fora da lista e nome sem extensão', () => {
    expect(mimeDeclarado(arquivo('a.m4a', 'audio/mp4'))).toBeNull()
    expect(mimeDeclarado(arquivo('semextensao', ''))).toBeNull()
    expect(mimeDeclarado(arquivo('lista.m3u', 'audio/x-mpegurl'))).toBeNull()
  })
})

describe('recusaLocal', () => {
  it('aceita um arquivo dentro do limite do tipo', () => {
    expect(recusaLocal(arquivo('a.mp3', 'audio/mpeg', 10 * MB), 'Vinheta')).toBeNull()
    expect(recusaLocal(arquivo('a.mp3', 'audio/mpeg', 250 * MB), 'Musica')).toBeNull()
  })

  it('recusa acima do limite de cada tipo (10 MB, 20 MB e 250 MB)', () => {
    expect(recusaLocal(arquivo('a.mp3', 'audio/mpeg', 10 * MB + 1), 'Vinheta')).toMatch(
      /vinheta aceita até 10 MB/,
    )
    expect(recusaLocal(arquivo('a.mp3', 'audio/mpeg', 21 * MB), 'Comercial')).toMatch(
      /comercial aceita até 20 MB/,
    )
    expect(recusaLocal(arquivo('a.mp3', 'audio/mpeg', 250 * MB + 1), 'Musica')).toMatch(
      /música aceita até 250 MB/,
    )
  })

  it('recusa arquivo vazio e formato não aceito', () => {
    expect(recusaLocal(arquivo('a.mp3', 'audio/mpeg', 0), 'Musica')).toBe('O arquivo está vazio.')
    expect(recusaLocal(arquivo('a.exe', 'application/x-msdownload'), 'Musica')).toMatch(
      /Formato não aceito/,
    )
  })
})

describe('formatação', () => {
  it('tamanho e duração', () => {
    expect(formatarTamanho(512)).toBe('1 KB')
    expect(formatarTamanho(1.5 * MB)).toBe('1,5 MB')
    expect(formatarTamanho(250 * MB)).toBe('250 MB')
    expect(formatarDuracao(95)).toBe('1:35')
    expect(formatarDuracao(59.6)).toBe('1:00')
  })
})
