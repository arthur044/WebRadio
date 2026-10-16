# Épico 2 — Motor de transmissão, grade e ao vivo: pacote de tarefas

> Autor: Nexus · 2026-10-15 · Referências: `00-arquitetura.md` (Fluxos A e D), `01-dominio.md` (§2.3 `Programa`), `02-api-rest.md` (§4, §9, §10), `03-schema-sqlserver.sql`, `05-seguranca-auditoria.md` (S-A08, S-A09).
> Donos: **Forge** (backend, Worker, Liquidsoap, contêineres), **Atlas** (banco), **Prism** (frontend), **Sentinel** (revisão de segurança do ao vivo).
> Escopo decidido pelo dono do produto em 2026-10-15: **inclui o locutor ao vivo**, com credencial de transmissão própria (identificador opaco + segredo aleatório guardado só como hash com salt).
> O `05` continua **normativo**: cada achado que cita uma tarefa `E2-*` vira critério de aceite dela. A checklist da §8 do `05` é o quality gate de todo PR.
> **v1.3 (2026-10-15):** os dois riscos aceitos (falha aberta do ao vivo e harbor sem TLS) foram confirmados pelo dono do produto e registrados no `05` §4.
> **v1.2 (2026-10-15):** re-review do PR #38: encerrar também um `Agendado` com a janela em curso (Worker parado); prazo de queda corrigido para 15 s; throttle atrás do proxy só no proxy; senha errada conta no limite de geração; seed antigo removido.
> **v1.1 (2026-10-15):** incorpora a revisão do PR #38 (Guardian, Atlas e Sentinel): autorização do harbor pela **janela de horário** calculada no SQL, e não pelo `Status` que o Worker muda; falha aberta com teto; throttle por par (identificador, IP); validação estrita do que vem do source client; proxy na frente do harbor em produção; evento de grade também no encerrar e no cancelar; seed de dev pelo `DevDataSeeder`.

## O que o Épico 2 entrega

1. **Grade de programas.** O Admin cria, edita e cancela programas sem sobreposição. O Worker muda o status sozinho (`Agendado → AoVivo → Finalizado`) e o selo "Ao Vivo" troca na tela sem refresh.
2. **Locutor ao vivo.** Só o locutor dono do programa que está na janela de horário consegue conectar o source client (BUTT, Mixxx) no harbor, e a conexão cai quando o programa termina ou é encerrado.
3. **Estado da rádio em tempo real.** "Tocando agora" e "transmissão ao vivo no ar" chegam a todos pelo SignalR.
4. **Telas.** Admin gerencia a grade; locutor tem um painel com a credencial de transmissão, o estado "no ar" e o botão de encerrar; o público vê a grade da semana, o selo Ao Vivo e o que está tocando.

## Fora do Épico 2

- **Fila de pedidos** (`fila_api` com pedidos aprovados): Épico 3. O `GET /internal/playout/proxima` continua respondendo **204**.
- **Biblioteca de mídia, upload e sanitização**: Épico 4. A rotação continua com a **playlist local** montada em volume, e **nada é gravado em `midia.Reproducao`** (a tabela exige `MidiaId`, e a playlist local não tem). O "tocando agora" segue pelos metadados, como no Épico 1.
- **Grade recorrente**: fora da v1 (`00 §6`).
- **TLS no harbor**: fora, como risco aceito com gatilhos (ver "Riscos aceitos"; aceito pelo dono do produto em 2026-10-15).

## Definição de pronto do Épico

