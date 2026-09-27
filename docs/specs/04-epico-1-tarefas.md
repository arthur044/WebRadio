# Épico 1 — Fundação: pacote de tarefas

> Autor: Nexus · 2026-09-27 · Referências: `00-arquitetura.md`, `01-dominio.md`, `02-api-rest.md`, `03-schema-sqlserver.sql`
> Donos: **Forge** (backend + infra de contêineres), **Atlas** (banco), **Prism** (frontend).
> O Épico 1 **não** implementa regras de negócio de grade, pedidos ou upload (Épicos 2–4). Ele entrega a fundação rodando de ponta a ponta.
> **v1.1:** .NET 10 (D15) + auditoria do Sentinel (D16–D21). O `05-seguranca-auditoria.md` é **normativo**: cada achado que cita um ID de tarefa (E1-Fxx/Pxx/Axx) vira critério de aceite dessa tarefa, **além** do que está nesta tabela. A checklist da §8 do `05` é o quality gate de todo PR.

## Definição de pronto do Épico

1. **No job `compose-smoke` do GitHub Actions** (D23; runner `ubuntu-latest`, que funciona como a "máquina limpa"): `infra/scripts/gerar-segredos.sh` e depois `docker compose -f docker-compose.yml -f docker-compose.dev.yml up -d` sobem **todos** os serviços `healthy` em menos de 3 min. Os itens 2–4 e 8 abaixo são verificados por script nesse mesmo job (o item 2 com Playwright). Rodar numa máquina Windows local fica para quando houver um ambiente com virtualização. Sem `.env`, o `compose up` **falha** com mensagem clara (`${VAR:?}`).
2. `http://localhost:8080` abre a SPA, e o player toca `/stream/radio.mp3` (áudio de teste do Liquidsoap) **sem interromper** ao navegar entre Mural, Grade e Pedir Música.
3. Login com o Admin semeado funciona e exige a troca de senha no primeiro acesso. `GET /api/v1/auth/me` responde 200 com o token e 401 sem ele.
4. `/health/ready` na porta interna `:8081` responde `Healthy` com SQL, MinIO e Redis. Pela `:8080` pública, `/health/*` e `/api/v1/internal/*` (em qualquer grafia) respondem 404.
5. O schema do banco é criado pelo `db-init` + migrations e equivale ao `03-schema-sqlserver.sql` (com o aceite do Atlas).
6. `dotnet test` e `npm test` passam; o teste de arquitetura passa; a cobertura dos projetos Domain e Application é ≥ 80%.
7. Nenhum segredo versionado; `.env.example` só com `__GERAR__`; cada serviço recebe só os próprios segredos; as APIs .NET recusam subir com segredo ausente, placeholder ou fraco.
8. As portas publicadas são só a **8080** (e, no `docker-compose.dev.yml`, `127.0.0.1` para 1433, 9001 e 8005).

## Estrutura do repositório (alvo)

```
webradio/
├─ docs/specs/                      # esta especificação
├─ backend/
│  ├─ WebRadio.sln
│  ├─ Directory.Build.props         # net10.0 (+ global.json fixando SDK 10.0.x), Nullable, TreatWarningsAsErrors, AnalysisLevel latest
│  ├─ Directory.Packages.props      # Central Package Management
│  ├─ src/WebRadio.{Domain,Application,Infrastructure,Api,Worker}/
│  └─ tests/WebRadio.{Domain,Application,Architecture}.Tests/, WebRadio.Api.IntegrationTests/
├─ frontend/                        # Vite + React + TS + Tailwind
├─ infra/
│  ├─ icecast/ (Dockerfile, icecast.xml.template)
│  ├─ liquidsoap/ (radio.liq, playlist-teste/)
│  ├─ nginx/ (default.conf.template)
│  ├─ minio/ (init-buckets.sh, policies/*.json)
│  ├─ sql/ (init-db.sql, logins.sql, seed-dev.sql)
│  ├─ redis/ (entrypoint que gera redis.conf com requirepass)
│  └─ scripts/ (gerar-segredos.sh, gerar-segredos.ps1)
├─ docker-compose.yml               # produção-like: sem portas além da 8080, secrets em arquivo
├─ docker-compose.dev.yml           # uso explícito com -f (NÃO é "override", S-M12)
├─ .env.example                     # só __GERAR__ nos segredos
└─ .gitattributes / .editorconfig / .gitignore
```

