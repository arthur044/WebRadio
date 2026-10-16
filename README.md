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

- **Backend**: .NET 10 (Clean Architecture, EF Core 10, MediatR)
- **Banco de dados**: SQL Server 2022, migrations geradas via EF Core
- **Cache & realtime**: Redis (throttle, SignalR backplane)
- **Storage**: MinIO (S3-compatible, arquivos de áudio/divulgação)
- **Áudio**: Icecast (stream distribuição) + Liquidsoap (playout, fallback)
- **Frontend**: React 19 + Vite + TypeScript + Tailwind + React Router

### Componentes principais

| Serviço | Função | Tecnologia |
|---------|--------|-----------|
| **API** | REST endpoints + SignalR hub (pedidos, grade, auth) | ASP.NET Core 10 |
| **Worker** | Eleição de líder e manutenção diária; transições de grade e sanitização entram nos Épicos 2 e 4 | .NET 10 background service |
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
├── docs/
│   ├── specs/                     # Especificações (arquitetura, domínio, API, schema, tarefas, segurança)
│   ├── runbooks/                  # Procedimentos operacionais
│   ├── conexao-sql-server.md      # Connection string, pool e identificação de sessão
│   └── processo-de-desenvolvimento.md
├── backend/
│   ├── WebRadio.slnx
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
│       ├── WebRadio.Infrastructure.Tests/ # parte roda contra SQL Server real
│       └── WebRadio.Api.IntegrationTests/
├── frontend/
│   ├── package.json               # Vite, React, Tailwind, Playwright
│   ├── src/
│   │   ├── features/              # Por feature: auth, player, grade, pedidos, mural, estudio
│   │   ├── shared/                # API, lib, componentes reutilizáveis
│   │   └── assets/
│   └── e2e/                       # Testes Playwright
├── infra/
│   ├── scripts/                   # gerar-segredos.sh/.ps1, smoke-compose.sh
│   ├── db-init/                   # entrypoint do one-shot que cria banco e logins
│   ├── migrator/                  # entrypoint do one-shot que aplica as migrations
│   ├── sql/                       # init-db.sql, logins.sql
│   ├── nginx/                     # default.conf.template (gateway)
│   ├── icecast/                   # Dockerfile, icecast.xml.template
│   ├── liquidsoap/                # radio.liq, playlist de teste
│   ├── minio/                     # init-buckets.sh, políticas S3
│   └── redis/                     # entrypoint de configuração
├── docker-compose.yml             # Base de produção (segredos em arquivo, só a 8080 publicada)
├── docker-compose.dev.yml         # Desenvolvimento (deve ser usado explicitamente com -f)
├── .env.example                   # Configuração NÃO secreta; os segredos ficam em secrets/ (fora do git)
├── .gitattributes                 # Scripts shell com LF em Windows
├── .gitignore                     # .env, .maestri/, node_modules/
├── SECURITY.md                    # Política de segurança e divulgação (entra com o PR #31)
└── LICENSE
```

## Status do Épico 1

O Épico 1 entrega a fundação rodando de ponta a ponta; as regras de negócio de grade, pedidos e upload são dos Épicos 2 a 4. O detalhe de cada tarefa e o que ainda está pendente ficam em [`docs/specs/04-epico-1-tarefas.md`](docs/specs/04-epico-1-tarefas.md).

### Na `main`

- **Domínio e banco**: entidades e máquinas de estado, schema do SQL Server com migrations do EF Core, logins mínimos, `db-init` e `migrator`.
- **API**: autenticação JWT com refresh rotativo, gestão de usuários, health checks, abstrações de infraestrutura (relógio, storage, throttle, identidade do ouvinte), hub SignalR com outbox, endpoints internos de playout na porta `:8081`.
- **Worker**: eleição de líder, heartbeat e manutenção diária (anonimização LGPD, limpeza de outbox e de refresh tokens).
- **Playout**: Icecast e Liquidsoap com a playlist de teste; o Liquidsoap consulta a API pela próxima faixa.
- **Stack**: `docker-compose.yml` e `docker-compose.dev.yml` com 5 redes segmentadas, segredos por arquivo e contêineres endurecidos; scripts de geração de segredos.
- **Frontend**: shell com player global, autenticação, Mural e Grade com atualização em tempo real, gateway nginx.
- **CI**: gitleaks, build e testes do backend, smoke do stack inteiro no Docker Compose.

### Em PR

- **B3 (PR #31)**: jobs de CI com SQL Server real, verificação de DDL, frontend, testes de infra e varredura de vulnerabilidades; pendências dos reviews do B2; imagens-base atualizadas; `SECURITY.md`.

### Pendente

- Testes Playwright no CI, execução do `gerar-segredos.ps1` em Windows real e subida da base de produção num job.
- Atualização das imagens do SQL Server e do Liquidsoap, antes de o Épico 4 liberar upload.
- Reescrita do runbook de rotação de segredos (ver o aviso no topo dele).

## Como contribuir

### Fluxo de PR

O fluxo completo está em [`docs/processo-de-desenvolvimento.md`](docs/processo-de-desenvolvimento.md). Em resumo:

1. **Branch a partir da `main`**, no padrão `<agente>/<assunto>`, criada num worktree próprio (`git worktree add`). O checkout compartilhado não troca de branch.
2. **Commit só dos arquivos que você criou ou modificou.** Nunca `git add .` nem `git add -A` numa árvore compartilhada.
3. **Um PR por feature grande**, com commits separados por tarefa, em vez de um PR por tarefa.
4. **Revisão só com PR aberto:** Guardian (código) em todos; Sentinel (segurança) em auth, storage, infra e segredos; Atlas (banco) em SQL e migrations; Prism em frontend.
5. **Merge sem squash** (merge commit), depois de todos os checks verdes.
6. **Ao terminar**, apague os temporários que criou (worktrees, logs, contêineres de teste) e a branch já mergeada.

### Checklist de qualidade

- [ ] Branch criada a partir da `main` atual
- [ ] Commits só dos caminhos que você tocou
- [ ] `dotnet build` (backend) e `npm run lint`, `npm test`, `npm run build` (frontend) localmente
- [ ] Testes unitários com cobertura ≥ 80% para Domain/Application (backend)
- [ ] Nenhum segredo, `.env`, ou dados pessoais no histórico
- [ ] Specs/contrato atualizados **antes** do código (mudanças em DTO, rota ou coluna)

## Como subir localmente

Pré-requisitos: Docker com Compose v2 e `openssl` (Linux/macOS) ou PowerShell (Windows).

```bash
# 1. Gera secrets/ (um arquivo por segredo) e o .env de desenvolvimento. Não sobrescreve o que já existe.
infra/scripts/gerar-segredos.sh dev
#    Windows: .\infra\scripts\gerar-segredos.ps1 -Modo dev

