# WebRadio

**WebRadio** é uma plataforma open-source de transmissão de rádio online 24/7 que combina streaming contínuo com moderação de pedidos musicais em tempo real. Ouvintes solicitam músicas sem autenticação, locutores aprovam e tocam, e o stream permanece ininterrupto via Icecast + Liquidsoap.

## Arquitetura

```
Internet
    ↓
 [Caddy :80/:443]       ← HTTPS (Let's Encrypt); só em produção (E5). No dev não há Caddy
    ↓
 [nginx gateway :8080]  ← SPA React, /api/*, /hubs/*, /stream/* (no dev, publicado direto)
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
| **Worker** | Eleição de líder, manutenção diária e transições de grade (`Agendado → AoVivo → Finalizado`), pipeline de mídia enviada (confere, passa pelo antivírus, entrega ao `sanitizer`) e limpezas da biblioteca | .NET 10 background service |
| **db-init** | Criação do schema, logins SQL | one-shot sqlcmd |
| **migrator** | Aplicação de migrations EF | one-shot efbundle |
| **Icecast** | Distribuição do stream HTTP | Icecast 2 |
| **Liquidsoap** | Motor de playout, fallback, ao vivo | Liquidsoap 2.x |
| **harbor-borda** | Proxy na frente do harbor do Liquidsoap: lista de IPs (`HARBOR_PERMITIDOS`) e limite de conexões simultâneas para o locutor ao vivo | nginx unprivileged |
| **MinIO** | Armazenamento S3 (mídia, uploads) | MinIO |
| **sanitizer** | Recodifica o áudio enviado para MP3, sem rede e sem segredos; só a saída entra na biblioteca | ffmpeg mínimo compilado + shell script |
| **clamd / freshclam** | Antivírus do upload, na rede `antivirus` (só Worker e `clamd`); `freshclam` separado atualiza as assinaturas | ClamAV |
| **Redis** | Throttle de pedidos, backplane SignalR | Redis 7 |
| **nginx** | Gateway, reverse proxy, segurança | nginx unprivileged |

## Estrutura do repositório

```
webradio/
├── docs/
│   ├── specs/                     # Especificações (arquitetura, domínio, API, schema, tarefas por épico, segurança)
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
│   │   ├── features/              # Por feature: admin, auth, player, grade, pedidos, mural, estudio
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
│   ├── liquidsoap/                # radio.liq, playlist de teste, teste.sh
│   ├── harbor-borda/              # proxy do harbor (ao vivo), entrypoint e teste.sh
│   ├── perf/                      # medição dos planos da grade (E2-A02)
│   ├── minio/                     # init-buckets.sh, políticas S3
│   └── redis/                     # entrypoint de configuração
├── docker-compose.yml             # Base de produção (segredos em arquivo, só o Caddy publica: 80 e 443)
├── docker-compose.dev.yml         # Desenvolvimento (deve ser usado explicitamente com -f)
├── .env.example                   # Configuração NÃO secreta; os segredos ficam em secrets/ (fora do git)
├── .gitattributes                 # Scripts shell com LF em Windows
├── .gitignore                     # .env, .maestri/, node_modules/
├── SECURITY.md                    # Política de segurança e divulgação
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

### Entregue depois (B3, PR #31)

Jobs de CI com SQL Server real, verificação de DDL, frontend, testes de infra e varredura de vulnerabilidades; pendências dos reviews do B2; imagens-base atualizadas; `SECURITY.md`. Os testes Playwright rodam no `compose-smoke`.

### Entregue depois (E4-F01, PR #95)

O pendente de atualizar as imagens do SQL Server e do Liquidsoap antes do upload foi fechado: as imagens construídas (Liquidsoap e `harbor-borda`) e as de SQL Server, Redis e MinIO não têm CRITICAL corrigível, e o trivy do CI falha se aparecer uma. O alpine do `minio-init` e os HIGH seguem só informativos (risco aceito (2) do B3, no `05`, §4).

### Entregue depois (hotfixes, PRs #128, #134, #135 e #150)

