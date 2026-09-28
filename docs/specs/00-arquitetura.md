# WebRadio — Especificação Técnica v1: Arquitetura

> Autor: Nexus (Planejador) · Data: 2026-09-27 · Versão: **v1.1** (aprovada pelo DJ)
> Base: nota `plano-base-webradio` + os 5 ajustes aprovados pelo DJ + a auditoria do Sentinel (`05-seguranca-auditoria.md`).
>
> **v1.1 (2026-09-27):** .NET 10 (D15). A auditoria do Sentinel foi incorporada (D16–D21). O `05-seguranca-auditoria.md` passa a ser **normativo**: tudo que ele recomenda vale como requisito da tarefa citada, salvo o que a tabela "Decisões sobre a auditoria" no fim deste arquivo diz de outro jeito.

Índice da especificação:

| Arquivo | Conteúdo | Público |
|---|---|---|
| `00-arquitetura.md` | Visão macro, decisões, fluxos, riscos | Todos |
| `01-dominio.md` | Entidades, enums, máquinas de estado, invariantes | Forge, Atlas |
| `02-api-rest.md` | Contratos REST, SignalR, endpoints internos, RBAC | Forge, Prism |
| `03-schema-sqlserver.sql` | DDL de referência do SQL Server | Atlas, Forge |
| `04-epico-1-tarefas.md` | Pacote de tarefas do Épico 1 | Forge, Atlas, Prism |

---

## 1. Requisitos (reescritos)

1. Tocar a rádio 24/7 por um stream HTTP (Icecast). O áudio vem do **Liquidsoap**, não da API.
2. Manter uma grade de **Programas** sem sobreposição, gravada em **UTC**. O selo "Ao Vivo" muda sozinho no horário, sem refresh.
3. Ouvintes pedem músicas sem precisar de conta, com **anti-spam de 5 min** por dispositivo e IP. O locutor modera em tempo real e o ouvinte é avisado da decisão.
4. Locutores e admins sobem vinhetas, comerciais e músicas. Só toca o arquivo que passou pela **sanitização** (tipo real, hash, duração, antivírus, recodificação).
5. **Autenticação JWT** com RBAC: `Admin`, `Locutor`, `Ouvinte`.

## 2. Arquitetura macro

```
                     Internet
                        │
                 ┌──────▼──────┐   (única porta pública em dev: 8080)
                 │  web (nginx)│── SPA React estática
                 │  gateway    │── /api/*   ─┐
                 └──┬───────┬──┘── /hubs/*  ─┤
          /stream/* │       │                │
             ┌──────▼──┐    │         ┌──────▼──────┐      ┌─────────┐
             │ icecast │    │         │  api (.NET) │◄────►│  redis  │ throttle + backplane SignalR
             └────▲────┘    │         └──┬───────┬──┘      └─────────┘
      source /radio.mp3     │            │       │
             ┌────┴──────┐  │  /internal │       │ EF Core
             │liquidsoap │──┼───────────►│       ▼
             └────┬──────┘  │     ┌──────────────────┐   ┌──────────────┐
                  │ GET presigned │   sqlserver      │◄──│ worker (.NET)│ grade, outbox, sanitização
                  ▼         │     └──────────────────┘   └──────┬───────┘
             ┌─────────┐◄───┘ POST policy (upload do browser)   │
             │  minio  │◄────────────────────────────────────────┘
             └─────────┘
```

### 2.1 Camadas (.NET 10 LTS, Clean Architecture)

> Alvo `net10.0` (decisão D15, 2026-09-27). O plano base pedia .NET 9, mas o .NET 9 é STS e o suporte termina em nov/2026. O .NET 10 é LTS e é o único SDK instalado na máquina de build.

