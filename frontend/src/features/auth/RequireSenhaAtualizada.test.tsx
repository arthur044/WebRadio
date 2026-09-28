import { render, screen } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { afterEach, describe, expect, it } from 'vitest'
import { useAuthStore } from '../../shared/api/authStore'
import { RequireSenhaAtualizada } from './RequireSenhaAtualizada'

function renderApp(initialPath: string) {
  return render(
    <MemoryRouter initialEntries={[initialPath]}>
      <Routes>
        <Route path="/trocar-senha" element={<div>Tela de troca de senha</div>} />
        <Route
          path="/grade"
          element={
            <RequireSenhaAtualizada>
              <div>Grade</div>
            </RequireSenhaAtualizada>
          }
        />
      </Routes>
    </MemoryRouter>,
  )
}

describe('RequireSenhaAtualizada', () => {
  afterEach(() => {
    useAuthStore.getState().clearSession()
  })

  it('Admin semeado (deveTrocarSenha=true) não navega antes de trocar a senha', () => {
    useAuthStore
      .getState()
      .setSession('t', {
        id: '1',
        nome: 'Admin',
        email: 'admin@a.com',
        role: 'Admin',
        deveTrocarSenha: true,
      })
    renderApp('/grade')
    expect(screen.getByText('Tela de troca de senha')).toBeInTheDocument()
    expect(screen.queryByText('Grade')).not.toBeInTheDocument()
  })

  it('navega normalmente depois que deveTrocarSenha vira false', () => {
    useAuthStore
      .getState()
      .setSession('t', {
        id: '1',
        nome: 'Admin',
        email: 'admin@a.com',
        role: 'Admin',
        deveTrocarSenha: false,
      })
    renderApp('/grade')
    expect(screen.getByText('Grade')).toBeInTheDocument()
  })
})