- **#128**: a `main` ficou vermelha por NU1903 (DoS no ASP.NET Core, GHSA-wr8v-9x5q-ggcm): pacotes Microsoft.* e imagens `dotnet/*` foram para a 10.0.15, e o Dependabot passou a ignorar o major (11.x) desses pacotes. O mesmo PR corrigiu `libxslt` r4 no `web` e no `harbor-borda`, e o `minio-init`, que quebrava quando a secret key gerada começava com `-`.
- **#134**: o `minio-init` morria por falta de memória com `mem_limit: 128m` (o `mc` passa disso no `policy attach`); passou para 256m. O `ler` do `minio-init` recusa segredo que começa com `-` sem imprimir o valor, e o job de Infra ganhou `infra/minio/teste.sh`.
- **#135**: o gitleaks isenta o `65532:65532` (uid do sanitizer) no `smoke-midia.sh`, falso positivo que derrubava todo PR.
- **#150**: o `liquidsoap` do Compose passou para `mem_limit: 512m`. O boot dele pica em ~420 MiB e o contêiner era morto (`Restarting (137)`) quando o runner não deixava usar swap; era a causa do #139. O `smoke-compose.sh` afirma os mínimos de memória do Liquidsoap, do Worker (768m, do D2) e do `minio-init` (256m, do #134).
- **Dependabot**: #142, #147, #148 e #149 na `main`. O #144 (`@microsoft/signalr` 11) fica segurado até o .NET 11.

### Entregue depois (segurança de auth, limites por IP e tempo real)

- **#159, segurança de login antes do deploy** (fecha #9, #17 e #18): a família do refresh token tem teto absoluto de 30 dias (`FamiliaCriadaEmUtc`; a rotação além do teto vira 401 e novo login); o pepper deriva uma subchave por uso com HKDF (`ip`, `pseudonimo`, `chave`, `device`, `refresh`), então o pepper cru não entra mais em HMAC; e o log `AUTH_401_TAXA` conta os 401 do `/auth/login` por minuto. O semáforo global de PBKDF2 (503 com `Retry-After`) já estava na `main`. Os hashes de IP e de dispositivo e os contadores de throttle zeram na troca, e sessões com família de mais de 30 dias pedem novo login uma vez.
- **#161 e #162, limites por IP para ouvintes atrás de NAT** (escola, empresa e CGNAT dividem um IP): global da API 600/min (era 100) e `/auth/login` 30/min (era 5); no nginx, `/stream/` 25 conexões por IP, `/hubs/` 50, `/api/v1/auth/` 10 r/s com burst 30 e `worker_connections` 4096. O limite global é configurável (`RateLimit__LimiteGlobalPorMinuto`): fora de Development a API recusa subir com valor acima de 600; o `docker-compose.dev.yml` sobe com 2000 para o smoke e o E2E. Não mudam: bloqueio por conta e IP, semáforo de PBKDF2, throttle de pedidos, registrar (3/h), trocar senha (10/min) e sessão (30/min). A capacidade do Icecast (200 clientes) se mede no deploy.
- **#160, hub em tempo real** (fecha #146): o frontend relê o estado da rádio por REST depois de cada conexão do hub, não só na reconexão; antes um evento na janela entre o handshake e a entrada nos grupos se perdia. A janela diminui, mas não some: fechá-la pede um sinal do servidor depois do `OnConnectedAsync`.
- **#156, índice único de `Reproducao`** (fecha #151): `UX_Reproducao_PedidoId` filtrado por `PedidoId`, com a migration `20261122181219_IndiceUnicoReproducaoPedido` (remove duplicados e cria o índice). O `FaixaIniciada` trata a violação como evento já tratado.
- **Runbook novo**: [`docs/runbooks/auth-401-taxa.md`](docs/runbooks/auth-401-taxa.md) explica o `AUTH_401_TAXA`. Com o limite de 30/min por IP, um IP sozinho nunca chega ao `Warning` de 1/s, então um *password spraying* de um IP só fica em `Information`; o runbook diz como reconhecê-lo.

### Pendente

- Execução do `gerar-segredos.ps1` em Windows real. (A subida da base de produção num job existe desde o Épico 5: `compose-producao`, atrás do Caddy em `tls internal`.)
- Reescrita do runbook de rotação de segredos (ver o aviso no topo dele).

## Status do Épico 2

**Fechado em 2026-10-29.** O Épico 2 entrega o motor de transmissão: grade de programas, locutor ao vivo e estado da rádio em tempo real. A DoD conferida item a item, com o PR que entrega cada um, está na seção "Fechamento do Épico 2" de [`docs/specs/06-epico-2-tarefas.md`](docs/specs/06-epico-2-tarefas.md).