# 2. Sobe o stack inteiro e espera todos os serviços ficarem healthy.
docker compose -f docker-compose.yml -f docker-compose.dev.yml up -d --wait

# 3. (Opcional) Confere a Definição de Pronto contra o stack no ar.
infra/scripts/smoke-compose.sh
```

- **Aplicação:** `http://localhost:8080` (SPA, `/api`, `/hubs`, `/stream/radio.mp3`). É a única porta publicada pela base.
- **Admin semeado:** o e-mail está em `secrets/seed_admin_email` e a senha em `secrets/seed_admin_senha`. O primeiro login exige a troca da senha.
- **Portas de diagnóstico, só no dev e só em `127.0.0.1`:** SQL Server `1433`, console do MinIO `9001`, harbor do Liquidsoap `8005`.
- **Parar:** `docker compose -f docker-compose.yml -f docker-compose.dev.yml down`. Sem `-v`, os dados dos volumes são preservados.

O `docker-compose.dev.yml` precisa ser passado com `-f`; ele não é carregado sozinho. Este roteiro é só de **desenvolvimento**: em produção usa-se apenas o `docker-compose.yml`, com `gerar-segredos.sh prod`, sem as portas de diagnóstico e atrás de TLS. É o mesmo roteiro que o job `compose-smoke` do CI executa a cada PR. O `gerar-segredos.ps1` ainda não foi validado em Windows real.

Para rodar só o backend ou só o frontend fora do Docker, veja `backend/` (`dotnet build`, `dotnet test`) e [`frontend/README.md`](frontend/README.md).

## Segurança

WebRadio é um projeto **público no GitHub**. Nenhum segredo, credencial, e-mail pessoal ou IP pode ser commitado.

- 🔒 Política de segurança: [`SECURITY.md`](SECURITY.md) (o arquivo entra com o PR #31)
- 🔍 Auditoria de segurança: [`docs/specs/05-seguranca-auditoria.md`](docs/specs/05-seguranca-auditoria.md)
- 🚨 Relatar vulnerabilidade: pelo *private vulnerability reporting* do GitHub, na aba Security do repositório

## Especificações

| Arquivo | Escopo | Leitura obrigatória para |
|---------|--------|--------------------------|
| [`00-arquitetura.md`](docs/specs/00-arquitetura.md) | Visão macro, decisões, fluxos | Todos |
| [`01-dominio.md`](docs/specs/01-dominio.md) | Entidades, enums, máquinas de estado | Backend |
| [`02-api-rest.md`](docs/specs/02-api-rest.md) | Contratos REST, SignalR, RBAC | Backend, Frontend |
| [`03-schema-sqlserver.sql`](docs/specs/03-schema-sqlserver.sql) | DDL, índices, constraints | Backend, DBA |
| [`04-epico-1-tarefas.md`](docs/specs/04-epico-1-tarefas.md) | Pacote de tarefas do Épico 1 e pendências | Time |
| [`05-seguranca-auditoria.md`](docs/specs/05-seguranca-auditoria.md) | Auditoria de segurança (normativo) | Backend, DevSecOps |
| [`06-epico-2-tarefas.md`](docs/specs/06-epico-2-tarefas.md) | Pacote de tarefas do Épico 2 (grade, ao vivo, credencial de transmissão) | Time |

## Licença

WebRadio é licenciado sob a [MIT License](LICENSE).

---

**Versão**: Épico 1  
**Última atualização**: 2026-10-14  
**Mantido por**: Nexus (Planejador), Forge (Backend), Atlas (DBA), Prism (Frontend)
