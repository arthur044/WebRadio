# WebRadio — regras para agentes

Plataforma de webradio interativa. Backend .NET (Clean Architecture + CQRS/MediatR, SQL Server), frontend React 19 + Vite + Tailwind 4, streaming Icecast/Liquidsoap, tudo em Docker. Specs em `docs/specs/`, processo em `docs/processo-de-desenvolvimento.md` (leia antes de abrir PR).

## Time (Maestri)

Fale com o time por `"$MAESTRI_CLI"` (o `maestri` do PATH é outro binário): `"$MAESTRI_CLI" list`, `"$MAESTRI_CLI" ask <Nome> "<msg>"`, `"$MAESTRI_CLI" note read <nota>`.

| Agente | Papel | Diretório |
|---|---|---|
| Nexus | planejador/arquiteto | `~/Projects/webradio-nexus` |
| Forge | backend .NET | `~/Projects/webradio-forge` |
| Prism | frontend React | `~/Projects/webradio-prism` |
| Atlas | DBA SQL Server | `~/Projects/webradio-atlas` |
| Lore | documentação | `~/Projects/webradio` (dono do checkout principal) |
| Guardian | code review (só leitura) | `~/Projects/webradio` |
| Sentinel | segurança (só leitura) | `~/Projects/webradio` |

## Git

- Cada agente que escreve tem seu worktree; trabalhe só no seu. Branch: `<agente>/<tarefa-id>` a partir de `origin/main`.
- No checkout principal (`~/Projects/webradio`), só o Lore troca de branch. Guardian e Sentinel usam apenas `git fetch`, `git show`, `git diff`, `git log`.
- `git add` só dos caminhos que você mudou. Nunca `git add -A`/`git add .`, `git stash`, reset hard ou clean.
- Conventional Commits (`feat`, `fix`, `refactor`, `docs`, `test`, `chore`, `perf`, `ci`), mensagens em PT-BR.
- `main` protegida. Review do Guardian sempre; Sentinel para auth/storage/infra/segredos; Atlas para SQL/migrations.
- Code review só com PR aberto. Findings via `gh pr review <N> --comment --body-file <arquivo>` (`--approve` falha: mesma conta).

## Comandos

Backend (raiz do worktree):
```bash
dotnet restore backend/WebRadio.slnx
dotnet build backend/WebRadio.slnx --configuration Release   # TreatWarningsAsErrors
dotnet test backend/WebRadio.slnx --filter "Categoria!=Integracao"
```

Frontend (`cd frontend`):
```bash
npm run lint && npm test && npm run build
npm run test:e2e        # Playwright
npm run format:check
```

Docker dev: `docker compose -f docker-compose.dev.yml up` (o arquivo dev precisa de `-f` explícito).

## Antes de dizer "pronto"

Rode build + testes da área que mudou e cite o resultado. CI também roda gitleaks: nunca commite segredo; segredos vão em arquivo, não em `.env`.
