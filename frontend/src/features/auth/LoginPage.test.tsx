import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { useAuthStore } from '../../shared/api/authStore'
import { LoginPage } from './LoginPage'

function jsonResponse(status: number, body: unknown) {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  })
}

describe('LoginPage', () => {
  afterEach(() => {
    useAuthStore.getState().clearSession()
    vi.restoreAllMocks()
  })

  it('mostra sempre a mesma mensagem genérica, credencial errada ou conta bloqueada', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () =>
        jsonResponse(401, {
          type: '/erros/credenciais',
          title: 'e-mail ou senha inválidos',
          status: 401,
        }),
      ),
    )
    const user = userEvent.setup()
    render(
      <MemoryRouter>
        <LoginPage />
      </MemoryRouter>,
    )

    await user.type(screen.getByLabelText('E-mail'), 'a@a.com')
    await user.type(screen.getByLabelText('Senha'), 'qualquer')
    await user.click(screen.getByRole('button', { name: 'Entrar' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('E-mail ou senha inválidos')
  })

  it('não repete a chamada via refresh num login errado (Guardian M1)', async () => {
    const calls: string[] = []
    vi.stubGlobal(
      'fetch',
      vi.fn(async (url: string) => {
        calls.push(url)
        return jsonResponse(401, { type: '/erros/credenciais', title: 'invalido', status: 401 })
      }),
    )
    const user = userEvent.setup()
    render(
      <MemoryRouter>
        <LoginPage />
      </MemoryRouter>,
    )
    await user.type(screen.getByLabelText('E-mail'), 'a@a.com')
    await user.type(screen.getByLabelText('Senha'), 'errada')
    await user.click(screen.getByRole('button', { name: 'Entrar' }))

    await waitFor(() => expect(screen.getByRole('alert')).toBeInTheDocument())
    // Só a chamada de /auth/login — nunca um /auth/refresh disparado pelo 401.
    expect(calls).toHaveLength(1)
    expect(calls[0]).toContain('/auth/login')
  })

  it('mostra mensagem diferente pra 429 (limite de tentativas, não credencial)', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () =>
        jsonResponse(429, {
          type: '/erros/muitos-pedidos',
          title: 'muitas tentativas',
          status: 429,
        }),
      ),
    )
    const user = userEvent.setup()
    render(
      <MemoryRouter>
        <LoginPage />
      </MemoryRouter>,
    )
    await user.type(screen.getByLabelText('E-mail'), 'a@a.com')
    await user.type(screen.getByLabelText('Senha'), 'x')
    await user.click(screen.getByRole('button', { name: 'Entrar' }))

    expect(await screen.findByRole('alert')).toHaveTextContent(/muitas tentativas/i)
  })
})
