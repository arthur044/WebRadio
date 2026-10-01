import { describe, expect, it } from 'vitest'
import { urlHttpSegura } from './urlSegura'

describe('urlHttpSegura', () => {
  it('aceita http e https', () => {
    expect(urlHttpSegura('https://exemplo.com/a?b=1')).toBe('https://exemplo.com/a?b=1')
    expect(urlHttpSegura('http://exemplo.com/')).toBe('http://exemplo.com/')
  })

  it.each([
    'javascript:alert(1)',
    'JaVaScRiPt:alert(1)',
    ' javascript:alert(1)',
    'java\tscript:alert(1)',
    'data:text/html,<script>alert(1)</script>',
    'vbscript:msgbox(1)',
    'file:///etc/passwd',
    'blob:https://exemplo.com/x',
  ])('rejeita %s', (url) => {
    expect(urlHttpSegura(url)).toBeNull()
  })

  it('rejeita vazio, nulo e inválido', () => {
    expect(urlHttpSegura(null)).toBeNull()
    expect(urlHttpSegura(undefined)).toBeNull()
    expect(urlHttpSegura('')).toBeNull()
    expect(urlHttpSegura('http://')).toBeNull()
  })
})