1. No `compose-smoke`: um programa criado pela API para "agora" vira `AoVivo` em até 20 s; um teste Playwright vê o selo Ao Vivo aparecer **sem recarregar a página**. Encerrar e cancelar também atualizam a tela sem refresh.
2. Criar um programa que se sobrepõe a outro responde **409** com `programaConflitanteId`; dois `POST` concorrentes para o mesmo horário resultam em **um** 201 e **um** 409 (teste com SQL real).
3. Com a credencial do locutor dono do programa da janela atual, um source client de teste conecta no harbor e o stream passa a tocar o ao vivo. Com credencial errada, revogada, de outro locutor ou fora da janela, a conexão é **recusada** (teste no `teste.sh` do Liquidsoap e no smoke).
4. Ao encerrar o programa (`POST /programas/{id}/encerrar`) ou quando `FimUtc` passa, o ao vivo é **derrubado em até 30 s** e o stream volta para a rotação **sem cair** (o `curl` do stream não perde a conexão). Isso vale **com o Worker parado**: a autorização não depende dele.
5. O segredo da credencial de transmissão **nunca** é gravado em claro, nunca aparece em log e só é mostrado uma vez, na geração.
6. Os eventos `ProgramaStatusAlterado` e `TransmissaoAoVivoAlterada` chegam ao frontend pelo hub, e o cliente se ressincroniza por REST ao reconectar.
7. A base de produção (`docker-compose.yml`) continua publicando **só** a 8080; a porta do ao vivo existe só num arquivo de compose explícito (E2-F11).
8. Tudo que já era exigido no Épico 1 continua verde (CI completo, cobertura ≥ 80% em Domain e Application).

## Contratos novos e alterados

Esta seção é o contrato. O `02-api-rest.md` é atualizado no mesmo PR desta spec. O DDL entra no `03` **no mesmo commit da migration**, no PR B2 (o job `ddl-check` compara o `03` com as migrations e falharia antes disso); nesse mesmo PR, o bloco de DDL que ficava aqui virou um ponteiro para o `03` §3.1, para não haver duas fontes.

### Credencial de transmissão (S-A08, decisão de 2026-10-15)

- **Identificador** (o "usuário" que o locutor digita no source client): `Guid.NewGuid()` (UUID v4, aleatório), **opaco**. Nunca `Guid.CreateVersion7()` nem `NEWSEQUENTIALID()`, que carregam o relógio ou são sequenciais, e nunca o `Id` da linha, o `Id` do usuário ou o e-mail. Quem interceptar a conexão do harbor não descobre de quem é a conta.
- **Segredo** (a "senha" do source client): 32 bytes do `RandomNumberGenerator`, em base64url sem padding: exatamente **43 caracteres** de `[A-Za-z0-9_-]`. Um GUID **não** serve como segredo: tem só 122 bits aleatórios.
- **Hash**: `SHA-256(salt ‖ bytes ASCII dos 43 caracteres)`, com salt de 16 bytes aleatório por credencial. A **mesma** função na geração e na verificação: o que entra no hash é o texto que o locutor digita, não os 32 bytes de origem. Como o segredo tem 256 bits de entropia, um hash rápido basta e mantém a autenticação em milissegundos. Comparação com `CryptographicOperations.FixedTimeEquals`.
- **Validação antes de qualquer uso**: `usuario` precisa casar `^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$` e `senha` precisa casar `^[A-Za-z0-9_-]{43}$`. Fora disso, **401** sem tocar no banco, no throttle nem no log com o valor. Vale no `radio.liq` (antes de chamar a API) **e** na API.
- **Tempo constante**: identificador válido que não existe também calcula um hash contra salt e hash fictícios, para o tempo de resposta não revelar se o identificador existe.
- **Geração**: exige `senhaAtual` no corpo (um access token roubado não vira credencial persistente), não é aceita com o token restrito de troca de senha pendente (não leva `PermitirComTrocaDeSenhaPendente`), limite de **5 tentativas de geração por hora** por usuário, contando também as com `senhaAtual` errada (senão o endpoint vira oráculo de senha para quem tem um token roubado), e log de auditoria de geração e revogação (quem, quando, identificador; nunca o segredo).
- **Ciclo de vida**: uma credencial ativa por locutor. Gerar uma nova **revoga** a anterior na **mesma transação** (`UPDATE` da ativa, depois `INSERT`). Violação do índice de "uma ativa" em gerações concorrentes vira **409**, nunca 500. O segredo aparece **uma única vez**, na resposta da geração.
- **Revogação automática**: desativar a conta, trocar a senha da conta ou mudar o papel para `Ouvinte` revoga a credencial. Troca entre `Admin` e `Locutor` **não** revoga (os dois podem transmitir). O gatilho é o evento `UsuarioCredenciaisAlteradas`, filtrado pelo motivo no handler.
- **Não é o login do site.** A senha da conta nunca trafega pelo harbor. Vazar a credencial de transmissão permite, no máximo, transmitir na janela do programa daquele locutor, e o locutor ou o Admin a revoga na hora (o ao vivo cai em até 15 s: consulta a cada 10 s mais o timeout de 5 s).