| Projeto | Depende de | Responsabilidade |
|---|---|---|
| `WebRadio.Domain` | nada | Entidades, enums, invariantes, transições de estado, erros de domínio |
| `WebRadio.Application` | Domain | Commands/Queries (MediatR), validadores (FluentValidation), comportamentos de pipeline, **interfaces** (`IRadioDbContext`, `IObjectStorage`, `IRequestThrottle`, `IClock`, `IListenerIdentity`, `IRealtimeNotifier`) |
| `WebRadio.Infrastructure` | Application | EF Core/SQL Server, MinIO (SDK S3), Redis, JWT, hash de senha, outbox |
| `WebRadio.Api` | Application, Infrastructure | Endpoints REST (Minimal APIs agrupadas por feature), hub SignalR, despachante do outbox, autenticação, limite de requisições |
| `WebRadio.Worker` | Application, Infrastructure | Serviço separado: transições da grade, expiração de pedidos, pipeline de sanitização, limpeza |

A regra de dependência é garantida por **teste de arquitetura** (NetArchTest), não só por convenção.

### 2.2 Decisões de arquitetura (ADRs resumidos)

| # | Decisão | Motivo | Alternativa descartada |
|---|---|---|---|
| D1 | **Liquidsoap** é o motor de áudio; o Icecast só distribui o stream (`/radio.mp3`, MP3 128 kbps) | O Icecast não toca arquivos; o Liquidsoap faz fallback, rotação, vinhetas e entrada ao vivo | API tocar áudio (acopla o stream ao ciclo de vida da API) |
| D2 | O Liquidsoap **puxa** a próxima faixa da API (`GET /internal/playout/proxima`) e **avisa** o início (`POST /internal/playout/eventos`) | A API continua como fonte da verdade da fila; o Liquidsoap não acessa o banco | API empurrar pelo telnet do Liquidsoap (frágil e com estado) |
| D3 | **Worker em processo separado** da API | Escalar a API não duplica tarefas agendadas | `BackgroundService` dentro da API |
| D4 | Idempotência do Worker por **UPDATE condicional + outbox na mesma transação**; `sp_getapplock` de sessão só elege um líder | A correção não depende do lock: mesmo com dois workers, só um recebe as linhas no `OUTPUT` | Só o lock distribuído (quebra se a conexão cair) |
| D5 | **Outbox transacional**: o Worker grava o evento no SQL e a API o entrega via SignalR | O Worker não tem clientes SignalR; o evento não se perde se o processo cair entre o commit e o envio | Worker com hub próprio |
| D6 | **Redis adicionado ao compose** (não estava no plano base) | O anti-spam precisa de uma operação atômica (`SET NX EX 300`); também serve de backplane do SignalR com mais de uma API | Cache em memória (quebra com mais de uma instância); tabela SQL (corrida no check-then-set) |
| D7 | Sem sobreposição na grade: **`sp_getapplock` exclusivo na transação** + consulta de interseção | O SQL Server não tem exclusion constraint; com RCSI ligado, um trigger sozinho deixa passar duas inserções concorrentes | Trigger; SERIALIZABLE (bloqueio de faixa largo demais) |
| D8 | Tudo em **UTC** (`datetime2`, sufixo `Utc`, ISO 8601 com `Z`); o fuso `America/Sao_Paulo` só na exibição | Horário de verão/fuso não corrompem a grade | `datetimeoffset` (não precisamos guardar o offset de origem) |
| D9 | **nginx do `web` é o gateway**: SPA, `/api`, `/hubs` e `/stream` na mesma origem | Sem CORS; o cookie de refresh fica `SameSite=Strict`; um único ponto de TLS | Expor API e Icecast em portas separadas |
| D10 | Upload direto ao MinIO por **presigned POST policy** (com `content-length-range` e `Content-Type`) no bucket `quarentena` | O arquivo não passa pela API e o tamanho é limitado pelo storage; o PUT pré-assinado não limita tamanho | Upload via API em chunks |
| D11 | O banco guarda **chave de objeto**, nunca URL | URLs expiram e mudam de host (interno × público) | Guardar `UrlStorage` |
| D12 | Erros em **ProblemDetails (RFC 9457)**; listas paginadas em `{ itens, total, pagina, tamanho }`; recursos retornados sem envelope | É o padrão do ASP.NET Core e o frontend recebe um contrato uniforme | Envelope `{success,data,error}` em tudo |
| D13 | Frontend: **React + Vite + TypeScript + Tailwind**, player `<audio>` único montado **fora** do `<Outlet>` do router | SPA garante que o player não reinicie ao trocar de aba | SSR/Next (o player reinicia em navegação de servidor) |
| D15 | **.NET 10 (LTS)** em todos os projetos, `global.json` fixando o SDK 10.0.x (`rollForward: latestFeature`) | O .NET 9 (STS) perde suporte em nov/2026; a máquina de build só tem o SDK 10 | Instalar o SDK 9 (entregaria uma plataforma sem suporte daqui a 6 semanas) |
| D16 | **Serviço `sanitizer` isolado** (ffmpeg/ffprobe mínimo, `network_mode: none`, sem segredos, `read_only`, `cap_drop: ALL`). O Worker troca arquivos com ele por um volume `tmpfs` compartilhado (protocolo de job: `jobs/{id}/in.bin` + `job.json` → `out.mp3` + `result.json`). Os magic bytes decidem o demuxer (`-f`) e há `-protocol_whitelist file` | Upload não confiável dentro do ffmpeg é SSRF, leitura de `/proc/self/environ` e RCE em demuxer (S-C01) | ffprobe dentro do Worker |
| D17 | **Porta interna da API `:8081`** para `/api/v1/internal/*` e `/health/*` (filtro de endpoint que exige `HttpContext.Connection.LocalPort == 8081` (a porta real do socket; **nunca** `RequireHost`, que compara o cabeçalho `Host`, controlado pelo cliente), com o listener `:8081` do Kestrel ligado só ao IP da rede `playout` e a `127.0.0.1` (o healthcheck roda dentro do contêiner; S-A06)); o nginx só encaminha `:8080` | O bloqueio por `location` no nginx é contornável com maiúsculas (S-A06, S-B05) | Só o `location` de bloqueio no nginx |
| D18 | **Storage na mesma origem**: `/storage/` no nginx → `minio:9000`, só com os métodos e caminhos permitidos; a porta 9000 nunca é publicada. Apps usam usuários de serviço (`svc-api`, `svc-worker`) com política mínima; o bucket `publico` só tem `GetObject` anônimo | Porta S3 e admin expostas, credencial root nas apps, bucket listável (S-A03, S-A04, S-A05) | Porta 9000 pública com CORS |
| D19 | **Redes segmentadas** `borda`, `app`, `dados`, `playout`; segredos **por serviço**; `.env` só em dev, **Docker secrets** (`/run/secrets`, `AddKeyPerFile`) em produção; `.env.example` só com `__GERAR__` + `gerar-segredos.sh/.ps1` + fail-fast no boot; Redis com senha | Segredo compartilhado e rede plana ampliam qualquer invasão (S-C02, S-A10) | `env_file` único para todos |
| D20 | **`db-init` separado do `migrator`**: só o `db-init` usa a SA | A SA nunca entra numa imagem de aplicação (S-A10) | Migrator com SA |
| D21 | **Login sem bloqueio por conta**: bloqueio de 15 min por (conta, IpHash) em Redis; por conta, só alerta e atraso progressivo (≥ 50 falhas/h); resposta **sempre 401 genérico** (o 423 sai do contrato); hash fictício para e-mail inexistente | Bloquear a conta deixa derrubar o Admin, e o 423 revela quais e-mails existem (S-A12) | 5 falhas → conta bloqueada |
| D22 | **Eventos de domínio**: a entidade acumula `IDomainEvent` (interface marcadora no Domain, sem MediatR) numa lista privada. Um `SaveChangesInterceptor` na Infrastructure despacha os eventos **antes do commit, na mesma transação**, para `IDomainEventHandler<T>` da Application. Efeito em banco (revogar refresh tokens) roda no handler; efeito externo (derrubar conexões do hub, SignalR) vira linha no **outbox** (D5), nunca chamada direta | Atomicidade: revogação e mudança de papel ficam na mesma transação; o efeito externo sobrevive a queda do processo | Publicar via MediatR depois do commit (perde o evento se cair) |
| D23 | **GitHub Actions é o ambiente de integração** (decisão do DJ, 2026-09-27): repositório **público** `arthur044/webradio` (o DJ escolheu público para ter proteção de branch e minutos de Actions sem custo; num repo privado do plano Free os checks não bloqueiam o merge). Consequências: todo o histórico do git é público para sempre, então nenhum segredo, `.env`, `.maestri/` ou dado pessoal pode entrar em commit (gitleaks no **histórico inteiro** antes do primeiro push); a `main` exige os checks obrigatórios (sem aprovação obrigatória: todos os agentes usam a mesma conta do GitHub, que não pode aprovar o próprio PR; a revisão do Sentinel e do Guardian é registrada como comentário no PR); os commits usam o e-mail noreply do GitHub, nunca o e-mail pessoal; workflows de PR de fork só rodam com aprovação manual; secret scanning + push protection ligados; tudo que precisa de contêiner (Testcontainers, compilação do DDL, `db-init`/`migrator`, `compose up` de teste de fumaça, Playwright contra o stack) roda em runner `ubuntu-latest`. **Atualização 2026-09-27:** o Docker local foi corrigido (Engine 29.8, Compose v5.5), e testes de integração, DDL e `compose up` também rodam localmente. A CI continua sendo o **gate** oficial do merge | Único ambiente com Docker disponível hoje, sem custo de infraestrutura | Outra VM, Docker remoto (ficam para depois) |
| D14 | **EF Core Migrations** são a fonte executável; `03-schema-sqlserver.sql` é o contrato do Atlas, que revisa o script gerado (`dotnet ef migrations script`) | Uma fonte executável só; o DBA mantém o controle do resultado | Duas fontes (DDL manual + migrations) divergindo |