## Serviços do docker-compose

Redes (D19): `borda` (web) · `app` (web, api:8080; **subnet fixa** em `ipam`, usada em `KnownNetworks`) · `dados` (api, worker, migrator, db-init, sqlserver, redis, minio, minio-init) · `playout` (api:8081, liquidsoap, icecast, minio). `web` também entra em `playout` só para encaminhar `/stream/` ao Icecast e `/storage/` ao MinIO.

| Serviço | Imagem | Redes | Porta publicada | Segredos que recebe (só estes) | Healthcheck |
|---|---|---|---|---|---|
| `sqlserver` | `mcr.microsoft.com/mssql/server:2022-<CU fixada>` | dados | 1433 só no `dev.yml`, `127.0.0.1` | `MSSQL_SA_PASSWORD` (entrypoint wrapper lê de `/run/secrets`) | `sqlcmd` com a senha lida de **arquivo**, nunca escrita no YAML |
| `db-init` | `mssql-tools` | dados | — | SA + `DB_MIGRATOR/APP/RELATORIO_PASSWORD` | one-shot: `init-db.sql` + `logins.sql` (Atlas) |
| `migrator` | build `backend` (efbundle) | dados | — | `DB_MIGRATOR_PASSWORD` | one-shot, depois do `db-init` |
| `minio` | `minio/minio` (tag fixada), `user: 1000:1000` | dados, playout | **nenhuma**; 9001 (console) só no `dev.yml`, `127.0.0.1` | `MINIO_ROOT_*` (`_FILE`) | `mc ready local` |
| `minio-init` | `minio/mc` | dados | — | root + chaves de `svc-api`/`svc-worker` | one-shot: buckets `quarentena`, `midia`, `publico` + políticas JSON do S-A04/S-A05 + quota da quarentena |
| `redis` | `redis:7-alpine` | dados | — | `REDIS_PASSWORD` | `redis-cli -a` lendo de arquivo `ping` |
| `api` | build → `WebRadio.Api` | app, dados, playout | — | JWT, pepper, `Playout__Token(+Anterior)`, `DB_APP_PASSWORD`, `svc-api`, Redis, seed do Admin | `:8081/health/ready` |
| `worker` | build → `WebRadio.Worker` | dados | — | `DB_APP_PASSWORD`, `svc-worker`, Redis (**sem** pepper nem JWT) | `:8081/health/live` interno |
| `sanitizer` | `profiles: [sanitizacao]` no Épico 1 (imagem no Épico 4) | **`network_mode: none`** | — | **nenhum** | — |
| `icecast` | build `infra/icecast` (Alpine, sem root) | playout | — | `ICECAST_SOURCE/ADMIN/RELAY_PASSWORD`, `ICECAST_ADMIN_USER` | `wget /status-json.xsl` |
| `liquidsoap` | `savonet/liquidsoap` (tag 2.x fixada, conferir se roda sem root) | playout | 8005 só no `dev.yml` em `127.0.0.1`; em prod, só com allowlist/VPN (S-A08) | `ICECAST_SOURCE_PASSWORD`, `LIQUIDSOAP_HARBOR_PASSWORD`, `Playout__Token` | mount ativo no Icecast |
| `web` | build `frontend` (Node → `nginxinc/nginx-unprivileged`) | borda, app, playout | **8080** | nenhum | `wget /` |

Regras: toda imagem com **tag ou digest fixado**; em todos os serviços, `user` não root, `cap_drop: [ALL]` (+ `cap_add` justificado), `security_opt: [no-new-privileges:true]`, `read_only: true` + `tmpfs` onde der, `mem_limit`/`pids_limit` (S-B08); segredos obrigatórios como `${VAR:?}` em dev e `secrets:` em produção (§5 do `05`); **nada de `env_file` compartilhado**; `ASPNETCORE_ENVIRONMENT=Production` explícito no `docker-compose.yml`; `restart: unless-stopped`; volumes nomeados para `sqlserver`, `minio` e `redis`.

---

## Tarefas — Forge (Backend e contêineres)

