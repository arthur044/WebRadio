import { render, screen } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import App from './App'
import { useAuthStore } from './shared/api/authStore'
import type { Role } from './shared/api/types'

function renderApp(rota = '/') {
  const queryClient = new QueryClient()
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter initialEntries={[rota]}>
        <App />
      </MemoryRouter>
    </QueryClientProvider>,
  )
}

function json(status: number, body: unknown) {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  })
}

/** O boot restaura a sessão por /auth/refresh: o papel escolhido (ou anônimo) volta daí. */
function entrarComo(role: Role | null) {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (url: string) => {
      if (url.endsWith('/auth/refresh')) {
        return role
          ? json(200, {
              accessToken: 't',
              expiraEmUtc: '2026-01-14T15:00:00Z',
              deveTrocarSenha: false,
              usuario: { id: 'u1', nome: 'Fulana', email: 'f@f.com', role, deveTrocarSenha: false },
            })
          : json(401, { type: '/erros/credenciais', title: 'x', status: 401 })
      }
      if (url.includes('/usuarios'))
        return json(200, { itens: [], total: 0, pagina: 1, tamanho: 100 })
      if (url.includes('/programas')) return json(200, [])
      return json(404, { type: '/erros/nao-encontrado', title: 'x', status: 404 })
    }),
  )
}

describe('App', () => {
  it('renders the Mural route by default', () => {
    renderApp()
    expect(screen.getByRole('heading', { name: 'Mural' })).toBeInTheDocument()
  })

  it('renders the persistent player bar', () => {
    renderApp()
    expect(screen.getByTestId('player-bar')).toBeInTheDocument()
  })

  describe('administração da grade', () => {
    // Como no boot real: sem sessão e "restaurando" até o /auth/refresh responder.
    function estadoDeBoot() {
      useAuthStore.getState().clearSession()
      useAuthStore.getState().setRestoring(true)
    }

    beforeEach(estadoDeBoot)

    afterEach(() => {
      estadoDeBoot()
      vi.restoreAllMocks()
    })

    it('Admin vê o item "Admin" no menu e abre /admin/grade', async () => {
      entrarComo('Admin')
      renderApp('/admin/grade')

      expect(await screen.findByRole('heading', { name: 'Administrar grade' })).toBeInTheDocument()
      expect(screen.getByRole('link', { name: 'Admin' })).toHaveAttribute('href', '/admin/grade')
    })

    it('Locutor não vê o item e recebe "Acesso restrito" em /admin/grade', async () => {
      entrarComo('Locutor')
      renderApp('/admin/grade')

      expect(await screen.findByRole('heading', { name: 'Acesso restrito' })).toBeInTheDocument()
      expect(screen.queryByRole('link', { name: 'Admin' })).not.toBeInTheDocument()
    })

    it('anônimo em /admin/grade vai para o login', async () => {
      entrarComo(null)
      renderApp('/admin/grade')

      expect(await screen.findByRole('heading', { name: 'Entrar' })).toBeInTheDocument()
      expect(screen.queryByRole('link', { name: 'Admin' })).not.toBeInTheDocument()
    })
  })
})