DDL: `docs/specs/03-schema-sqlserver.sql` §3.1 (`seg.CredencialTransmissao`), aplicado pela migration `CredencialTransmissao` no mesmo commit (aprovado pelo Atlas no PR #38, E2-A01). O `03` é a única fonte: o bloco que estava aqui saiu no B2. Sem `CHECK (RevogadaEmUtc >= CriadaEmUtc)`: os dois relógios são diferentes (SQL e `IClock`) e a revogação falharia com erro 547.

### Autorização do ao vivo: pela janela, não pelo `Status`

A condição de `POST /internal/harbor/auth` e de `GET /internal/harbor/autorizacao/{identificador}` é calculada **no SQL**, com `SYSUTCDATETIME()`:

- credencial ativa, cujo dono está ativo e tem papel `Locutor` ou `Admin`;
- existe um programa desse locutor com `InicioUtc <= SYSUTCDATETIME() < FimUtc` **e** `Status NOT IN (Finalizado, Cancelado)`.

O `Status = AoVivo` **não** entra na conta. Assim, o locutor que conecta no segundo exato do início não espera o ciclo do Worker, e, com o Worker parado, o ao vivo cai mesmo assim quando `FimUtc` passa. O `encerrar` grava `FimUtc = agora`, então derruba na consulta seguinte.

### REST pública (acrescenta ao `02 §4`)

| Método | Rota | Quem | Descrição |
|---|---|---|---|
| GET | `/locutores/me/credencial-transmissao` | Locutor, Admin | **200** `{ identificador, criadaEmUtc, ultimoUsoEmUtc }` ou **204** se não houver ativa. Nunca devolve o segredo |
| POST | `/locutores/me/credencial-transmissao` | Locutor, Admin | Corpo `{ senhaAtual }`. Gera uma nova e revoga a anterior → **201** `{ identificador, segredo, criadaEmUtc }`. **Única** resposta que contém o segredo; `Cache-Control: no-store`. Senha errada → 401; mais de 5/h → 429; concorrência → 409 |
| DELETE | `/locutores/me/credencial-transmissao` | Locutor, Admin | Revoga a ativa → **204** |
| GET | `/credenciais-transmissao` | Admin | Lista as ativas: `[{ usuarioId, usuarioNome, identificador, criadaEmUtc, ultimoUsoEmUtc }]` |
| DELETE | `/usuarios/{id}/credencial-transmissao` | Admin | Revoga a de outro usuário → **204** |

Os endpoints de `/programas` já estão no `02 §4` e não mudam de rota. Muda o efeito: `encerrar` e `cancelar` gravam `ProgramaStatusAlterado` no outbox na mesma transação (E2-F01).

### Endpoints internos (substitui a linha reservada do `02 §9`)

Todos na `:8081`, com o Bearer `Playout__Token` e o filtro de porta do S-A06, como os do Épico 1.

| Método | Rota | Descrição |
|---|---|---|
| POST | `/internal/harbor/auth` | `{ usuario, senha, enderecoIp }` → **200** `{ programaId, fimUtc }` se a condição da janela vale; senão **401** sem corpo e sem dizer qual condição falhou. Throttle conforme abaixo. Grava `UltimoUsoEmUtc` no sucesso |
| GET | `/internal/harbor/autorizacao/{identificador}` | **200** `{ programaId, fimUtc }` enquanto a condição da janela valer; **403** quando deixar de valer. O `{identificador}` só é montado na URL depois de passar na regex de GUID |
| POST | `/internal/harbor/eventos` | `{ tipo: "Conectado" \| "Desconectado", identificador, ocorridoEmUtc }` → **202**. Publica `TransmissaoAoVivoAlterada` |

**Throttle do `harbor/auth`** (no desenho do D21 do login):
- Só contam as falhas de **credencial** (identificador desconhecido, segredo errado). "Credencial certa, fora da janela" responde 401 igual, mas **não** conta: o BUTT reconecta sozinho a cada poucos segundos, e o locutor que conecta minutos antes do programa não pode ficar bloqueado na hora de entrar.
- Bloqueio por **par (identificador, IP)**: 10 falhas em 5 min. Por **IP**: um limite mais alto, contra varredura de identificadores. Por **identificador sozinho**: só log e alerta, **nunca** bloqueio (quem conhece o identificador, que trafega em claro, não pode travar o locutor).
- Sucesso zera o par. As chaves do throttle são `ChaveOpaca` (HMAC) e só valores que passaram na validação entram nelas.
- **Atrás do proxy da E2-F11, o Liquidsoap só vê o IP do proxy** (o harbor não lê PROXY protocol). Nesse caso o par (identificador, IP) degeneraria em "por identificador", que é justamente o bloqueio proibido acima. Por isso, atrás do proxy, a API **não bloqueia** por IP nem por par: registra e alerta. O limite por cliente fica no proxy (`limit_conn` por IP real, lista de IPs/VPN), e o IP real do cliente existe **só** no log do `harbor-borda`. Contra força bruta, o que protege é o segredo de 256 bits.

**Logs** (API e `radio.liq`): identificador **só** se passou na regex; senão `usuario_invalido` e o tamanho (o locutor pode trocar os campos e mandar o segredo no lugar do usuário). IP **anonimizado** (/24 para IPv4, /64 para IPv6), como o nginx; nunca o segredo.

### SignalR (acrescenta ao `02 §10`)

| Evento | Destino | Payload |
|---|---|---|
| `TransmissaoAoVivoAlterada` | todos | `{ noAr: bool, programaId?, locutorNome? }` |

Estado: o cliente guarda o último `eventoId` aplicado e, ao reconectar, ressincroniza por `GET /stream/info`, que ganha o campo `aoVivo: { noAr, programaId?, locutorNome? }`.

### Metadados vindos do source client (S-A09)

Título e artista que o locutor manda pelo source client são validados **no `radio.liq` e no servidor** (`POST /internal/harbor/eventos` e `TocandoAgora`): até 200 caracteres, rejeitando as categorias Unicode `Cc`, `Cf` (inclui os caracteres de controle de direção, como `U+202E`, e os invisíveis, como `U+200B`) e `Cs`. Aplicados por `metadata.map`/`insert_metadata`, nunca por `annotate:`.

## Tarefas — Forge (Backend, Worker, Liquidsoap e contêineres)

| ID | Tarefa | Depende | Critério de aceite |
|---|---|---|---|
| **E2-F01** | **Comandos e consultas de `Programa`**: criar, editar (só `Agendado`, com `If-Match`), cancelar, encerrar (locutor dono ou Admin; vale para `AoVivo` **e** para `Agendado` cuja janela já começou, para funcionar com o Worker parado; grava `Status = Finalizado` e `FimUtc = mín(FimUtc, máx(agora truncado ao segundo, InicioUtc + 1 s))`: o `InicioUtc + 1 s` evita o erro do `CK_Programa_Intervalo` no primeiro segundo, e o `mín` impede que um `Agendado` já vencido tenha a janela estendida; refinamento do Forge no PR #40, aceito pelo Guardian), listar por intervalo (máx. 31 dias), `ao-vivo` e por id com `ETag`. Criar e editar passam pelo `sp_getapplock 'grade:programa' Exclusive` + consulta de interseção do Fluxo A, com o limite de 12 h e, na edição, **excluindo o próprio `Id`**. Validações do `01 §2.3`. **Encerrar e cancelar gravam `ProgramaStatusAlterado` no outbox na mesma transação** (o Worker só vê `Agendado` e `AoVivo`) | — | TDD no Domain e na Application; **dois POST concorrentes no mesmo horário → um 201 e um 409** com SQL real (`[SqlFact]`); intervalos encostados (`[a, b)` e `[b, c)`) **não** conflitam; 409 traz `programaConflitanteId`; encerrar e cancelar geram exatamente um evento no outbox, e o Worker não gera um segundo depois; encerrar um `Agendado` com a janela em curso funciona com o Worker parado |
| **E2-F02** | **Endpoints `/programas`** conforme o `02 §4` e a matriz RBAC do `02 §1`; `ConflitoDeGradeException` → 409 `/erros/conflito-grade` pelo `TratadorDeExcecoes` | F01 | Testes de integração para cada rota e papel (ouvinte anônimo só lê; Locutor só encerra o próprio; Admin tudo); `PUT` sem `If-Match` → 428, com `ETag` velho → 412 |
| **E2-F03** | **Transições de grade no Worker** (só o líder, ciclo de 10 s): o SQL do Fluxo A, passo 2, com `@agora = SYSUTCDATETIME()` (nunca o `IClock`), e o `ProgramaStatusAlterado` no outbox com o envelope `{ grupos: null, dados }`. O payload segue o `02 §10` (`{ programaId, titulo, status, locutorNome }`): o `OUTPUT` dos iniciados **e** dos finalizados traz `Titulo` e `LocutorId`, e o `locutorNome` vem de um `JOIN` com `seg.Usuario` | F01 | Idempotente: rodar duas vezes não gera evento repetido; programa criado para "agora" vira `AoVivo` no próximo ciclo; `FimUtc` passado vira `Finalizado`; o payload dos dois casos tem os quatro campos; teste com SQL real |
| **E2-F04** | **Credencial de transmissão**: entidade, migration (DDL do contrato; `03` §3.1 no mesmo commit; ponteiro no lugar do bloco deste arquivo), endpoints da credencial, geração e hash conforme o contrato, revogação automática pelo `UsuarioCredenciaisAlteradas` filtrado por motivo | A01 | O segredo não aparece em nenhum log nem em nenhuma resposta além do `POST` (teste que varre os logs da requisição); `[SqlFact]` com dois `POST` concorrentes termina com **exatamente uma** ativa e nenhum 500; `POST` sem `senhaAtual` correta → 401; sexta tentativa na hora (inclusive com senha errada) → 429; token de troca de senha pendente → 403; `Cache-Control: no-store`; troca Admin ↔ Locutor não revoga, ida para `Ouvinte` revoga |
| **E2-F05** | **`POST /internal/harbor/auth`** e **`GET /internal/harbor/autorizacao/{identificador}`** conforme o contrato (janela no SQL, validação estrita, tempo constante, throttle, logs) | F01, F04 | Cada motivo de recusa (inexistente, revogada, segredo errado, dono desativado, fora da janela, programa de outro locutor) → **401 idênticos**; `usuario` que não é GUID, `senha` fora do alfabeto ou do tamanho, `usuario` com `../`, aspas ou quebra de linha → 401 e **nenhuma** linha de log com o valor (teste que manda o segredo no campo usuário e varre os logs); **com o Worker parado, `/autorizacao` responde 403 já na primeira consulta depois de `FimUtc`**; falhas de "fora da janela" não consomem o throttle; o par (identificador, IP) bloqueia e o identificador sozinho não; só na `:8081` (o teste de bypass do S-A06 cobre as rotas novas) |
| **E2-F06** | **`POST /internal/harbor/eventos`** + evento `TransmissaoAoVivoAlterada` pelo outbox + campo `aoVivo` em `GET /stream/info`; metadados validados no servidor conforme o contrato | F05 | Conectar → `noAr: true` com `programaId`; desconectar → `noAr: false`; evento repetido não muda o estado; título com `U+202E` ou `U+200B` é recusado |
| **E2-F07** | ***Spike* (primeira tarefa do B2): derrubar o harbor pelo Liquidsoap.** Roda contra um **stub** HTTP mínimo no `teste.sh` (responde 200 com `{ programaId, fimUtc }`, 401 e 403 sob comando), sem depender da F05. Provar, com a imagem fixada do Liquidsoap: (a) o `auth` do `input.harbor` valida o formato e chama `POST /internal/harbor/auth`; exceção ou timeout (5 s) = recusa; (b) uma tarefa periódica (`thread.run`, 10 s) consulta `/autorizacao` e, no 403, **desconecta o source client** e volta para a rotação sem derrubar o `output.icecast`; (c) **falha aberta com teto**: só erro de **transporte** (timeout, conexão recusada, 5xx) mantém o locutor; **qualquer 4xx derruba na hora**; o teto é `min(fimUtc + 1 min, último 200 + 15 min)`; cada consulta que falha gera log `log.severe` (nível 2; o Liquidsoap 2.4 não tem nível Warning); (d) **conexão nova com a API fora é recusada**; (e) uma segunda conexão com credencial válida não derruba a que está no ar; (f) qual IP o `auth` recebe na topologia real (direto e atrás do proxy da F11); (g) se a imagem sustenta `input.harbor` com TLS e se BUTT e Mixxx conectam (só medir) | — | Relatório no PR com cada item e o que funcionou na versão fixada. **Se (b) ou (c) não forem possíveis**, o Forge para e avisa o Nexus antes de seguir: a alternativa é decisão de arquitetura |
| **E2-F08** | **`radio.liq` do ao vivo**: `input.harbor` com o `auth` da F07; metadados validados; `on_connect`/`on_disconnect` chamando `POST /internal/harbor/eventos`; logs conforme o contrato. Sai a senha única `liquidsoap_harbor_password` de **todos** os lugares: `radio.liq` (`segredo(...)`), `gerar-segredos.sh` e `.ps1`, `verificar-paridade-segredos.sh`, `.env.example`, compose, tabela de serviços do `04` e tabela 5.1 do `05` | F06, F07 | `teste.sh` cobre: credencial certa na janela → toca o ao vivo; errada → recusa; programa encerrado → derruba em ≤ 30 s e volta para a rotação; API parada → conexão nova recusada e a já conectada segue até o teto; o stream de saída não cai em nenhum dos casos; CI verde no meio do B2 (a paridade de segredos não quebra) |
| **E2-F09** | **Manutenção**: apagar credenciais revogadas há mais de 90 dias, no job diário do Worker, com `DELETE TOP (4000)` simples em laço até o lote vir incompleto (nenhuma tabela referencia a credencial, então não precisa do passo duplo do `RefreshToken`) | F04 | Teste com SQL real; seção 11 do `03` atualizada no mesmo commit |
| **E2-F10** | **Smoke e CI**: o `smoke-compose.sh` cobre os itens 1, 3, 4 e 7 da Definição de pronto (programa vira `AoVivo`; source client de teste com `ffmpeg` conecta e é derrubado no encerramento; o `harbor-borda` recusa IP fora da lista; a base de produção não publica a porta do ao vivo) | F08, F11 | Job `compose-smoke` verde com os itens novos |
| **E2-F11** | **Borda do harbor** (Sentinel, PR #38): serviço `harbor-borda` (nginx com módulo `stream`, sem root, imagem por digest, endurecido como os outros) nas redes `borda` e `playout`, repassando para o `liquidsoap:8005`. O Liquidsoap continua **só** na `playout` (`internal`). O proxy aplica `allow`/`deny` (lista de IPs ou VPN), `limit_conn` por IP do cliente e timeouts. O log dele é o único com o IP real do cliente: IP anonimizado (/24, /64) ou `access_log off`, e o serviço entra no `limpar-logs.sh`. A porta é publicada em `${HARBOR_BIND_IP:?}`, nunca `0.0.0.0`, num **`docker-compose.live.yml`** explícito; a base de produção continua sem ela. No dev, substitui a rede `diag-liquidsoap` | F07 | Smoke da base confere que nenhuma porta além da 8080 é publicada; com o `live.yml`, só a porta do harbor a mais e no IP configurado; IP fora da lista é recusado pelo proxy antes de chegar ao Liquidsoap |

## Tarefas — Atlas (Banco de dados)

| ID | Tarefa | Depende | Critério de aceite |
|---|---|---|---|
| **E2-A01** | Revisar o DDL de `seg.CredencialTransmissao` | — | **Feito no PR #38** (aprovado sem mudança). Pendente só de: `03` §3.1 e a migration entrarem no mesmo commit do B2, iguais (o `ddl-check` confere), e o bloco de DDL deste arquivo virar ponteiro |
| **E2-A02** | Planos de execução num banco descartável com **dataset sintético de pelo menos 200.000 programas** (por exemplo, 5 anos, vários por dia, nenhum acima de 12 h), gerado por script do Atlas fora do `seed-dev.sql`: (1) interseção da criação e da edição, excluindo o próprio `Id` na edição; (2) `UPDATE` do Worker para `AoVivo` e para `Finalizado`; (3) a consulta da janela do `harbor/auth`, que filtra `Status NOT IN (3, 4)` e por isso não usa o `IX_Programa_Worker`: deve ser seek no `IX_Programa_Grade` com o limite de 12 h em `InicioUtc`, como a interseção | E2-F01, E2-F03, E2-F05 | Parecer com o plano real e o `STATISTICS IO` das três consultas, confirmando seek em `IX_Programa_Grade` (consultas 1 e 3) e `IX_Programa_Worker` (consulta 2); se a consulta da janela pedir índice novo, ele entra no B2 |
| **E2-A03** | **Seed de desenvolvimento pelo `DevDataSeeder`** (C#), que roda no boot da Api depois do migrator e já cria os locutores: uma semana de grade criada pelo comando da E2-F01 (respeita as regras de grade e o lock) e as 3 divulgações de exemplo que o `seed-dev.sql` criava. Saem, no mesmo PR, o bloco `SEED_DEV` do `db-init` (que hoje roda **antes** do migrator, sem as tabelas, e derruba o stack se ligado), o `infra/sql/seed-dev.sql` e a variável `SEED_DEV` do `.env.example`. `Seed__DevData` e `Seed__DevSenha` (de arquivo de segredo, nunca no YAML) entram só no `docker-compose.dev.yml`; o segredo novo entra no `gerar-segredos` (`.sh` e `.ps1`) e na lista do `verificar-paridade-segredos.sh` | E2-F01 | `compose -f docker-compose.yml -f docker-compose.dev.yml up` com o seed ligado sobe o stack e mostra a semana de grade; sem ele, nada muda; em produção o seeder nem é registrado. O Forge implementa; o Atlas revisa |

## Tarefas — Prism (Frontend)

| ID | Tarefa | Depende | Critério de aceite |
|---|---|---|---|
| **E2-P01** | **Grade pública**: semana atual em horário de Brasília (dados em UTC), selo "Ao Vivo" no programa corrente, navegação entre semanas; atualiza por `ProgramaStatusAlterado` sem refresh | E2-F02 | Vitest nos cálculos de fuso; Playwright: o selo aparece sem recarregar quando o Worker muda o status, e some ao encerrar |
| **E2-P02** | **Administração da grade** (Admin): criar, editar, cancelar; o 409 mostra qual programa conflita; `If-Match` com o `ETag` | E2-F02 | Erros da API mapeados para mensagens úteis; teste do fluxo de conflito |
| **E2-P03** | **Painel do locutor**: credencial de transmissão (gerar pedindo a senha atual, ver o segredo **uma vez** com botão de copiar e aviso de que não será mostrado de novo, revogar, idade da credencial), instruções para BUTT/Mixxx com servidor, porta, mount e identificador; estado "no ar" por `TransmissaoAoVivoAlterada`; botão "Encerrar programa". **Admin**: lista de credenciais ativas com revogação | E2-F04, E2-F06 | O segredo não fica em `localStorage`, `sessionStorage` nem no cache do TanStack Query depois de fechar o diálogo; teste que confere |
| **E2-P04** | **Player e "tocando agora"**: metadados do `TocandoAgora` e do `TransmissaoAoVivoAlterada` na barra do player; reconexão do áudio quando o stream volta | E2-F06 | O player continua sem interromper entre páginas (Playwright do Épico 1 segue verde) |

## Agrupamento de entrega

Por decisão do dono do produto, a entrega é por **feature grande**: um PR por linha, commits separados por tarefa, revisão no fim (Guardian em todos; Sentinel no B2 e no B3; Atlas no B1 e no B2).

| PR | Feature | Tarefas | Depende de |
|---|---|---|---|
| **E1-Z** | Fechamento do Épico 1 | Playwright no `compose-smoke` (DoD 2 do `04`), limite de cobertura ≥ 80% no CI (DoD 6) — PR #39 | — |
| **B1** | Grade | F01, F02, F03, A02 (consultas 1 e 2), A03 | E1-Z |
| **B2** | Ao vivo | **F07 primeiro** (*spike* contra stub, com critério de parada), depois F04, F05, F06, F11, F08, F09, F10, A01, A02 (consulta 3) | B1 |
| **B3** | Telas | P01, P02, P03, P04 | contratos do B1 (P01, P02); B2 (P03, P04) |

O B3 pode começar pelo P01 e pelo P02 assim que o B1 estiver mergeado, em paralelo com o B2. O B3-13 do `04` (spec × entregue no Épico 1) fica com o Nexus, fora desses PRs.

## Riscos

| Nível | Risco | Mitigação |
|---|---|---|
| ALTO | O Liquidsoap não conseguir derrubar o source client por uma tarefa periódica, ou não sustentar a falha aberta com teto | *Spike* E2-F07 antes de qualquer outra tarefa do B2, contra stub, com critério de parada |
| ALTO | Locutor no ar depois do horário com o Worker parado | Autorização pela janela calculada no SQL, não pelo `Status` (aceite da E2-F05) |
| ALTO | Credencial de transmissão interceptada (o harbor não tem TLS) | Credencial separada do login, válida só na janela do programa do dono, revogável em ≤ 15 s; harbor só atrás do proxy da E2-F11 com lista de IPs ou VPN. TLS como risco aceito (abaixo) |
| MÉDIO | Bloqueio do locutor legítimo pelo throttle (reconexão automática do BUTT, ou alguém que conhece o identificador) | Só falhas de credencial contam; bloqueio por par (identificador, IP); identificador sozinho nunca bloqueia |
| MÉDIO | Entrada do source client usada para chegar a outra rota interna ou forjar log | Regex estrita de GUID e de segredo antes de qualquer uso, no `radio.liq` e na API |
| MÉDIO | API fora do ar derruba o locutor no meio do programa | Falha aberta só para erro de transporte, só para quem já foi autorizado, com teto de `min(fimUtc + 1 min, último 200 + 15 min)` |
| BAIXO | Relógio divergente na transição | Só o `SYSUTCDATETIME()` do SQL vale na transição e na janela (`00 §5`) |

## Riscos aceitos

**Aceitos pelo dono do produto em 2026-10-15**, por recomendação do Nexus e do Sentinel, e registrados no `05` §4 ("Riscos aceitos do Épico 2").

1. **Falha aberta do ao vivo** (E2-F07 c): com a API ou o SQL fora, o locutor já autorizado continua no ar até o teto de `min(fimUtc + 1 min, último 200 + 15 min)`, ou seja, **no máximo 15 min** sem confirmação. Uma credencial revogada ou um programa encerrado durante a queda só derrubam o locutor quando a API voltar ou no teto. Conexão nova com a API fora é sempre recusada.
2. **Harbor sem TLS** (Sentinel, PR #38): aceito **enquanto** (i) a porta do ao vivo só é alcançável por VPN ou lista de IPs no proxy da E2-F11; (ii) a credencial vale só na janela do programa e é revogável; (iii) com a API no ar, o 403 derruba em ≤ 15 s. **Se (i) deixar de valer** (locutor entrando de IP qualquer, porta aberta para a internet), o TLS vira pré-requisito, terminado no proxy da E2-F11.
