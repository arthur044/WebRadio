# WebRadio

**WebRadio** é uma plataforma open-source de transmissão de rádio online 24/7 que combina streaming contínuo com moderação de pedidos musicais em tempo real. Ouvintes solicitam músicas sem autenticação, locutores aprovam e tocam, e o stream permanece ininterrupto via Icecast + Liquidsoap.

## Arquitetura

```
Internet
    ↓
 [nginx gateway :8080]  ← SPA React, /api/*, /hubs/*, /stream/*
    ↓        ↓        ↓
[Icecast]  [API .NET]  [Redis]
    ↑        ↓         ↓
[Liquidsoap] [SQL Server] [MinIO]
```

Arquitetura detalhada: [`docs/specs/00-arquitetura.md`](docs/specs/00-arquitetura.md)

### Stack

- **Backend**: .NET 10 (Clean Architecture, EF Core 9, MediatR)
- **Banco de dados**: SQL Server 2022, migrations geradas via EF Core
- **Cache & realtime**: Redis (throttle, SignalR backplane)
- **Storage**: MinIO (S3-compatible, arquivos de áudio/divulgação)
- **Áudio**: Icecast (stream distribuição) + Liquidsoap (playout, fallback)
- **Frontend**: React 19 + Vite + TypeScript + Tailwind + React Router

### Componentes principais

| Serviço | Função | Tecnologia |
|---------|--------|-----------|
| **API** | REST endpoints + SignalR hub (pedidos, grade, auth) | ASP.NET Core 10 |
| **Worker** | Transições de grade, sanitização, limpeza | .NET 10 background service |
| **db-init** | Criação do schema, logins SQL | one-shot sqlcmd |
| **migrator** | Aplicação de migrations EF | one-shot efbundle |
| **Icecast** | Distribuição do stream HTTP | Icecast 2 |
| **Liquidsoap** | Motor de playout, fallback, ao vivo | Liquidsoap 2.x |
| **MinIO** | Armazenamento S3 (mídia, uploads) | MinIO |
| **Redis** | Throttle de pedidos, backplane SignalR | Redis 7 |
| **nginx** | Gateway, reverse proxy, segurança | nginx unprivileged |

## Estrutura do repositório

```
webradio/
├── docs/specs/                    # Especificações v1.1 (arquitetura, domínio, API, schema, segurança)
├── backend/
│   ├── WebRadio.sln
│   ├── Directory.Build.props      # .NET 10, properties centralizadas
│   ├── src/
│   │   ├── WebRadio.Domain/       # Entidades, enums, invariantes
│   │   ├── WebRadio.Application/  # Commands/Queries, validadores, interfaces
│   │   ├── WebRadio.Infrastructure/ # EF Core, Redis, MinIO, JWT
│   │   ├── WebRadio.Api/          # Minimal APIs, SignalR hub, health checks
│   │   └── WebRadio.Worker/       # Background service, líder, jobs
│   └── tests/
│       ├── WebRadio.Domain.Tests/
│       ├── WebRadio.Application.Tests/
│       ├── WebRadio.Architecture.Tests/ # NetArchTest
│       └── WebRadio.Api.IntegrationTests/
├── frontend/
│   ├── package.json               # Vite, React, Tailwind, Playwright
│   ├── src/
│   │   ├── features/              # Por feature: auth, player, grade, pedidos, mural, estudio
│   │   ├── shared/                # API, lib, componentes reutilizáveis
│   │   └── assets/
│   └── e2e/                       # Testes Playwright
├── infra/
│   ├── scripts/                   # gerar-segredos.sh/.ps1
│   ├── sql/                       # init-db.sql, logins.sql, seed-dev.sql
│   ├── nginx/                     # default.conf.template (gateway)
│   ├── icecast/                   # Dockerfile, icecast.xml.template
│   ├── liquidsoap/                # radio.liq, playlist de teste
│   ├── minio/                     # init-buckets.sh, políticas S3
│   └── redis/                     # entrypoint de configuração
├── docker-compose.yml             # Produção-like (segredos em arquivo)
├── docker-compose.dev.yml         # Desenvolvimento (deve ser usado explicitamente com -f)
├── .env.example                   # Placeholders: __GERAR__ para segredos
├── .gitattributes                 # Scripts shell com LF em Windows
├── .gitignore                     # .env, .maestri/, node_modules/
├── SECURITY.md                    # Política de segurança e divulgação
└── LICENSE
```

## Status do Épico 1

### Na `main` (mergeado)

- ✅ **Domínio (F02, F03)**: Entidades (Usuario, Programa, PedidoMusica, ArquivoMidia, etc.), máquinas de estado, testes de arquitetura.
- ✅ **SQL (A01, A02, A04, A06)**: Schema com schemas nomeados, índices, CHECKs, logins mínimos, seed de desenvolvimento.
- ✅ **CI (F17)**: GitHub Actions com gitleaks, build .NET, testes, Testcontainers, ddl-check, linters frontend.
- ✅ **Backend API básica (F04–F07a)**: EF Context + migrations, db-init/migrator, Minimal APIs (ProblemDetails, rate-limit, RateLimiter), autenticação JWT com refresh rotativo, bloqueio por (IP, conta), health checks internos, abstrações de infra, segredos validados no startup.

