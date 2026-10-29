import { describe, expect, it } from 'vitest'
import { imagemSegura, linkSeguro, previaLocalSegura } from './urlSegura'

const PERIGOSAS = [
  'javascript:alert(1)',
  'JaVaScRiPt:alert(1)',
  ' javascript:alert(1)',
  'java\tscript:alert(1)',
  'data:text/html,<script>alert(1)</script>',
  'data:image/svg+xml,<svg onload=alert(1)>',
  'vbscript:msgbox(1)',
  'file:///etc/passwd',
  'blob:https://exemplo.com/x',
  '//evil.com/x',
  '/\\evil.com',
  'http://exemplo.com/', // só https
]

describe('linkSeguro', () => {
  it('aceita https', () => {
    expect(linkSeguro('https://exemplo.com/a?b=1')).toBe('https://exemplo.com/a?b=1')
  })

  it.each([...PERIGOSAS, '/caminho/relativo'])('rejeita %s', (url) => {
    expect(linkSeguro(url)).toBeNull()
  })

  it('rejeita vazio, nulo e inválido', () => {
    expect(linkSeguro(null)).toBeNull()
    expect(linkSeguro(undefined)).toBeNull()
    expect(linkSeguro('')).toBeNull()
    expect(linkSeguro('https://')).toBeNull()
  })
})

describe('imagemSegura', () => {
  it('aceita https e caminho same-origin', () => {
    expect(imagemSegura('https://cdn.exemplo.com/a.png')).toBe('https://cdn.exemplo.com/a.png')
    expect(imagemSegura('/uploads/a.png')).toBe(`${window.location.origin}/uploads/a.png`)
  })

  it.each(PERIGOSAS)('rejeita %s', (url) => {
    expect(imagemSegura(url)).toBeNull()
  })
})

describe('previaLocalSegura', () => {
  it('aceita só blob:', () => {
    expect(previaLocalSegura('blob:http://localhost/3f2a')).toBe('blob:http://localhost/3f2a')
  })

  it.each([
    'https://x.com/a.png',
    'data:image/png;base64,AAAA',
    'javascript:alert(1)',
    '/a.png',
    '',
  ])('recusa %s', (valor) => {
    expect(previaLocalSegura(valor)).toBeNull()
  })
})