| ID | Tarefa | Depende | Critério de aceite |
|---|---|---|---|
| **E1-F00** | `git init`, `.gitignore` (.NET + Node + `.env`), `.gitattributes` (`*.sh text eol=lf`, `*.liq text eol=lf`), `.editorconfig` | — | Scripts shell rodam no contêiner mesmo com checkout no Windows (sem CRLF) |
| **E1-F01** | Solution com os 5 projetos + 4 de teste, `Directory.Build.props`, Central Package Management | F00 | `dotnet build` sem warnings |
| **E1-F02** | Teste de arquitetura (NetArchTest): Domain não referencia nada; Application não referencia Infrastructure/Api; Api não usa `DbContext` fora da Infrastructure | F01 | Teste falha se alguém violar a regra (fazer uma violação temporária e confirmar que o teste reprova) |
| **E1-F03** | **Domain**: enums com valores fixos (`01-dominio.md §1`), entidades `Usuario`, `RefreshToken`, `Programa`, `PedidoMusica`, `ArquivoMidia`, `Divulgacao`, `Reproducao` com construtores e métodos de transição; erros de domínio. TDD nas máquinas de estado | F01 | Testes cobrem cada transição válida e inválida; cobertura ≥ 90% no Domain |
| **E1-F04** | **Infrastructure/EF**: `RadioDbContext`, uma `IEntityTypeConfiguration` por entidade fiel ao `03-schema-sqlserver.sql` (schemas, nomes de constraint, índices filtrados com `HasFilter`, `rowversion`, `datetime2(0\|3)`, conversão de enum para tinyint), migration `Inicial`, `EnableRetryOnFailure` | F03, **A01** | O script `dotnet ef migrations script --idempotent` foi aprovado pelo Atlas (E1-A03) |
| **E1-F05** | **db-init + Migrator** (D20): `db-init` (one-shot com `sqlcmd`, **único** com a SA) roda `init-db.sql` + `logins.sql` do Atlas; `migrator` (efbundle com `webradio_migrator`) aplica as migrations | F04, A02, A04 | Rodar 2× seguidas é idempotente; `api`/`worker` só sobem depois; a SA não aparece em nenhuma outra definição de serviço |
| **E1-F06** | **Api (esqueleto)**: Minimal APIs agrupadas por feature, `/api/v1`, ProblemDetails + tratamento global de exceção (mapeando `01-dominio.md §3`), `JsonStringEnumConverter`, pipeline MediatR `Logging → Validation`, Serilog JSON (sem PII: nunca logar e-mail, senha, token ou IP em claro), OpenAPI + Scalar em Development, RateLimiter global. **v1.1:** dois listeners Kestrel (`:8080` público, `:8081` interno, D17); ForwardedHeaders com `KnownNetworks` = subnet da rede `app`, `ForwardLimit = 1` (S-A11); `FallbackPolicy` autenticado; `AddKeyPerFile("/run/secrets")` + `IValidateOptions`/`ValidateOnStart`, que **recusam subir** com segredo vazio, `__GERAR__` ou abaixo do mínimo (S-C02); o Serilog mascara `*Password*/*Senha*/*Token*/*Key*/*Pepper*` e não registra query string | F01 | Teste de integração: erro de validação → 400 ProblemDetails com `errors`; XFF forjado de fora da `KnownNetworks` é ignorado; boot com `Jwt__SigningKey=__GERAR__` falha |
| **E1-F07** | **Auth completo** (`02-api-rest.md §2–3`): `PasswordHasher`, emissão/validação do JWT, refresh rotativo com UPDATE atômico + janela de 10 s + detecção de reuso por família (S-M10), cookie `__Secure-wr_refresh` + checagem de `Origin`, bloqueio por (conta, IpHash) em Redis e hash fictício para e-mail inexistente (D21), `POST /auth/trocar-senha` + `DeveTrocarSenha`, revogação de sessões em papel/desativação/troca de senha (S-M11), políticas `PodeModerar`/`SomenteAdmin`, **seed do Admin** a partir de `Seed__AdminEmail`/`Seed__AdminSenha` (só se não existir nenhum Admin; nasce com `DeveTrocarSenha = true`). Requisitos de `TokenValidationParameters` na §6 do `05` | F04, F06, F09 | Testes de integração (Testcontainers SQL + Redis): login ok/erro; token expirado, `alg: none`, outra chave ou `role` adulterada → 401; reuso de refresh revoga a família; duas rotações concorrentes → uma vence; Ouvinte em rota Admin → 403; 6ª falha do mesmo IP → **401** (mesmo corpo e tempo equivalente ao de e-mail inexistente) e outro IP ainda loga; desativar → o refresh seguinte → 401 |
| **E1-F08** | **Health checks**: `/health/live`, `/health/ready` (SQL, MinIO, Redis; Redis → Degraded), **só na `:8081`** (S-B05) | F06 | Derrubar o Redis deixa `ready` = Degraded, não 503; `/health/ready` pela `:8080` → 404 |
| **E1-F09** | **Abstrações de infraestrutura** (interfaces na Application, implementações na Infrastructure): `IClock`, `IObjectStorage` (clientes **interno e público**, `CriarPostPolicy`, `UrlGetPreAssinada`, `Head`, `Mover`), `IRequestThrottle` (Redis `SET NX EX` / `INCR`), `IListenerIdentity` (HMAC com `Seguranca__Pepper`), `IRealtimeNotifier`. **v1.1:** `IAudioSanitizer` e `IMalwareScanner` (só as interfaces; `NoOpMalwareScanner` registrado **apenas** com `IsDevelopment()` **e** `Seguranca:PermitirScannerNoOp=true`, S-A02); a POST policy com condições **exatas** do S-M01; o cliente público assina para `<origem>/storage` (D18); a API usa a credencial `svc-api`, nunca a root (S-A04); Redis autenticado | F06 | Testes de integração (Testcontainers MinIO): a policy aceita upload dentro do limite, **rejeita** acima do `content-length-range` e rejeita `key` alterada no form (403); o `svc-api` **não** consegue `DeleteObject` em `midia/` |
| **E1-F10** | **SignalR** `/hubs/radio`: auth por `access_token` na query, grupos atribuídos no servidor (`locutores`, `listener:{hash}`), backplane Redis; **OutboxDispatcher** (`BackgroundService` na API) com `UPDLOCK, READPAST` | F07, F09 | Teste: evento inserido no outbox chega a um cliente SignalR de teste e fica `ProcessadoEmUtc` preenchido; reprocessar não reenvia |
| **E1-F11** | **Worker (esqueleto)**: host genérico, eleição de líder por `sp_getapplock` de sessão (`webradio-worker-leader`, timeout 0) com reconexão, loop `PeriodicTimer` 10 s que registra heartbeat, job diário de manutenção (as rotinas da seção 11 do SQL, em lotes) | F04 | Dois workers no ar: só um vira líder; matar o líder faz o outro assumir em ≤ 30 s |
| **E1-F12** | **Endpoints internos stub**: `GET /internal/playout/proxima` → 204 e `POST /internal/playout/eventos` → 202 (atualiza só o "tocando agora" em memória/Redis e publica `TocandoAgora`), com Bearer `Playout__Token`, **só no listener `:8081`** (`RequireHost`), comparação com `FixedTimeEquals` e aceitação de `Playout__TokenAnterior` (S-A06). Metadados validados (≤ 200 caracteres, sem controle, S-A09) | F06, F10 | Sem token → 401; pela `:8080`, `/api/v1/internal/x`, `/api/v1/INTERNAL/x`, `/api/v1//internal/x`, `/api/v1/%69nternal/x` e `/api/v1/internal` → **404** |
| **E1-F13** | **Dockerfiles** multi-stage para Api, Worker e Migrator (SDK → `aspnet:10.0` / `runtime:10.0`, `USER app`), `.dockerignore` | F06, F11 | Imagem da API < 150 MB; `docker run` roda sem root |
| **E1-F14** | **Icecast**: Dockerfile próprio (Alpine), `icecast.xml` gerado de template com senhas de `.env` (`ICECAST_SOURCE_PASSWORD`, `ICECAST_ADMIN_PASSWORD`), mount `/radio.mp3`, `<hidden>` no admin, limites de clientes/fonte. **v1.1:** `ICECAST_ADMIN_USER` ≠ `admin`, `<sources>2</sources>`, `<source-timeout>`, template renderizado em `tmpfs` no entrypoint (checa placeholder e tamanho mínimo), logs com IP truncado e retenção de 14 dias (S-A07, S-B04) | F00 | `curl -I http://localhost:8080/stream/radio.mp3` → 200 `audio/mpeg`; `/stream/admin/` e `PUT /stream/radio.mp3` → 404/403 |
| **E1-F15** | **Liquidsoap** `radio.liq` (esqueleto do Fluxo D): `fallback(track_sensitive=false, [input.harbor("live", port=8005, password=env), fila_api, playlist("/musica-teste", mode="randomize"), sine()])`; `fila_api` = `request.dynamic` chamando `/internal/playout/proxima` (204 → sem faixa); `on_track` → POST `/internal/playout/eventos`; `output.icecast(%mp3(bitrate=128), mount="radio.mp3")`. **v1.1:** a API é chamada em `http://api:8081`; a `url` recebida só é aceita com o prefixo `http://minio:9000/midia/`; metadados via `metadata.map`, **nunca** concatenados em `annotate:` (S-A09); senhas lidas com `file.contents("/run/secrets/...")`; log de conexão e desconexão do harbor sem a senha (S-A08). Pasta `playlist-teste/` com 2–3 faixas **livres de direitos** (ou gere um tom com ffmpeg) | F12, F14 | Stream toca a playlist de teste; derrubar a `api` **não** derruba o stream; conectar o BUTT em `:8005/live` sobrepõe a playlist |
| **E1-F16** | **`docker-compose.yml` + `docker-compose.dev.yml` + `.env.example`** conforme a tabela de serviços v1.1: 4 redes com a subnet de `app` fixa, segredos por serviço, `sanitizer` reservado em `profiles`, `db-init`, hardening de contêiner, `depends_on` com `condition: service_healthy`/`service_completed_successfully` | F05, F13, F14, F15, F18, A04, P06 | Definição de pronto itens 1–8; `docker compose config` não mostra nenhum segredo literal; o Liquidsoap **não** alcança `sqlserver:1433` nem `redis:6379` (teste com `nc` de dentro do contêiner) |
| **E1-F17** | **CI no GitHub Actions: ambiente de integração** (D23). **Prioridade antecipada**: a primeira versão entra logo depois da F02, e os jobs crescem junto com as tarefas. Repositório **privado** `arthur044/webradio`, branch padrão `main`, proteção da `main` exigindo os checks. Jobs: `seguranca` (gitleaks + grep `__GERAR__` no `.env.example`), `backend-unit` (build + unitários + arquitetura + cobertura), `backend-integracao` (`dotnet test --filter Categoria=Integracao`, Testcontainers), `ddl-check` (serviço `mssql/server:2022` + `sqlcmd -b` rodando `03-schema-sqlserver.sql` e o script `dotnet ef migrations script --idempotent`, com o resultado para o Atlas), `frontend` (lint, testes, build), `compose-smoke` (DoD 1–4, 8: segredos gerados **no runner**, `compose up --wait`, curl no stream, os 5 testes de bypass do S-A06, `/health` via `docker compose exec`, Playwright do player), `vulnerabilidades` (`trivy image`, `dotnet list package --vulnerable`, `npm audit --audit-level=high`). Nenhum segredo cadastrado no GitHub no Épico 1: tudo é gerado por execução. **Critérios obrigatórios na §9 do `05`** (gatilhos, permissões, Actions por SHA + dependabot, `::add-mask::`, `docker compose config --quiet`, cache só de NuGet/npm, e a checagem **pela saída** do `dotnet list package --vulnerable`, que sai com 0 mesmo havendo vulnerabilidade) | F02 (depois cresce com F05, F09, F16) | Pipeline verde no PR; um PR com segredo no `.env.example` ou com um teste de integração quebrado **não** faz merge |
| **E1-F18** | **Segredos** (S-C02, §5 do `05`): `infra/scripts/gerar-segredos.sh` **e** `.ps1` (o DJ usa Windows), que criam `.env` (dev) ou `secrets/*` (prod) com `openssl rand` / `RandomNumberGenerator`, respeitam os mínimos da tabela 5.1 e **se recusam a sobrescrever**; `.env.example` com `__GERAR__` e o comando de geração comentado; `.gitignore` com `.env`, `.env.*` (exceto `.example`) e `secrets/` | F00 | Rodar 2× não altera o `.env` existente; um `.env` gerado passa no fail-fast de todas as aplicações |

