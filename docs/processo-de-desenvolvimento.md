# Processo de Desenvolvimento

> Este documento descreve o fluxo de trabalho para contribuir ao WebRadio, com ênfase em coordenação de equipe, qualidade de código e segurança.

## Fila de Pull Requests

A `main` é **protegida por branch**: todo merge exige:
- Todos os **checks obrigatórios** (CI) passando
- Revisão do **Guardian** (code-review)
- Revisão adicional do **Sentinel** (security) para PRs de autenticação, storage, infraestrutura ou segredos
- Revisão adicional do **Atlas** (DBA) para PRs de SQL ou migrations

### Regra de um PR por vez

**Apenas um PR aberto por vez** na fila. Não abra um novo PR antes do anterior ser mergeado.

**Por quê?** Numa árvore de trabalho compartilhada (todos editam a mesma pasta), manter múltiplos PRs abertos causa:
- Conflitos ao sincronizar entre PRs
- Perda acidental de trabalho em troca de branch
- Interferência com testes de integração

**Coordenação**: o **Nexus** (planejador) gerencia a ordem. Comunique quando sua tarefa estiver pronta para PR.

## Criação de branch e commits

### Padrão de branch

```bash
git checkout -b <seu-agente>/<tarefa-id>
```

Exemplo:
- `forge/f04-ef-context`
- `prism/p03-player-global`
- `atlas/a01-schema-review`

### Commits

**Regra de ouro**: `git add` apenas dos caminhos que **você** criou ou modificou.

```bash
# ✅ CORRETO
git add src/features/auth/LoginForm.tsx
git add backend/src/WebRadio.Api/Endpoints/AuthEndpoints.cs
git commit -m "feat: login form com validação"

# ❌ ERRADO
git add .                   # Leva arquivos de outros agentes
git add -A                  # Idem
git add src/               # Pode incluir mudanças alheias
```

**Por quê?** Numa árvore de trabalho compartilhada, staged files de outros agentes podem estar misturados. Se você faz `git add -A`, leva o trabalho deles junto, e ao commitaar, você se torna coautor involuntário. Worse: se você depois faz `git reset --hard` antes de sincronizar, esse trabalho staged desaparece do disco.

**Atômico**: cada commit faz uma coisa só.

```bash
git commit -m "feat: auth com JWT e refresh rotativo"  # ✅
# Não: "feat: auth, health checks, docker-compose e documentação"
```

**Mensagem**: use Conventional Commits.

```
<tipo>: <descrição>

<corpo opcional>
```

Tipos: `feat`, `fix`, `refactor`, `docs`, `test`, `chore`, `perf`, `ci`

Exemplo:
```
feat: comandos de login e refresh com HMAC-SHA256

- Access token válido por 15 min
- Refresh token rotativo com revogação de família
- Cookie HttpOnly + SameSite=Strict
```

## Não troque de branch antes do merge

**Cenário proibido:**
```bash
git checkout -b forge/f04-ef
# ... adiciona migrations/EF ...
git add backend/src/WebRadio.Infrastructure/Data
git commit -m "feat: EF core migration"

# ❌ Alguém precisa que você volte para main
git checkout main  # A árvore muda; os arquivos de F04 somem do disco
# ... outro agente commitou num arquivo que F04 usa ...
git merge --ff-only origin/main

# Agora há conflito e os arquivos de F04 não estão visíveis em local
```

**Por quê?** Você criou um arquivo em `forge/f04-ef` e commitou lá. Se trocar de branch **antes do merge**, o arquivo fica "órfão": existe no git, mas está "privado" daquela branch. Outro agente pode vir e, sem ver, editar um arquivo que teoricamente está na sua branch mas não está no disco dele. Ao sincronizar, há conflito oculto.

**Regra**: depois de abrir o PR, **não saia da branch até o merge**. Enquanto isso, outros agentes devem:

1. Sincronizar com `main` regularmente (para não ficar muito atrasado):
   ```bash
   git fetch origin
   # (não faz merge, só atualiza a referência)
   ```

2. Se precisar da sua branch, avise: "espera a F04 ser mergeada".

Depois do merge:
```bash
git fetch origin                    # Atualiza main
git checkout main
git merge --ff-only origin/main     # Avança main para o commit mergeado
```

## Merge sem squash

O merge é um **merge commit**, não squash.

```bash
# ✅ No GitHub: deixe "Create a merge commit" selecionado (padrão)

# ❌ Não use:
# - Squash and merge (apaga a história de commits)
# - Rebase and merge (reescreve os commits)
```

**Por quê?** O merge commit registra:
- O ponto exato quando a tarefa entrou em `main`
- A origem de cada linha de código (importante para blame/audit)
- Que aquele agente finalizou aquela tarefa

