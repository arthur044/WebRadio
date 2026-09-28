import { render, screen } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { beforeEach, describe, expect, it } from 'vitest'
import { useAuthStore } from '../../shared/api/authStore'
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
    useAuthStore
      .getState()
      .setSession('t', {
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
    useAuthStore
      .getState()
      .setSession('t', {
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