## 3. Fluxos de negócio (versão final)

### Fluxo A — Grade e "Ao Vivo"
1. O Admin cria o Programa (`POST /programas`). Na transação: `sp_getapplock 'grade:programa' Exclusive` → consulta de interseção (`InicioUtc < @Fim AND FimUtc > @Inicio AND Status IN (Agendado, AoVivo)`) → se houver conflito, **409** com o id do programa conflitante; senão, INSERT.
   - Como a duração máxima é 12 h, a consulta ganha o limite `InicioUtc > DATEADD(HOUR, -12, @Inicio)` e vira um *seek* de faixa no índice `IX_Programa_Grade`.
2. O Worker roda a cada 10 s (`PeriodicTimer`), só no líder:
   ```sql
   BEGIN TRAN;
   DECLARE @agora datetime2(0) = SYSUTCDATETIME();
   UPDATE grade.Programa SET Status = 2, AtualizadoEmUtc = @agora
   OUTPUT inserted.Id, inserted.Titulo, inserted.LocutorId INTO @iniciados
   WHERE Status = 1 AND InicioUtc <= @agora AND FimUtc > @agora;
   UPDATE grade.Programa SET Status = 3, AtualizadoEmUtc = @agora
   OUTPUT inserted.Id INTO @finalizados
   WHERE Status IN (1, 2) AND FimUtc <= @agora;
   INSERT infra.EventoOutbox (Tipo, Payload) SELECT 'ProgramaStatusAlterado', ... FROM @iniciados UNION ALL ... @finalizados;
   COMMIT;
   ```
   Rodar de novo não produz nada: é idempotente por construção.