## Tarefas — Atlas (Banco de dados)

> ⚠️ O Atlas **não aparece conectado no canvas Maestri** (`maestri list`). O DJ precisa conectá-lo ou encaminhar este pacote a ele.

| ID | Tarefa | Depende | Critério de aceite |
|---|---|---|---|
| **E1-A01** | Revisar `03-schema-sqlserver.sql`: tipos, CHECKs, índices (sobretudo os filtrados e a consulta de sobreposição da grade), collation `Latin1_General_100_CI_AI_SC`. Devolver ajustes **antes** do E1-F04 | — | Documento de ajustes (ou "aprovado") comentado no arquivo, e o Forge notificado |
| **E1-A02** | `infra/sql/logins.sql`: principals `webradio_migrator`, `webradio_app`, `webradio_relatorio` com privilégio mínimo; senhas via variáveis do `sqlcmd` (`$(VAR)`), nunca literais | A01 | `webradio_app` não consegue `CREATE TABLE`; consegue `sp_getapplock` |
| **E1-A03** | Revisar o script da migration `Inicial` gerado pelo Forge contra o DDL de referência (nomes, índices filtrados, `rowversion`, `datetime2` com precisão certa) | F04 | Aprovação registrada no PR |
| **E1-A04** | `infra/sql/init-db.sql`: `CREATE DATABASE` com collation, `READ_COMMITTED_SNAPSHOT ON`, `ALLOW_SNAPSHOT_ISOLATION ON`, `COMPATIBILITY_LEVEL = 160`, recovery model por ambiente. Executado pelo **`db-init`** com a SA (D20), junto com o `logins.sql` | — | Idempotente (`IF DB_ID(...) IS NULL`) |
| **E1-A05** | Guia de connection string e pool: `Max Pool Size`, `Connect Timeout`, `Encrypt=True` (`TrustServerCertificate=True` **só** em dev), `Application Name` distinto para api, worker e migrator (identificar sessões em `sys.dm_exec_sessions`) | — | Seção no `docs/` e valores no `.env.example` |
| **E1-A06** | Seed de desenvolvimento (`infra/sql/seed-dev.sql`, **não** roda em produção): 2 locutores, grade de 7 dias sem sobreposição, 3 divulgações | A04, F05 | Aplicado só com `SEED_DEV=true` |
| **E1-A07** | Rascunho de `rel.usp_MaisTocadas @DeUtc, @AteUtc, @Top` sobre `midia.Reproducao` (entrega efetiva no Épico 3; aqui só validar que o índice serve) | A01 | Plano de execução com seek em `IX_Reproducao_Periodo` |