## Revisores obrigatórios

Cada tipo de PR tem revisores específicos.

### 🛡️ Guardian (Code-Review)

Revisa **todos os PRs**.

- Qualidade de código (nomes, funções pequenas, sem code smell)
- Testes cobrindo ≥ 80% (Domain, Application)
- Sem console.log, TODO/FIXME abandonados
- Performance e legibilidade

Critério de aceite: nenhum issue CRÍTICO ou ALTO sem correção.

### 🔐 Sentinel (DevSecOps/Security)

Revisa PRs de:
- Autenticação/autorização (`/auth`, JWT, refresh, bloqueio de conta)
- Storage e upload (`/midias`, presigned URLs, sanitização)
- Infraestrutura e compose (`docker-compose.yml`, Dockerfiles, segredos)
- Qualquer mudança com o ID prefixo `S-` no `05-seguranca-auditoria.md`

Critério de aceite: checklist da §8 do `05-seguranca-auditoria.md` 100% ok.

### 🗄️ Atlas (DBA)

Revisa PRs de:
- SQL e migrations (`03-schema-sqlserver.sql`, `infra/sql/`)
- EF Core `DbContext` e `IEntityTypeConfiguration`

Critério de aceite: script de migration aprovado, índices corretos, collation e tipos conferidos.

## Checks obrigatórios na CI

A `main` exige que **todos** os seguintes checks passem antes de permitir merge:

| Check | Descrição | Falha em |
|-------|-----------|----------|
| `seguranca` | gitleaks (histórico inteiro), grep `__GERAR__` no `.env.example` | segredo em claro, credencial no git |
| `backend-unit` | `dotnet build`, testes unitários, cobertura | código quebrado, cobertura < 80% |
| `backend-integracao` | Testcontainers SQL + Redis, testes de integração | comportamento incorreto com banco real |
| `ddl-check` | Schema validação contra `03-schema-sqlserver.sql` | migração inválida, índice perdido |
| `frontend` | ESLint, Prettier, testes Vitest, build Vite | lint, formatação, testes |
| `compose-smoke` | Docker Compose up, Playwright no player | serviços não sobem, UI quebrada |
| `vulnerabilidades` | trivy, `dotnet list package --vulnerable`, `npm audit` | dependência vulnerável |

Se um check falhar, o merge é bloqueado até a correção.

## Lição de 2026-09-27

A organização aprendeu pela via difícil a importância de **staging e committing conservador** numa árvore compartilhada. Em uma reescrita de histórico, operações como `git stash -u` removem do disco o trabalho de outros agentes que estava staged mas não commitado — resultando em perda de horas.

### Proteções

1. **Sempre commit antes de operações reescritoras**:
   ```bash
   git add seu-arquivo.ts
   git commit -m "wip: em progresso"
   # Agora está seguro no git, até que rebase/reset
   ```

2. **`git add` conservador**:
   ```bash
   git add src/seu-arquivo.ts  # Só o que você tocou
   # Não: git add . ou git add -A
   ```

3. **Comunique antes de reescrita**. Se sua branch está aberta, rebase causa fallout para outros.

4. **Sincronize frequentemente** com `git fetch` para não ficar atrás e ter que fazer operações complicadas:
   ```bash
   git fetch origin        # Atualiza suas referências
   # Agora sabe onde main está
   ```

## Checklist antes de abrir PR

- [ ] Branch criada a partir da `main` atual (não atrás)
- [ ] `git add` só dos caminhos que você tocou
- [ ] Commits atômicos com mensagens claras
- [ ] Nenhum `console.log`, `// TODO`, `// DEBUG`
- [ ] Sem segredo, `.env`, arquivo pessoal no histórico
- [ ] Testes escritos (TDD recomendado) e passando
- [ ] Backend: `dotnet build`, `dotnet test` 100% verde localmente
- [ ] Frontend: `npm run lint`, `npm test`, `npm run build` 100% verde localmente
- [ ] Mudanças de contrato (DTO, rota, coluna) atualizadas em `docs/specs/` **antes** do código
- [ ] Uma única purpose por PR (não misture F04 + F06 + P08)

## Sincronização após merge

Quando seu PR for mergeado:

```bash
git fetch origin
git checkout main
git merge --ff-only origin/main  # Avança para o merge commit
```

Agora você e outros estão no mesmo ponto. O próximo PR pode começar.

---

**Veja também:**
- [`./specs/00-arquitetura.md`](specs/00-arquitetura.md) — decisões e design
- [`./specs/04-epico-1-tarefas.md`](specs/04-epico-1-tarefas.md) — tarefas em detalhe
- [`../SECURITY.md`](../SECURITY.md) — política de segurança

**Versão**: v1.1  
**Última atualização**: 2026-09-28
