import { QueryClient } from '@tanstack/react-query'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { useAlertasHarbor } from './alertasHarborStore'
import { useAuthStore } from './authStore'
import { ligarLimpezaDeCache, limparCachePrivado } from './limparCacheAoSair'
import type { UsuarioDto } from './types'

const admin: UsuarioDto = {
  id: 'a1',
  nome: 'Admin',
  email: 'a@a',
  role: 'Admin',
  deveTrocarSenha: false,
}

function clienteComCache() {
  const client = new QueryClient()
  client.setQueryData(['grade-semana', 'admin', '2026-01-12'], ['com cancelados'])
  client.setQueryData(['grade-semana', 'publica', '2026-01-12'], ['só o público'])
  client.setQueryData(['locutores'], ['Ana'])
  client.setQueryData(['pedidos-meus'], ['p1'])
  client.setQueryData(['programa-ao-vivo'], { titulo: 'No ar' })
  client.setQueryData(['stream-info'], {})
  client.setQueryData(['divulgacoes-ativas'], [])
  return client
}

describe('limparCacheAoSair', () => {
  beforeEach(() => useAuthStore.setState({ accessToken: null, usuario: null }))
  afterEach(() => useAuthStore.setState({ accessToken: null, usuario: null }))

  it('limparCachePrivado remove o que não é público e mantém o público', () => {
    const client = clienteComCache()

    limparCachePrivado(client)

    expect(client.getQueryData(['grade-semana', 'admin', '2026-01-12'])).toBeUndefined()
    expect(client.getQueryData(['locutores'])).toBeUndefined()
    expect(client.getQueryData(['pedidos-meus'])).toBeUndefined()
    expect(client.getQueryData(['grade-semana', 'publica', '2026-01-12'])).toBeDefined()
    expect(client.getQueryData(['programa-ao-vivo'])).toBeDefined()
    expect(client.getQueryData(['stream-info'])).toBeDefined()
    expect(client.getQueryData(['divulgacoes-ativas'])).toBeDefined()
  })

  it('limpa o cache privado quando o usuário sai', () => {
    const client = clienteComCache()
    useAuthStore.setState({ accessToken: 't', usuario: admin })
    const parar = ligarLimpezaDeCache(client)

    useAuthStore.getState().clearSession()

    expect(client.getQueryData(['locutores'])).toBeUndefined()
    expect(client.getQueryData(['grade-semana', 'publica', '2026-01-12'])).toBeDefined()
    parar()
  })

  it('limpa quando o papel muda com o mesmo id (Admin rebaixado a Locutor) e quando a renovação só reafirma o papel, não', () => {
    const client = clienteComCache()
    useAuthStore.setState({ accessToken: 't', usuario: admin })
    const parar = ligarLimpezaDeCache(client)

    useAuthStore.getState().setSession('t2', { ...admin, nome: 'Admin Renomeado' })
    expect(client.getQueryData(['locutores'])).toBeDefined()

    useAuthStore.getState().setSession('t3', { ...admin, role: 'Locutor' })
    expect(client.getQueryData(['locutores'])).toBeUndefined()
    expect(client.getQueryData(['stream-info'])).toBeDefined()
    parar()
  })

  it('limpa quando outro usuário entra no lugar', () => {
    const client = clienteComCache()
    useAuthStore.setState({ accessToken: 't', usuario: admin })
    const parar = ligarLimpezaDeCache(client)

    useAuthStore.getState().setSession('t2', { ...admin, id: 'l9', role: 'Locutor' })

    expect(client.getQueryData(['locutores'])).toBeUndefined()
    parar()
  })

  it('não limpa no login nem quando a sessão só renova o token do mesmo usuário', () => {
    const client = clienteComCache()
    const parar = ligarLimpezaDeCache(client)

    useAuthStore.getState().setSession('t1', admin)
    useAuthStore.getState().setSession('t2', admin)

    expect(client.getQueryData(['locutores'])).toBeDefined()
    parar()
  })
})

describe('avisos do harbor na troca de sessão', () => {
  afterEach(() => useAuthStore.setState({ accessToken: null, usuario: null }))

  it('saem da memória quando o usuário sai', () => {
    const parar = ligarLimpezaDeCache(new QueryClient())
    useAuthStore.setState({ accessToken: 't', usuario: admin })
    useAlertasHarbor.getState().registrar({
      tipo: 'global',
      falhas: 50,
      janelaMinutos: 5,
      ocorridoEmUtc: '2026-10-22T12:00:00Z',
    })

    useAuthStore.getState().clearSession()

    expect(useAlertasHarbor.getState().alertas).toEqual({})
    parar()
  })
})