3. O `OutboxDispatcher` da API lê `UPDATE TOP(50) ... WITH (UPDLOCK, READPAST)` onde `ProcessadoEmUtc IS NULL` e publica `ProgramaStatusAlterado` para todos no hub. A entrega é **pelo menos uma vez**: o cliente ignora `eventoId` repetido.
4. O frontend troca o selo "Ao Vivo" ao receber o evento. Ao reconectar, chama `GET /programas/ao-vivo` para ressincronizar.

### Fluxo B — Pedido de música
1. O frontend gera `listenerId` (`crypto.randomUUID()`, localStorage) e o envia no header `X-Listener-Id` e na query do hub.
2. `POST /pedidos` → `CreateSongRequestCommand`. Ordem do pipeline MediatR: `Logging → Validation (FluentValidation, só campos) → Throttle (IThrottledRequest) → Handler`.
3. `IListenerIdentity` calcula `DeviceHash = HMAC-SHA256(pepper, listenerId)` e `IpHash = HMAC-SHA256(pepper, IP)`. O IP vem de `ForwardedHeaders` **só com o nginx em `KnownProxies`**; IPv6 é normalizado para /64. O banco nunca guarda IP nem listenerId em claro (LGPD).
4. Regras do throttle (Redis):
   - dispositivo: `SET throttle:pedido:dev:{hash} 1 NX EX 300` → falhou = 429;
   - IP: `INCR throttle:pedido:ip:{hash}` + `EXPIRE NX 300` → acima de 3 = 429 (tolera NAT/Wi-Fi compartilhado);
   - usuário logado: `SET throttle:pedido:usr:{id} NX EX 300`.
   - O 429 volta como ProblemDetails com `retryAfterSeconds` (TTL restante) e o header `Retry-After`.
   - **Redis fora do ar**: cai para uma consulta no SQL (`EXISTS` nos últimos 5 min pelos índices de hash), registra um warning e o health fica `Degraded`. Não é atômico, mas é aceitável em modo degradado.