- **Grade**: o Admin cria, edita e cancela programas sem sobreposição (409 com `programaConflitanteId`). O Worker muda o status sozinho (`Agendado → AoVivo → Finalizado`), e o selo "Ao Vivo" troca na tela sem recarregar.
- **Ao vivo com credencial de transmissão**: o locutor gera, no painel dele, uma credencial própria (identificador + segredo, mostrado uma vez e guardado só como hash) e conecta o source client (BUTT, Mixxx) no harbor do Liquidsoap. Só o locutor dono do programa na janela de horário entra; o ao vivo cai quando o programa termina ou é encerrado, sem derrubar o stream.
- **Alerta do harbor**: tentativas de credencial inválida geram o `AlertaHarborFalhas`, exibido no painel do Admin e do locutor; ele é de melhor esforço (ver os riscos aceitos no `06`).
- **Produção**: só o Caddy (`edge`) publica portas, a `80` (redireciona) e a `443` (HTTPS); o `web` (nginx) não publica nada. A porta do ao vivo fica atrás do `harbor-borda`, em compose explícito, e o `HARBOR_PERMITIDOS` em produção deve ter só o IP do locutor.

**Fora do Épico 2:** a fila de pedidos (`GET /internal/playout/proxima` segue respondendo 204) e a biblioteca de mídia; a rotação ainda usa a playlist local.

## Status do Épico 3

