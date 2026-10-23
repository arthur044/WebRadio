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

describe('TrocarSenhaPage — motivo do erro do servidor', () => {
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

  function servidorResponde(resposta: Response) {
    vi.stubGlobal(
      'fetch',
      vi.fn(async (url: string) => {
        if (url.endsWith('/auth/trocar-senha')) return resposta
        throw new Error(`chamada inesperada: ${url}`)
      }),
    )
  }

  const validacao = (errors: Record<string, string[]>) =>
    jsonResponse(400, {
      type: '/erros/validacao',
      title: 'Dados inválidos.',
      status: 400,
      detail: 'Um ou mais campos são inválidos.',
      errors,
    })

  it('mostra a regra da nova senha abaixo do campo desde o início', () => {
    renderApp()

    expect(screen.getByLabelText(/Nova senha/)).toHaveAccessibleDescription(
      'A senha precisa ter de 12 a 128 caracteres.',
    )
  })

  it.each([
    ['senha comum', 'Esta senha é muito comum. Escolha outra.'],
    ['igual à atual', 'A nova senha precisa ser diferente da atual.'],
    ['tamanho', 'A senha precisa ter de 12 a 128 caracteres.'],
  ])('400 de validação (%s): o motivo aparece junto do campo da nova senha', async (_, motivo) => {
    servidorResponde(validacao({ NovaSenha: [motivo] }))
    renderApp()
    await preencherEEnviar()

    const campo = screen.getByLabelText(/Nova senha/)
    expect(await screen.findByRole('alert')).toHaveTextContent(motivo)
    expect(campo).toBeInvalid()
    expect(campo).toHaveAccessibleDescription(expect.stringContaining(motivo))
    expect(screen.queryByText(/Um ou mais campos são inválidos/)).not.toBeInTheDocument()
    expect(screen.queryByText(/Corrija os campos/)).not.toBeInTheDocument()
  })

  it('várias mensagens do mesmo campo aparecem juntas', async () => {
    servidorResponde(
      validacao({ NovaSenha: ['Esta senha é muito comum. Escolha outra.', 'Outra regra.'] }),
    )
    renderApp()
    await preencherEEnviar()

    expect(await screen.findByRole('alert')).toHaveTextContent(
      'Esta senha é muito comum. Escolha outra. Outra regra.',
    )
  })

  it('400 no campo da senha atual aparece junto dele', async () => {
    servidorResponde(validacao({ SenhaAtual: ['Informe a senha atual.'] }))
    renderApp()
    await preencherEEnviar()

    expect(await screen.findByRole('alert')).toHaveTextContent('Informe a senha atual.')
    expect(screen.getByLabelText('Senha atual')).toBeInvalid()
  })

  it('campo que a tela não tem ainda aparece', async () => {
    servidorResponde(validacao({ Outro: ['Algo mais está errado.'] }))
    renderApp()
    await preencherEEnviar()

    expect(await screen.findByRole('alert')).toHaveTextContent('Algo mais está errado.')
  })

  it('401 de senha atual errada aparece junto do campo da senha atual', async () => {
    servidorResponde(jsonResponse(401, { type: '/erros/credenciais', title: 'x', status: 401 }))
    renderApp()
    await preencherEEnviar()

    expect(await screen.findByRole('alert')).toHaveTextContent('Senha incorreta.')
    expect(screen.getByLabelText('Senha atual')).toBeInvalid()
  })

  it('erro sem campo (429) mostra a mensagem geral', async () => {
    servidorResponde(jsonResponse(429, { type: '/erros/limite', title: 'x', status: 429 }))
    renderApp()
    await preencherEEnviar()

    expect(await screen.findByRole('alert')).toHaveTextContent('Muitas tentativas')
  })
})