5. O handler grava `Pendente` (e `ProgramaId` do programa AoVivo, se houver) e escreve no outbox `PedidoCriado`, que vai para o grupo SignalR `locutores`.
6. O locutor aprova (`midiaId` opcional, que precisa ser uma mídia `Aprovado` do tipo Musica) ou rejeita. O outbox publica `PedidoModerado` para o grupo `listener:{deviceHash}`.
7. Pedido aprovado com `midiaId` entra na fila de playout (Fluxo D). Sem mídia, o locutor toca manualmente e marca `Tocado`.
8. O Worker expira pedidos `Pendente` há mais de `Pedidos:ExpiracaoMinutos` (padrão 120) → `Expirado`.

### Fluxo C — Upload e sanitização de mídia
1. `POST /midias/uploads` (Locutor/Admin; 20/h por usuário e no máximo 5 abertos por usuário, S-M06) → cria `ArquivoMidia` em `AguardandoUpload` e devolve a presigned POST policy, com as condições **exatas** do S-M01: `bucket` eq `quarentena`, `key` eq `{yyyy}/{MM}/{id}`, `content-length-range [1, limiteDoTipo]`, `Content-Type` eq e validade de 15 min. A URL é `<origem>/storage/quarentena` (D18).
2. O browser envia o arquivo e depois chama `POST /midias/{id}/concluir`. A API faz o `HEAD` do objeto (existe e o tamanho bate), grava `EtagUpload` → `Pendente`.
3. O Worker pega o arquivo (`UPDATE TOP(1) ... WITH (READPAST)` → `EmAnalise`, `EmAnaliseDesdeUtc`, `TentativasSanitizacao + 1`; um lease vencido volta para a fila; na 3ª falha → `Rejeitado`):
   1. `stat` do objeto: tamanho acima do limite ou `ETag` ≠ `EtagUpload` → `Rejeitado`. Baixa **uma única vez** para o `tmpfs`; daqui em diante tudo roda sobre essa cópia (S-M02, S-A01);
   2. `HashOriginalSHA256` do arquivo recebido (auditoria, reenvio);
   3. **magic bytes** decidem o formato (`ID3`/sync → mp3, `OggS` → ogg, `RIFF…WAVE` → wav, `fLaC` → flac). Qualquer outro → `Rejeitado`, **sem** ffmpeg;
   4. o Worker entrega o job ao **`sanitizer`** (D16): `ffprobe -protocol_whitelist file -f <fmt>` → whitelist de codec e exatamente 1 stream de áudio → `ffmpeg … -map 0:a:0 -map_metadata -1 -fflags +bitexact … -t <max+1> -fs <max>` → MP3 CBR 192k 44.1 kHz, com timeout duro e kill (comando exato no S-C01);
   5. `DuracaoSegundos` e `MimeType` vêm do **arquivo de saída** (o cabeçalho de entrada não é confiável, S-A01); limites por tipo aplicados sobre ela;
   6. `HashSHA256` da **saída** → duplicata de uma mídia aprovada = `Rejeitado` ("duplicado de {id}");
   7. antivírus (ClamAV no Épico 4). No Épico 1 existe o `NoOpMalwareScanner` só com `IsDevelopment()` **e** `Seguranca:PermitirScannerNoOp=true`. Fora disso, sem scanner o Worker **não sobe**. Falha ou timeout do scan = continua `Pendente` (S-A02);
   8. grava **só a saída** em `midia/`, apaga o original da quarentena → `Aprovado`. `Rejeitado` apaga o objeto na hora; `Quarentena` guarda por 30 dias, isolada, e avisa o Admin (S-B06).