## Tarefas — Prism (Frontend)

| ID | Tarefa | Depende | Critério de aceite |
|---|---|---|---|
| **E1-P01** | Scaffold: Vite + React + TypeScript (strict) + Tailwind + React Router + TanStack Query; ESLint + Prettier; Vitest + Testing Library; Playwright. Pastas por feature (`src/features/{player,grade,pedidos,mural,estudio,auth}`, `src/shared`) | — | `npm run lint && npm test && npm run build` verdes |
| **E1-P02** | **Direção visual e tokens** (regras de design do projeto: nada de template genérico): definir paleta, tipografia e tokens CSS em `:root`, com claro e escuro intencionais. Registrar em `frontend/DESIGN.md` | P01 | Tokens usados pelos componentes-base; revisão do DJ |
| **E1-P03** | **Shell + player global**: layout raiz com `<PlayerBar/>` **fora** do `<Outlet/>`; um único `HTMLAudioElement` num store (Zustand ou contexto) apontando para `/stream/radio.mp3`; play/pause, volume e mudo persistidos (localStorage em try/catch); reconexão com backoff exponencial em `error`/`stalled`; Media Session API (título do "tocando agora") | P01 | **Teste Playwright**: dar play, navegar Mural → Grade → Pedir Música → Mural, e o `audio.paused` continua `false` com `currentTime` crescente e zero eventos `pause` |
| **E1-P04** | **Cliente de API tipado**: tipos gerados do `/openapi/v1.json` (`openapi-typescript`) + `fetch` wrapper; access token **só em memória**; em 401, **um único** refresh concorrente (single-flight) **também entre abas** via `navigator.locks.request('wr-refresh', …)` (S-M10), e repetição da requisição; ProblemDetails → erros tipados de UI | P01, F06 (pode começar com mock baseado no `02-api-rest.md`) | Teste: 3 requisições com 401 simultâneo disparam 1 refresh só; 2 abas com token expirado não derrubam a sessão |
| **E1-P05** | **Auth UI**: tela de login (erro sempre genérico: "e-mail ou senha inválidos"), tela obrigatória de **troca de senha** quando `deveTrocarSenha = true`, `RequireRole` para `/estudio` (Locutor/Admin), logout. Sessão restaurada no load via `/auth/refresh` | P04, F07 | Ouvinte que tenta `/estudio` vê 403 amigável; F5 mantém a sessão; o Admin semeado cai na troca de senha e não consegue navegar antes de trocar |
| **E1-P06** | **Dockerfile + nginx** (`infra/nginx/default.conf.template`): SPA com fallback `try_files`; proxy `/api/` → `api:8080`, `/hubs/` → `api:8080` com upgrade de WebSocket. **v1.1 (normativo, ver `05`):** `location ~* ^/api/v1/+internal(/|$) { return 404; }` **antes** de `/api/` e `/health/` → 404 (S-A06, S-B05); `/stream/`: só `location = /stream/radio.mp3` com `limit_except GET HEAD`, `proxy_buffering off`, e o resto → 404 (S-A07); `/storage/` → `minio:9000`, só `POST /storage/quarentena`, `GET/HEAD /storage/midia/*` e `/storage/publico/*`, `client_max_body_size 250m` só aí, `nosniff` + `CSP sandbox` em `publico` (S-A03, S-A05); `proxy_set_header X-Forwarded-For $remote_addr` (sobrescreve, S-A11); `limit_req`/`limit_conn`, `client_max_body_size 1m` em `/api/`, timeouts contra slowloris (S-M13); `security-headers.conf` com a CSP exata do S-M14 incluído em **todo** `location`, com `always`; `log_format` sem `$args` em `/hubs/` e com IP truncado (S-M09, S-B04) | P01 | A SPA carrega por `localhost:8080`; o stream toca pelo proxy sem cortar; os 5 testes de bypass do S-A06 → 404; `GET /storage/publico/` → 403/404; zero violações de CSP no console (Playwright) |
| **E1-P07** | **Cliente SignalR**: `@microsoft/signalr` com `withAutomaticReconnect`, `listenerId` (`crypto.randomUUID()` persistido) na query e no header `X-Listener-Id` de toda chamada à API; dedupe por `eventoId`; ao reconectar, invalidar as queries `programa-ao-vivo` e `stream-info` | P04, F10 | Evento de teste do outbox aparece na UI; o mesmo `eventoId` duas vezes aplica uma vez só |
| **E1-P08** | **Páginas placeholder** navegáveis: Mural (lista `/divulgacoes/ativas`), Grade (semana atual em horário de Brasília, convertendo para UTC em `deUtc/ateUtc`), Pedir Música (formulário desabilitado com "em breve"), Estúdio (vazio, protegido) | P03, P04 | Navegação completa sem recarregar a página |

