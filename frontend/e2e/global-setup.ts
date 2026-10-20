import { request, type FullConfig } from '@playwright/test'
import { exigirVariavel } from './helpers/stack'

/**
 * Só no modo stack (E2E_BASE_URL): prepara o Admin semeado do compose descartável. O Admin nasce com
 * `deveTrocarSenha`: troca a senha uma vez e deixa o token e o id nas variáveis de ambiente, que os
 * workers herdam. Um login só aqui (e o da tela, no spec da administração) mantém o E2E longe do
 * limite de 5 logins por minuto por IP e do bloqueio por falhas (D21).
 *
 * Roda DEPOIS do smoke-compose.sh, que confere justamente o `deveTrocarSenha` do primeiro login.
 */
export default async function globalSetup(config: FullConfig) {
  const baseURL = config.projects[0].use.baseURL!
  const email = exigirVariavel('E2E_ADMIN_EMAIL')
  const senhaSemeada = exigirVariavel('E2E_ADMIN_SENHA')
  const senhaNova = exigirVariavel('E2E_ADMIN_SENHA_NOVA')

  // Origin da mesma origem: as rotas de cookie (refresh, logout) exigem; o login não, mas não custa.
  const api = await request.newContext({ baseURL, extraHTTPHeaders: { Origin: baseURL } })
  try {
    let login = await api.post('/api/v1/auth/login', { data: { email, senha: senhaSemeada } })

    if (login.ok()) {
      const corpo = await login.json()
      if (corpo.deveTrocarSenha) {
        const troca = await api.post('/api/v1/auth/trocar-senha', {
          headers: { Authorization: `Bearer ${corpo.accessToken}` },
          data: { senhaAtual: senhaSemeada, novaSenha: senhaNova },
        })
        if (troca.status() !== 204) {
          throw new Error(`Troca da senha do Admin falhou: HTTP ${troca.status()}`)
        }
        login = await api.post('/api/v1/auth/login', { data: { email, senha: senhaNova } })
      }
    } else {
      // O stack já passou por uma rodada anterior (rerun local): a senha semeada não vale mais.
      login = await api.post('/api/v1/auth/login', { data: { email, senha: senhaNova } })
    }

    if (!login.ok()) throw new Error(`Login do Admin falhou: HTTP ${login.status()}`)
    const sessao = await login.json()
    if (sessao.deveTrocarSenha) throw new Error('O Admin continua com a troca de senha pendente.')

    process.env.E2E_ADMIN_TOKEN = sessao.accessToken
    process.env.E2E_ADMIN_ID = sessao.usuario.id
  } finally {
    await api.dispose()
  }
}