4. O Worker apaga registros em `AguardandoUpload` com mais de 1 h e seus objetos.
5. As imagens de divulgação **não** usam POST policy: passam pela API (multipart ≤ 2 MB), que recodifica, tira o EXIF, limita a 4096 px e grava em `publico/` com `Content-Type` definido pelo servidor (S-A05).

### Fluxo D — Playout (contrato definido agora, implementação no Épico 2)
- Script do Liquidsoap: `fallback(track_sensitive=false, [ao_vivo_harbor, fila_api, rotacao, silencio_emergencia])`, com `rotate(weights=[1,4], [vinhetas, musicas])` na rotação.
- O Liquidsoap fala **só** com `http://api:8081` (porta interna, D17) pela rede `playout`. Ele valida o prefixo da `url` recebida (`http://minio:9000/midia/`) e aplica os metadados com `metadata.map`, nunca concatenando dentro de `annotate:` (S-A09).
- `fila_api` = `request.dynamic` → `GET /internal/playout/proxima` → a API faz `UPDATE TOP(1) ... WITH (UPDLOCK, READPAST) SET EnviadoPlayoutEmUtc` no pedido aprovado mais antigo que tenha mídia e devolve uma URL pré-assinada **interna** (`minio:9000`). Sem pedido na fila, responde 204 e a rotação assume.
- `on_track` → `POST /internal/playout/eventos` → grava `midia.Reproducao`, marca o pedido como `Tocado` e publica `TocandoAgora`.
- Ao vivo: o locutor conecta um source client (BUTT, Mixxx) no harbor `:8005/live`, que tem prioridade no fallback.
- Os endpoints `/internal/*` exigem `Authorization: Bearer {Playout__Token}`, e o nginx **bloqueia** `/api/internal` do lado público.

## 4. Segurança (base; o agente DevSecOps aprofunda)

> A fonte detalhada é `05-seguranca-auditoria.md` (normativo). Aqui fica só o resumo.

- Senhas com `PasswordHasher<Usuario>` (PBKDF2, ASP.NET Core Identity), de 12 a 128 caracteres. Bloqueio por (conta, IP) conforme o D21; troca de senha obrigatória no primeiro login do Admin semeado (`DeveTrocarSenha`).
- Access token JWT HS256 de 15 min, chave ≥ 32 bytes decodificados, com `kid` e aceitação da chave anterior durante a rotação. Refresh token de 7 dias, **rotativo com UPDATE atômico** e janela de tolerância de 10 s (S-M10); reutilizar um token revogado revoga a família. Desativar, rebaixar ou trocar a senha revoga todas as famílias e derruba as conexões do hub (S-M08, S-M11).
- Refresh só no cookie `__Secure-wr_refresh` (`HttpOnly; Secure; SameSite=Strict; Path=/api/v1/auth`), conferindo também `Origin`/`Sec-Fetch-Site`. O access token fica **só em memória** no frontend.
- `FallbackPolicy` = usuário autenticado: um endpoint anônimo precisa de `AllowAnonymous` explícito.
- Borda nginx: `limit_req`/`limit_conn`, timeouts contra slowloris, CSP e headers de segurança em **todo** `location` (S-M13, S-M14), `X-Forwarded-For` sobrescrito, e `KnownNetworks` com a subnet fixa da rede `app` (S-A11). `/stream/` só aceita GET em `radio.mp3` (S-A07).
- Harbor `:8005`: senha ≥ 24 caracteres, publicado só com lista de IPs permitidos ou VPN; em dev, `127.0.0.1`. Autenticação por locutor no Épico 2 (S-A08).
- Limite de requisições (ASP.NET RateLimiter): global de 100/min por IP; `/auth/login` com 5/min por IP; `/pedidos` tem o throttle de domínio além disso.
- Contêineres sem root, segredos em `.env` (nunca versionado; o repositório tem `.env.example`), e SQL, MinIO e Redis sem porta pública fora do perfil dev.

