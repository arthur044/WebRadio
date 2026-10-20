import { render, screen } from '@testing-library/react'
import { useEffect } from 'react'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { useAuthStore } from '../../shared/api/authStore'
import { apiFetch } from '../../shared/api/httpClient'
import { RequireRole } from './RequireRole'

function renderGuarded() {
  return render(
    <MemoryRouter initialEntries={['/estudio']}>
      <Routes>
        <Route path="/login" element={<div>Tela de login</div>} />
        <Route
          path="/estudio"
          element={
            <RequireRole roles={['Locutor', 'Admin']}>
              <div>Conteúdo do estúdio</div>
            </RequireRole>
          }
        />
      </Routes>
    </MemoryRouter>,
  )
}

describe('RequireRole', () => {
  beforeEach(() => {
    useAuthStore.getState().clearSession()
    useAuthStore.getState().setRestoring(false)
  })

  it('redireciona anônimo pro login', () => {
    renderGuarded()
    expect(screen.getByText('Tela de login')).toBeInTheDocument()
  })

  it('mostra 403 amigável pro Ouvinte, sem redirecionar', () => {
    useAuthStore.getState().setSession('t', {
      id: '1',
      nome: 'Fulano',
      email: 'a@a.com',
      role: 'Ouvinte',
      deveTrocarSenha: false,
    })
    renderGuarded()
    expect(screen.getByText('Acesso restrito')).toBeInTheDocument()
  })

  it('libera o Locutor', () => {
    useAuthStore.getState().setSession('t', {
      id: '1',
      nome: 'Fulano',
      email: 'a@a.com',
      role: 'Locutor',
      deveTrocarSenha: false,
    })
    renderGuarded()
    expect(screen.getByText('Conteúdo do estúdio')).toBeInTheDocument()
  })

  it('não renderiza nada enquanto restaura a sessão', () => {
    useAuthStore.getState().setRestoring(true)
    const { container } = renderGuarded()
    expect(container.textContent).toBe('')
  })
})

describe('RequireRole — papel que mudou no servidor (Sentinel L2 do #42)', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
    useAuthStore.getState().clearSession()
  })

  function AcaoDoAdmin() {
    useEffect(() => {
      apiFetch('/programas', { method: 'POST', body: {} }).catch(() => {})
    }, [])
    return <div>Tela do Admin</div>
  }

  it('Admin rebaixado: o 403 da primeira ação troca a tela montada pelo aviso de acesso restrito', async () => {
    const eu = { id: '1', nome: 'Ana', email: 'a@a.com', deveTrocarSenha: false }
    useAuthStore.getState().setSession('token-admin', { ...eu, role: 'Admin' })
    useAuthStore.getState().setRestoring(false)
    vi.stubGlobal(
      'fetch',
      vi.fn(
        async (url: string) =>
          url.endsWith('/auth/refresh')
            ? new Response(
                JSON.stringify({
                  accessToken: 'token-locutor',
                  expiraEmUtc: new Date().toISOString(),
                  deveTrocarSenha: false,
                  usuario: { ...eu, role: 'Locutor' },
                }),
                { status: 200, headers: { 'Content-Type': 'application/json' } },
              )
            : new Response(null, { status: 403 }), // a política de papel responde 403 sem corpo
      ),
    )

    render(
      <MemoryRouter initialEntries={['/admin']}>
        <Routes>
          <Route
            path="/admin"
            element={
              <RequireRole roles={['Admin']}>
                <AcaoDoAdmin />
              </RequireRole>
            }
          />
        </Routes>
      </MemoryRouter>,
    )

    expect(await screen.findByText('Acesso restrito')).toBeInTheDocument()
    expect(screen.queryByText('Tela do Admin')).not.toBeInTheDocument()
  })
})
