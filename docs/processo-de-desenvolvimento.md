# Processo de Desenvolvimento

> Este documento descreve o fluxo de trabalho para contribuir ao WebRadio, com ênfase em coordenação de equipe, qualidade de código e segurança.

## Fila de Pull Requests

A `main` é **protegida**: só entra por PR, e o GitHub bloqueia o merge enquanto os dois checks obrigatórios não passarem: `Segurança (gitleaks)` e `Backend (unit + arquitetura)`. O GitHub **não** exige aprovação de review (o repositório tem uma conta só) nem os demais jobs.

O resto é **regra do time**, cumprida por disciplina e não pela plataforma:
- Todos os jobs do CI verdes antes do merge, não só os dois obrigatórios
- Revisão do **Guardian** (code-review) em todo PR
- Revisão adicional do **Sentinel** (security) para PRs de autenticação, storage, infraestrutura ou segredos
- Revisão adicional do **Atlas** (DBA) para PRs de SQL ou migrations
- Merge autorizado pelo dono do produto

### Entrega por feature grande

Por decisão do dono do produto (2026-10-12), o trabalho entra por **feature grande**, e não por tarefa: um PR por grupo de tarefas relacionadas, com **commits separados por tarefa** e uma revisão no fim. O agrupamento do backend do Épico 1 (B1, B2, B3) está em [`specs/04-epico-1-tarefas.md`](specs/04-epico-1-tarefas.md).

A regra antiga, "apenas um PR aberto por vez", existia porque todos os agentes editavam a mesma pasta. Ela deixou de valer quando cada agente passou a trabalhar em **worktree próprio** (ver abaixo). O **Nexus** (planejador) continua definindo a ordem e avisando quem depende de quem.

### Revisão só com PR aberto

Um revisor só é chamado quando já existe um **PR aberto** do assunto. Rascunho local ou branch sem PR não vai para revisão. O review é publicado no próprio PR (`gh pr review`), e o re-review cobre só o delta desde o commit revisado.

### Worktrees e o checkout compartilhado

- Cada agente desenvolve num **worktree próprio** (`git worktree add <pasta> -b <agente>/<assunto> origin/main`), fixo ou temporário.
- O checkout compartilhado **não troca de branch**: outros agentes podem ter commits locais ou arquivos não rastreados nele.
- O projeto roda em mais de uma máquina; só o GitHub é comum. Antes de confiar no estado local, rode `git fetch` e confira os PRs abertos.

### Limpeza ao terminar

Ao concluir um desenvolvimento, cada agente apaga o que criou no caminho: worktrees temporários, logs, clones, contêineres e volumes de teste. Apague só o que é seu, e olhe o conteúdo antes (um arquivo temporário pode ser a única cópia de uma nota). Branches já mergeadas são apagadas do remoto.

## Criação de branch e commits

### Padrão de branch

```bash
git fetch origin
git worktree add ../webradio-<seu-agente> -b <seu-agente>/<assunto> origin/main
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

> Esta seção vale para quem trabalha no **checkout compartilhado**. Num worktree próprio, a branch é só sua e o problema descrito aqui não acontece.

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
# ✅ No GitHub: escolha "Create a merge commit" (ou `gh pr merge --merge`).
#    O repositório também aceita squash e rebase; não usar é regra do time, a plataforma não impede.

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

O CI roda os jobs abaixo em todo PR. O GitHub só **bloqueia** o merge por `seguranca` e `backend-unit`; os outros são exigidos pela regra do time.

| Job | O que roda | Falha em |
|-----|-----------|----------|
| `seguranca` | gitleaks no histórico inteiro; grep de segredo no `.env.example` (o grep entra com o PR #31) | segredo em claro, credencial no git |
| `backend-unit` | build, testes unitários e de arquitetura | código quebrado, regra de camada violada |
| `backend-sql` | testes que precisam de SQL Server e Redis reais; falha se algum for pulado | comportamento incorreto com banco real |
| `ddl-check` | compara `03-schema-sqlserver.sql` com o script das migrations do EF | spec e migrations divergentes |
| `frontend` | ESLint, testes Vitest, `tsc` e build do Vite | lint, tipos, testes |
| `infra-testes` | `teste.sh` do Icecast e do Liquidsoap | regressão no playout |
| `compose-smoke` | sobe o stack inteiro e roda `infra/scripts/smoke-compose.sh` | serviço que não sobe, isolamento de rede, bypass da porta interna |
| `vulnerabilidades` | `dotnet list package --vulnerable`, `npm audit` e `trivy` nas imagens construídas | dependência ou imagem com vulnerabilidade alta ou crítica |

Além desses, o GitHub roda o CodeQL. Os jobs `backend-sql`, `ddl-check`, `frontend`, `infra-testes` e `vulnerabilidades` entram com o PR #31; antes dele, só `seguranca`, `backend-unit` e `compose-smoke` existem. O Playwright ainda não roda no CI.

Se um dos dois checks obrigatórios falhar, o GitHub bloqueia o merge. Se outro job falhar, o merge não é feito até a correção, por regra do time.

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

- [ ] Branch criada a partir da `main` atual, num worktree próprio
- [ ] `git add` só dos caminhos que você tocou
- [ ] Commits atômicos com mensagens claras
- [ ] Nenhum `console.log`, `// TODO`, `// DEBUG`
- [ ] Sem segredo, `.env`, arquivo pessoal no histórico
- [ ] Testes escritos (TDD recomendado) e passando
- [ ] Backend: `dotnet build`, `dotnet test` 100% verde localmente
- [ ] Frontend: `npm run lint`, `npm test`, `npm run build` 100% verde localmente
- [ ] Mudanças de contrato (DTO, rota, coluna) atualizadas em `docs/specs/` **antes** do código
- [ ] Um PR por feature grande, com commits separados por tarefa (não misture features sem relação)

## Sincronização após merge

Quando seu PR for mergeado:

```bash
git fetch origin
git checkout main
git merge --ff-only origin/main  # Avança para o merge commit
```

Agora você e outros estão no mesmo ponto. Apague a branch mergeada (local e remota) e os temporários que criou.

---

**Veja também:**
- [`./specs/00-arquitetura.md`](specs/00-arquitetura.md) — decisões e design
- [`./specs/04-epico-1-tarefas.md`](specs/04-epico-1-tarefas.md) — tarefas em detalhe
- [`../SECURITY.md`](../SECURITY.md) — política de segurança (o arquivo entra com o PR #31)

**Versão**: v1.2  
**Última atualização**: 2026-10-14
