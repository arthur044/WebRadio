import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { useAuthStore } from '../../shared/api/authStore'
import { TrocarSenhaPage } from './TrocarSenhaPage'
import { LoginPage } from './LoginPage'

function jsonResponse(status: number, body: unknown) {
  return new Response(body === undefined ? null : JSON.stringify(body), {
    status,
    headers: body === undefined ? {} : { 'Content-Type': 'application/json' },
  })
}

function renderApp() {
  return render(
    <MemoryRouter initialEntries={['/trocar-senha']}>
      <Routes>
        <Route path="/trocar-senha" element={<TrocarSenhaPage />} />
        <Route path="/login" element={<LoginPage />} />
        <Route path="/" element={<div>Mural</div>} />
      </Routes>
    </MemoryRouter>,
  )
}

async function preencherEEnviar() {
  const user = userEvent.setup()
  await user.type(screen.getByLabelText('Senha atual'), 'senha-atual-123')
  await user.type(screen.getByLabelText(/Nova senha/), 'senha-nova-1234567')
  await user.click(screen.getByRole('button', { name: 'Trocar senha' }))
  return user
}

describe('TrocarSenhaPage — Guardian H3/N1 (204 -> refresh -> token novo)', () => {
  beforeEach(() => {
    useAuthStore.getState().setSession('token-provisorio', {
      id: '1',
      nome: 'Admin',
      email: 'admin@a.com',
      role: 'Admin',
      deveTrocarSenha: true,
    })
  })

  afterEach(() => {
    useAuthStore.getState().clearSession()
    vi.restoreAllMocks()
  })

  it('204 seguido de refresh bem-sucedido troca o token e navega pra /', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async (url: string) => {
        if (url.endsWith('/auth/trocar-senha')) return jsonResponse(204, undefined)
        if (url.endsWith('/auth/refresh')) {
          return jsonResponse(200, {
            accessToken: 'token-novo',
            expiraEmUtc: new Date().toISOString(),
            deveTrocarSenha: false,
            usuario: {
              id: '1',
              nome: 'Admin',
              email: 'admin@a.com',
              role: 'Admin',
              deveTrocarSenha: false,
            },
          })
        }
        throw new Error(`chamada inesperada: ${url}`)
      }),
    )

    renderApp()
    await preencherEEnviar()

    expect(await screen.findByText('Mural')).toBeInTheDocument()
    expect(useAuthStore.getState().accessToken).toBe('token-novo')
    expect(useAuthStore.getState().usuario?.deveTrocarSenha).toBe(false)
  })

  it('204 seguido de refresh que falha limpa a sessão e manda pra /login com aviso', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async (url: string) => {
        if (url.endsWith('/auth/trocar-senha')) return jsonResponse(204, undefined)
        if (url.endsWith('/auth/refresh')) {
          return jsonResponse(401, {
            type: '/erros/credenciais',
            title: 'sessão expirada',
            status: 401,
          })
        }
        throw new Error(`chamada inesperada: ${url}`)
      }),
    )

    renderApp()
    await preencherEEnviar()

    expect(await screen.findByRole('status')).toHaveTextContent('Senha trocada. Entre novamente.')
    expect(useAuthStore.getState().accessToken).toBeNull()
    expect(screen.queryByText('Mural')).not.toBeInTheDocument()
  })
})