**Fechado em 2026-11-08.** O Épico 3 entrega pedidos de música com moderação e divulgações com imagem opcional. O pacote de tarefas, com a Definição de Pronto conferida item a item e o PR que entrega cada um (registrada no #91), está em [`docs/specs/07-epico-3-tarefas.md`](docs/specs/07-epico-3-tarefas.md).

| Pacote | Entrega | PR |
|---|---|---|
| C1 | Backend de pedidos: criar, consultar, moderar, expirar (Worker) e retenção LGPD | #81 |
| C2 | Backend de divulgações: CRUD com `ETag`, envio de imagem (recodificada para WebP, sem metadados) e ciclo de vida da imagem no MinIO | #82 |
| C3 | Telas: pedir música, "Meus pedidos", fila de moderação no Estúdio e Mural | #84 |

- **Pedidos**: o ouvinte pede sem conta; o throttle por dispositivo e por IP responde 429 com `Retry-After`, com teto global de `Pendente`. A fila de moderação e "Meus pedidos" atualizam em tempo real. O pedido sem resposta expira em 120 min.
- **Divulgações**: o Admin gerencia; o Mural mostra só as ativas, por prioridade.
- **Fora do Épico 3**: o pedido aprovado ainda não toca sozinho (`GET /internal/playout/proxima` segue em 204): o locutor toca e marca como tocado. O playout do aprovado fica para o Épico 4.

## Status do Épico 4

**Em andamento: os quatro pacotes (D1 a D4) e o A01b do Atlas estão na `main`; falta conferir a Definição de Pronto item a item.** O Épico 4 entrega biblioteca de mídia, upload, sanitização e playout: locutor e Admin enviam áudio direto ao storage, o Worker passa o original pelo antivírus e o `sanitizer` recodifica para MP3; só a saída recodificada entra na biblioteca. O pedido aprovado com mídia passa a tocar sozinho pelo playout, e o Admin ganha o relatório de mais tocadas. O pacote de tarefas, as decisões e a Definição de Pronto estão em [`docs/specs/08-epico-4-tarefas.md`](docs/specs/08-epico-4-tarefas.md).

| Pacote | Entrega | PR | Situação |
|---|---|---|---|
| Spec | Pacote de tarefas do Épico 4 (v1.6: o que o D2 mostrou, com os reviews do Guardian, do Sentinel e do Atlas) | #92, #108, #141 | Na `main` |
| Spec | v1.7: travas de apagão (`midia/` e quarentena), índice único de `Reproducao` e a regra de testes | #154 | Na `main` |
| D1 | Imagens de terceiros sem CRITICAL corrigível e trivy bloqueando (E4-F01) | #95 | Na `main` |
| D2 | Upload, sanitização, ClamAV, biblioteca e limpezas (backend): E4-F02 a F07, F09 e o `smoke-midia.sh` (F10, parte de upload) | #112 | Na `main` |
| D3 | Playout pela biblioteca e relatório de mais tocadas (backend): A01a (chave de sorteio e índices), E4-F08, E4-A02 e a parte de playout do F10 | #111, #143 | Na `main` |
| D4 | Telas: biblioteca, envio, aprovar com mídia, mais tocadas (E4-P01 a P05) | #110 | Na `main` |
| A01b | Planos de execução com 50.000 mídias e 2 milhões de `Reproducao` (Atlas, parecer em [`docs/pareceres/e4-a01-midia.md`](docs/pareceres/e4-a01-midia.md)): o índice `IX_ArquivoMidia_EnviadoPor` passa a cobrir os limites de upload (de 1.116 a 2.375 leituras por envio para 3 a 12) e a rotação passa a sortear uma entre as 5 primeiras candidatas (a chave fixa favorecia umas faixas em bibliotecas grandes) | #174 | Na `main` |
| Pendências do D2 | Piso na trava de `midia/`, trava própria da quarentena, motivo `sanitizer` e piso do `INCRBY` (issues #136, #137, #138, #140 e #153), com o runbook do Worker | #152, #157 | Na `main` |
| Índice único de `Reproducao` | `UX_Reproducao_PedidoId` filtrado, para o `FaixaIniciada` concorrente (issue #151) | #156 | Na `main` |

- **Já na API**: `POST /midias/uploads` e `/midias/{id}/concluir` (envio direto ao storage por POST policy, com limites de 20 por hora e 5 abertos por usuário), `GET /midias` e `GET /midias/{id}`, preview, `PATCH`/`DELETE`, o pipeline do Worker e as limpezas da manutenção. O contrato está na §6 de [`docs/specs/02-api-rest.md`](docs/specs/02-api-rest.md).
- **Playout (D3)**: `GET /internal/playout/proxima` devolve o pedido aprovado com mídia (E4-D5) ou, sem pedido, uma faixa da rotação (E4-D4: pesos 8/2/1 entre `Musica`, `Vinheta` e `Comercial`, com janelas de repetição; o sorteio escolhe uma entre as 5 primeiras candidatas, #174); a URL é pré-assinada interna de 30 min e o cabeçalho `X-Playout-Instancia` é obrigatório. `FaixaIniciada` grava a `Reproducao`, marca o pedido como `Tocado` e publica `PedidoTocado` e `TocandoAgora` com `tipoMidia`. `GET /relatorios/mais-tocadas` (Admin) lê a `rel.usp_MaisTocadas`.
- **Telas (D4)**: `/biblioteca` (Locutor e Admin: lista, filtros, status ao vivo, pré-escuta), envio por policy com progresso, fila e 429 com contagem regressiva, edição com `If-Match`, "Aprovar com mídia" na moderação e `/admin/mais-tocadas`.
- **Pendências do D2 (resolvidas)**: piso de 5 objetos na trava contra apagão de `midia/` (#136), trava própria na varredura da quarentena, de 30 min, com limite absoluto de 50 e o motivo `trava_apagao_quarentena` (#137 e #153), o marcador `WORKER_DEGRADADO motivo=sanitizer` (#138) e o piso do `INCRBY` negativo no throttle (#140). O Worker recusa `LimiteExclusao` acima de 500. O smoke não testa a recusa pela quota do bucket `quarentena` (o MinIO só a aplica depois da medição dele); confere que a quota está configurada.
- **Operação do Worker**: o que fazer com cada `WORKER_DEGRADADO` (`scanner`, `assinatura_velha`, `sanitizer`, `trava_apagao`, `trava_apagao_quarentena`) está em [`docs/runbooks/worker-degradado.md`](docs/runbooks/worker-degradado.md), com a saída do operador nas duas travas.
- **Índice único de `Reproducao` (#151)**: `UX_Reproducao_PedidoId` filtrado por `PedidoId`, definido na spec v1.7 e entregue pelo Atlas no #156, na `main`.
- **Regra de testes (2026-11-22)**: teste automatizado novo só para auth/segurança e dados/SQL; o resto é teste manual. O smoke (`smoke-midia.sh`, seção DoD 2 e 8) segue cobrindo o pedido tocado pelo playout e a rotação; só a asserção de repetição em sequência saiu dele (#143), e o DoD 2 do playout passa a contar como teste manual do usuário.
- **Revisão**: todo PR do Épico 4 passa pelo Sentinel, por ser o épico do upload não confiável.

## Status do Épico 5

**Em andamento.** O Épico 5 fecha a v1 e leva a rádio para uma VPS própria: HTTPS pelo Caddy com Let's Encrypt e backup com cópia externa. O pacote de tarefas é [`docs/specs/09-epico-5-tarefas.md`](docs/specs/09-epico-5-tarefas.md) (spec: #165). **Como colocar no ar, passo a passo e escrito para quem não é desenvolvedor: [`docs/deploy.md`](docs/deploy.md).**

| Entrega | PR | Situação |
|---|---|---|
| Ajustes de produção no frontend (E5-P01): página 404, nome da rádio no título, na descrição, no favicon e no player (`NOME_RADIO` no `.env`) e aviso "A rádio volta em instantes." com o stream fora do ar | #166 | Na `main` |
| Backup e restauração do SQL Server (E5-A01): `RECOVERY SIMPLE` em produção, login `svc_backup` só com `db_backupoperator`, `BACKUP ... WITH CHECKSUM`, restauração pelo `sa` e teste com SQL real numa instância nova | #167 | Na `main` |
| Borda HTTPS (E5-F02) e job de CI da base de produção (E5-F05): serviço `edge` (Caddy), `web` sem porta publicada, IP real do ouvinte até o nginx e a API | #168 | Na `main` |
| Backup com restic (E5-F03): local e criptografado, cópia externa em S3 **opcional**, `restaurar` manual, timer `systemd` às 03:30 | #170 | Na `main` |
| `pos-restore.sql` confere papéis, usuários e `guest` do banco restaurado (E5-A01b) | #171 | Na `main` |
| Spec 09 v1.2: backup externo opcional, disco de 3× a biblioteca e o limite da cópia externa corrigido (E5-D11) | #172 | Na `main` |
| Guia de deploy para o dono do produto, [`docs/deploy.md`](docs/deploy.md) (E5-L01) | #173 | Na `main` |
| Robustez antes do deploy (E5-F04, fecha a issue #25): outbox com backoff por evento, banco fora e applock estourado em 503, índice do outbox sem eventos parados | #175 | Na `main` |

- **Produção**: só o Caddy publica portas (`80` e `443`). Certificado automático, HSTS de 1 dia no primeiro deploy (sobe para 1 ano depois de uma semana). Sem IPv6 na v1 (sem registro AAAA). Operação em [`docs/runbooks/borda-https.md`](docs/runbooks/borda-https.md).
- **Backup**: o serviço `backup` (perfil `backup`) grava o `.bak` do banco e o espelho de `midia/` e `publico/` num repositório restic local; **com a cópia externa ligada**, copia também para um bucket S3 versionado (`BACKUP_S3_ENDPOINT`, `BACKUP_S3_BUCKET` e `secrets/backup_s3_*`). Sem ela, o log traz o aviso `BACKUP_EXTERNO_NAO_CONFIGURADO` e só há cópia local. A perda máxima é de 1 dia. A `restic_senha` precisa ser guardada **fora do servidor**: sem ela nenhum backup abre. Operação, restauração e o caso de invasão suspeita (restaurar o ponto **anterior** à suspeita) estão em [`docs/runbooks/backup-e-restauracao.md`](docs/runbooks/backup-e-restauracao.md).
- **Robustez (#175)**: o outbox espera por evento (5 s, 10 s, 20 s, até 5 min; um evento ruim não atrasa os outros) e o índice `IX_EventoOutbox_Pendentes` ignora eventos esgotados (10 tentativas). Com o **banco fora**, ou com o applock estourado, a API responde **503 `/erros/servidor-ocupado` com `Retry-After: 5`** em vez de 500. O banco fica em `READ COMMITTED` (não ligue `ALLOW_SNAPSHOT_ISOLATION`; ver [`docs/conexao-sql-server.md`](docs/conexao-sql-server.md)). O serviço `restaurar` passou a ter `build:`, então a restauração funciona num servidor novo sem rodar o `backup` antes.
- **A cópia externa não é à prova de invasor**: a chave do backup precisa apagar, e quem a tiver apaga versões de forma definitiva; só o Object Lock protegeria, e ele fica desligado até a revisão de riscos (E5-N01).
- **Adiado para a v2**: a imagem de divulgação no `sanitizer` (E5-F01, decisão E5-D6).

## O que vem a seguir

- **Épico 4**: conferir a Definição de Pronto item a item.
- **Épico 5**: a revisão dos riscos aceitos com o dono do produto (E5-N01) e o deploy, depois do checklist de segurança do `05` na base de produção (E5-S01).
- **v2**: imagem de divulgação no `sanitizer` (E5-F01), grade recorrente, métricas de audiência e busca de texto.

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

- **Aplicação:** `http://localhost:8080` (SPA, `/api`, `/hubs`, `/stream/radio.mp3`). É a entrada do dev, sem Caddy e sem HTTPS; a base de produção não publica a `8080` (só o Caddy, em `80` e `443`: [runbook da borda](docs/runbooks/borda-https.md)). Quem já tinha um `.env` de antes do Épico 5 precisa acrescentar `DOMINIO=localhost` e `ACME_EMAIL=dev@example.com` (o `.env.example` mostra).
- **Admin semeado:** o e-mail está em `secrets/seed_admin_email` e a senha em `secrets/seed_admin_senha`. O primeiro login exige a troca da senha.
- **Portas de diagnóstico, só no dev e só em `127.0.0.1`:** SQL Server `1433`, console do MinIO `9001`, harbor do Liquidsoap `8005`.
- **Parar:** `docker compose -f docker-compose.yml -f docker-compose.dev.yml down`. Sem `-v`, os dados dos volumes são preservados.

O `docker-compose.dev.yml` precisa ser passado com `-f`; ele não é carregado sozinho. Este roteiro é só de **desenvolvimento**: em produção usa-se apenas o `docker-compose.yml`, com `gerar-segredos.sh prod`, sem as portas de diagnóstico e atrás de TLS. É o mesmo roteiro que o job `compose-smoke` do CI executa a cada PR. O `gerar-segredos.ps1` ainda não foi validado em Windows real.

Para rodar só o backend ou só o frontend fora do Docker, veja `backend/` (`dotnet build`, `dotnet test`) e [`frontend/README.md`](frontend/README.md).

## Segurança

WebRadio é um projeto **público no GitHub**. Nenhum segredo, credencial, e-mail pessoal ou IP pode ser commitado.

- 🔒 Política de segurança: [`SECURITY.md`](SECURITY.md)
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
| [`07-epico-3-tarefas.md`](docs/specs/07-epico-3-tarefas.md) | Pacote de tarefas do Épico 3 (pedidos de música, moderação, divulgações) | Time |
| [`08-epico-4-tarefas.md`](docs/specs/08-epico-4-tarefas.md) | Pacote de tarefas do Épico 4 (biblioteca de mídia, upload, sanitização, playout) | Time |
| [`09-epico-5-tarefas.md`](docs/specs/09-epico-5-tarefas.md) | Pacote de tarefas do Épico 5 (borda HTTPS, backup, deploy) | Time |

## Operação e deploy

| Documento | Para quê |
|---|---|
| [`docs/deploy.md`](docs/deploy.md) | Guia de deploy para o dono do produto: VPS, domínio, segredos, bucket externo, primeira subida, conferência, backup, atualização e restauração |
| [`docs/runbooks/borda-https.md`](docs/runbooks/borda-https.md) | Caddy, certificado, HSTS e IP real do ouvinte |
| [`docs/runbooks/backup-e-restauracao.md`](docs/runbooks/backup-e-restauracao.md) | Backup, cópia externa, restauração e teste mensal |
| [`docs/runbooks/worker-degradado.md`](docs/runbooks/worker-degradado.md) | O que fazer com cada `WORKER_DEGRADADO` |
| [`docs/runbooks/auth-401-taxa.md`](docs/runbooks/auth-401-taxa.md) | O alerta de 401 do login |
| [`docs/runbooks/rotacao-jwt-signing-key.md`](docs/runbooks/rotacao-jwt-signing-key.md) | Rotação da chave de assinatura do login |
| [`docs/operacao-logs.md`](docs/operacao-logs.md) | Rotação e retenção dos logs |

## Licença

WebRadio é licenciado sob a [MIT License](LICENSE).

---

**Versão**: Épico 4 entregue; Épico 5 em andamento (spec 09 v1.2; borda HTTPS, backup, guia de deploy e robustez na `main`)  
**Última atualização**: 2026-11-23  
**Mantido por**: Nexus (Planejador), Forge (Backend), Atlas (DBA), Prism (Frontend), Lore (Documentação)