### Em PR (aguardando merge)

- 🔄 **Frontend (P01–P08)**: Scaffold Vite + React + Tailwind, direção visual (P02), shell + player global (P03), cliente tipado (P04), auth UI (P05), nginx gateway (P06), SignalR (P07), páginas placeholder (P08).

### Em desenvolvimento

- 🚧 **Backend (F08–F16)**: Health checks `/health/live` e `/health/ready`, abstrações de infra (IClock, IObjectStorage, IRequestThrottle, IListenerIdentity, IAudioSanitizer, IMalwareScanner), SignalR hub com outbox dispatcher, Worker com eleição de líder, endpoints internos playout, Dockerfiles, Icecast + Liquidsoap, docker-compose + .env.example.

## Como contribuir

### Fluxo de PR

1. **Crie uma branch** a partir de `main` com o padrão `<seu-agente>/<tarefa>`:
   ```bash
   git checkout main
   git pull origin main
   git checkout -b seu-agente/tarefa-x
   ```

2. **Commit apenas os arquivos que você criou/modificou**:
   ```bash
   git add src/seu-arquivo.ts
   git add backend/src/SeuProjeto/SeuArquivo.cs
   git commit -m "feat: descrição curta"
   ```
   ⚠️ **Nunca** use `git add .` ou `git add -A` numa árvore compartilhada.

3. **Não troque de branch** antes do merge. Isso muda a árvore de trabalho para os outros agentes.

4. **Push e abra um PR** contra `main`:
   ```bash
   git push -u origin seu-agente/tarefa-x
   ```

5. **Um PR por vez**: o Nexus coordena a fila. Sem novo PR até o anterior ser mergeado.

6. **Revisores obrigatórios**:
   - 🛡️ **Guardian** (Code-Review): todos os PRs
   - 🔐 **Sentinel** (DevSecOps): PRs de auth, storage, infra, segredos
   - 🗄️ **Atlas** (DBA): PRs de SQL e migrations

7. **Merge sem squash**: cria um merge commit que registra a história e a origem.

### Checklist de qualidade

- [ ] Branch criada a partir de `main` (não atrás)
- [ ] Commits só dos caminhos que você tocou
- [ ] `dotnet build` (backend) e `npm run lint`, `npm test`, `npm run build` (frontend) localmente
- [ ] Testes unitários com cobertura ≥ 80% para Domain/Application (backend)
- [ ] Nenhum segredo, `.env`, ou dados pessoais no histórico
- [ ] Specs/contrato atualizados **antes** do código (mudanças em DTO, rota ou coluna)

## Como subir localmente

> ⚠️ **Disponível após E1-F16 (Forge em desenvolvimento).**  
> O `docker-compose.yml`, `gerar-segredos.sh`, Icecast, Liquidsoap e a imagem do `sanitizer` ainda estão em desenvolvimento (F13–F16). Instruções completas serão incluídas assim que a Definição de Pronto do Épico 1 for alcançada.

Enquanto isso, consulte [`docs/specs/04-epico-1-tarefas.md`](docs/specs/04-epico-1-tarefas.md) para a sequência de tarefas, [`docs/runbooks/rotacao-de-segredos.md`](docs/runbooks/rotacao-de-segredos.md) para procedimentos operacionais e [`docs/processo-de-desenvolvimento.md`](docs/processo-de-desenvolvimento.md) para o fluxo de PR.

## Segurança

WebRadio é um projeto **público no GitHub**. Nenhum segredo, credencial, e-mail pessoal ou IP pode ser commitado.

- 🔒 Política de segurança: [`SECURITY.md`](SECURITY.md)
- 🔍 Auditoria de segurança: [`docs/specs/05-seguranca-auditoria.md`](docs/specs/05-seguranca-auditoria.md)
- 🚨 Relatar vulnerabilidade: veja [`SECURITY.md`](SECURITY.md)

## Especificações

| Arquivo | Escopo | Leitura obrigatória para |
|---------|--------|--------------------------|
| [`00-arquitetura.md`](docs/specs/00-arquitetura.md) | Visão macro, decisões, fluxos | Todos |
| [`01-dominio.md`](docs/specs/01-dominio.md) | Entidades, enums, máquinas de estado | Backend |
| [`02-api-rest.md`](docs/specs/02-api-rest.md) | Contratos REST, SignalR, RBAC | Backend, Frontend |
| [`03-schema-sqlserver.sql`](docs/specs/03-schema-sqlserver.sql) | DDL, índices, constraints | Backend, DBA |
| [`04-epico-1-tarefas.md`](docs/specs/04-epico-1-tarefas.md) | Pacote de tarefas do Épico 1 | Time |
| [`05-seguranca-auditoria.md`](docs/specs/05-seguranca-auditoria.md) | Auditoria de segurança (normativo) | Backend, DevSecOps |

## Licença

WebRadio é licenciado sob a [MIT License](LICENSE).

---

**Versão**: v1.1 (Épico 1)  
**Última atualização**: 2026-09-28  
**Mantido por**: Nexus (Planejador), Forge (Backend), Atlas (DBA), Prism (Frontend)