## Ordem sugerida e paralelismo

```
Dia 1  Forge: F00→F01→F02→F03        Atlas: A01, A04, A05     Prism: P01→P02, P06 (nginx com api mock)
Dia 2  Forge: F04 (após A01)→F05     Atlas: A02, A03          Prism: P03, P04 (mock)
Dia 3  Forge: F06→F07→F08→F09        Atlas: A06, A07          Prism: P05, P08
Dia 4  Forge: F10→F11→F12→F13        —                        Prism: P07 (após F10)
Dia 5  Forge: F14→F15→F16→F17  +  teste integrado da Definição de Pronto (todos)
```

Caminho crítico: **A01 ✅ → F04 → F05 → F16**, e a **F17** (CI) entra logo depois da F02: sem ela, nada que depende de contêiner é verificado (D23). O Atlas precisa revisar o schema no primeiro dia. A F18 (segredos) entra no Dia 1 do Forge, logo depois da F00.
**Sentinel:** revisa os PRs de F07, F09, F12, F14–F18 e P06 com a checklist da §8 do `05`, antes do Guardian.

## Handoffs e revisão
- Cada tarefa termina em PR pequeno; o **Guardian** (Code-Review) revisa todos. Os PRs de SQL precisam também do aceite do Atlas.
- Ao fechar F07, F10 e P06, o **Lore** (Documentação) atualiza o README com o "como subir localmente".
- Mudança de contrato (DTO, rota, coluna) exige editar **primeiro** `docs/specs/` e avisar o Nexus. Código e especificação não podem divergir.