## 5. Riscos

| Nível | Risco | Mitigação |
|---|---|---|
| ALTO | URL pré-assinada assinada para o host errado (`minio:9000` × `<origem>/storage`) quebra upload ou playout | Dois clientes S3: interno (Liquidsoap/Worker) e público (`Storage__PublicEndpoint=<origem>/storage`, D18). Teste de integração cobre os dois pelo proxy real |
| ALTO | Troca de programa no Worker com relógio divergente | Só o relógio do SQL (`SYSUTCDATETIME()`) vale como `@agora` na transação; o `IClock` da aplicação não entra na transição |
| MÉDIO | O Liquidsoap fica sem faixa se a API cair | `rotacao` lê uma playlist local montada em volume; `silencio_emergencia` é o último recurso. O stream nunca cai |
| MÉDIO | Índice filtrado ignorado com predicado parametrizado | Os predicados de status nas consultas da fila usam **constante** no LINQ (o EF inlina), e o Atlas confere o plano de execução |
| MÉDIO | NAT faz ouvintes legítimos do mesmo IP levarem 429 | O limite por IP é 3/5 min (o de dispositivo é 1). Monitorar a taxa de 429 |
| BAIXO | Crescimento de `midia.Reproducao` | Índice por data; particionamento mensal só se passar de ~10 milhões de linhas |

## 6. Fora do escopo da v1
Grade recorrente (modelo de repetição semanal que gera as instâncias), busca full-text, transcodificação multi-bitrate (HLS), app mobile, métricas de audiência do Icecast.

## 7. Decisões sobre a auditoria do Sentinel (itens da §7 do `05`)

| Item | Decisão | Onde entra |
|---|---|---|
| Serviço `sanitizer` isolado | **Aceito** (D16). Épico 1: o serviço entra no compose com `profiles: [sanitizacao]` e a interface `IAudioSanitizer` na Application; a imagem com o ffmpeg mínimo vem no Épico 4 | E1-F09, E1-F16 |
| Porta interna `:8081` | **Aceito** (D17), com `/health/*` também nela | E1-F06, F08, F12, F15, F16 |
| Storage em `/storage/` | **Aceito** (D18) | E1-F09, F16, P06 |
| `POST /divulgacoes/imagens` multipart | **Aceito** | `02-api-rest.md §7`, Épico 3 |
| `POST /internal/harbor/auth` | **Aceito para o Épico 2** (rota reservada na `:8081`) | `02-api-rest.md §9` |
| Colunas novas em `ArquivoMidia` | **Aceito**: `HashOriginalSHA256`, `EtagUpload`, `TentativasSanitizacao`, `EmAnaliseDesdeUtc` | `01`, `03` (Atlas aplica na A01) |
| Conta bloqueada | **Sempre 401 genérico**. Recusei a variante "423 depois de senha correta", porque ela confirma ao atacante que a senha está certa. O 423 sai do contrato (D21) | `01 §3`, `02 §2`, E1-F07 |
| `db-init` separado | **Aceito** (D20) | E1-F05, A02, A04, F16 |
| Redes `borda/app/dados/playout` | **Aceito** (D19) | E1-F16 |
| `Usuario.DeveTrocarSenha` | **Aceito**: coluna + `POST /auth/trocar-senha`; o login devolve `deveTrocarSenha` | `01`, `02`, `03`, E1-F07, P05 |
| Renomear `docker-compose.override.yml` | **Aceito** (S-M12): vira `docker-compose.dev.yml`, usado explicitamente com `-f` | E1-F16 |
| Cookie `__Secure-wr_refresh` | **Aceito** (S-B01) | `02 §2`, E1-F07, P04 |

Os MÉDIOS e BAIXOS sem impacto de contrato valem como requisito da tarefa citada no `05`.
