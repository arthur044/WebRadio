# Épico 3 — Pedidos de música, moderação e divulgações: pacote de tarefas

> Autor: Nexus · 2026-10-29 · Referências: `00-arquitetura.md` (Fluxo B), `01-dominio.md` (§2.4 `PedidoMusica`, §2.6 `Divulgacao`), `02-api-rest.md` (§5, §7, §10), `03-schema-sqlserver.sql` (`interacao.*`), `05-seguranca-auditoria.md` (S-A05, S-B03, S-B10, S-M08, S-M14).
> Donos: **Forge** (backend, Worker, contêineres), **Atlas** (banco), **Prism** (frontend), **Sentinel** (revisão de segurança do upload de imagem e do throttle).
> O `05` continua **normativo**: cada achado que cita o Épico 3 vira critério de aceite da tarefa correspondente. A checklist da §8 do `05` é o quality gate de todo PR.
> **v1.0 (2026-10-29):** primeira versão. Domínio, DDL e índices de `interacao.PedidoMusica` e `interacao.Divulgacao` já estão na `main` desde o Épico 1 (E1-F03, migration `Inicial`); o Épico 3 é Application, API, Worker e telas.

## O que o Épico 3 entrega

1. **Pedido de música sem conta.** O ouvinte manda nome, música, artista e dedicatória opcional. O anti-spam é de 1 pedido a cada 5 min por dispositivo, até 3 por IP na mesma janela e 1 por usuário logado.
2. **Moderação em tempo real.** O locutor (ou o Admin) vê a fila de pedidos pendentes chegar sem refresh, aprova, rejeita com motivo ou marca como tocado. O ouvinte vê a decisão em "Meus pedidos" sem recarregar.
3. **Expiração.** O Worker expira os pedidos pendentes há mais de 120 min e avisa o ouvinte.
4. **Divulgações.** O Admin cria, edita e remove divulgações com imagem opcional. A API recodifica a imagem antes de publicar. O público vê o Mural com as divulgações ativas.

## Fora do Épico 3

- **Pedido tocado pelo playout** (`fila_api`, `GET /internal/playout/proxima` devolvendo um pedido com mídia): depende da biblioteca de mídia, que é do **Épico 4**. Neste épico o `midiaId` do `aprovar` continua no contrato, mas nenhuma mídia `Aprovado` existe, então qualquer `midiaId` responde **400**. O locutor toca o pedido manualmente e marca `Tocado`. O `proxima` continua respondendo **204**.
- **Relatório de mais tocadas** (`rel.usp_MaisTocadas`, rascunho em `docs/rascunho-relatorio-mais-tocadas.md`): precisa de `midia.Reproducao`, que exige `MidiaId`. Vai para o Épico 4.
- **Sanitizer isolado para imagens**: a recodificação fica na API neste épico (S-B10) e muda para o `sanitizer` no Épico 4.
- **Notificação ao ouvinte fora do site** (e-mail, push): fora da v1.

## Definição de pronto do Épico

1. No `compose-smoke`: um `POST /pedidos` anônimo responde **201**; a tela de moderação aberta por um locutor (Playwright) mostra o pedido **sem recarregar**; ao aprovar, a tela "Meus pedidos" do ouvinte (outro contexto do browser, mesmo `listenerId`) muda para "Aprovado" **sem recarregar**. O mesmo vale para rejeitar (com o motivo visível) e marcar tocado.
2. Throttle no `compose-smoke`, **atrás do nginx**: o segundo pedido do mesmo `listenerId` em 5 min → **429** com `Retry-After` e `retryAfterSeconds`; o quarto pedido de `listenerId`s diferentes no mesmo IP → **429**. Isso prova que o `KnownProxies` está certo (com ele errado, todo mundo tem o IP do nginx e o teste de IP passa por acaso: o teste usa **dois** IPs de origem distintos e confere que o segundo IP não é bloqueado pelo primeiro).
3. Com o Redis parado, o throttle cai para o SQL, o `/health/ready` fica `Degraded` e o segundo pedido do mesmo dispositivo continua recebendo **429**.
4. Dois locutores aprovando o mesmo pedido ao mesmo tempo: **um** 200 e **um** 409 (teste com SQL real).
5. Um pedido `Pendente` criado com `CriadoEmUtc` vencido vira `Expirado` no ciclo seguinte do Worker, e o ouvinte recebe `PedidoModerado` com `status = Expirado`.
6. Upload de imagem de divulgação: um JPEG com EXIF de GPS volta como **WebP sem metadados**; SVG, GIF, HTML renomeado para `.jpg`, arquivo > 2 MB e imagem com mais de 4096 px de lado são recusados **antes** de decodificar o corpo inteiro; a imagem é servida por `/storage/publico/...` com `Content-Type: image/webp`, `X-Content-Type-Options: nosniff` e `Content-Security-Policy: sandbox`; `GET /storage/publico/` (listagem) → 403/404.
7. O Mural mostra só divulgações ativas (janela e `Ativo`), por prioridade, com o texto como texto puro e o link com `rel="noopener noreferrer"`.
8. Tudo que já era exigido nos Épicos 1 e 2 continua verde (CI completo, cobertura ≥ 80% em Domain e Application).

## Decisões deste épico

| ID | Decisão | Motivo |
|---|---|---|
| E3-D1 | O `ProgramaId` do pedido é o programa **da janela** (`InicioUtc <= agora < FimUtc` e `Status NOT IN (Finalizado, Cancelado)`), a mesma regra do harbor, e não `Status = AoVivo` | Com o Worker parado o pedido continua associado ao programa certo, igual à autorização do ao vivo (`06`, "Autorização do ao vivo") |
| E3-D2 | Ordem do throttle: dispositivo (`SET NX`) → usuário logado (`SET NX`) → IP (`INCR`). Uma reserva feita antes de um 429 por IP **não** é desfeita | Desfazer exigiria script Lua ou transação; o efeito é o dispositivo esperar 5 min, que é o que o anti-spam já impõe |
| E3-D3 | `PedidoModerado` passa a carregar também `status = Expirado`, emitido pelo Worker | O ouvinte precisa saber que o pedido não vai tocar; um evento novo só para isso não acrescenta nada. **[CONTRATO]** `02 §10` |
| E3-D4 | Recodificação de imagem com **SkiaSharp** (MIT): `SKCodec` lê o cabeçalho e confere formato e dimensões antes de decodificar; saída WebP qualidade 85 | Atende o S-B10 (dimensões no cabeçalho). Aceita só JPEG, PNG e WebP, pelo formato detectado e não pela extensão nem pelo `Content-Type` do cliente |
| E3-D5 | Chave da imagem: `divulgacoes/{guid:N}.webp`. `POST` e `PUT` de divulgação só aceitam `imagemChave` com esse formato **e** que exista no bucket (`HEAD`) | Impede apontar uma divulgação para um objeto arbitrário do bucket |
| E3-D6 | Imagens órfãs (enviadas e nunca ligadas, ou de divulgação removida ou editada) são apagadas pela `Manutencao` diária quando têm mais de 24 h | O bucket é público; uma imagem não publicada fica acessível só por uma chave impossível de adivinhar, e por no máximo um dia |
| E3-D7 | Os campos de texto do pedido passam pelo `MetadadoSeguro` (o mesmo validador do harbor e do `TocandoAgora`): NFC, sem `Cc`/`Cf`/`Cs` (com as exceções de emoji do `06`), sem bidi, tamanhos do `01 §2.4` | Um validador só para todo texto que vem de anônimo e aparece para outro usuário |

## Contratos alterados

- **`02 §10`**: `PedidoModerado` passa a aceitar `status` `Aprovado`, `Rejeitado` ou `Expirado` (E3-D3).
- **`02 §7`**: `POST /divulgacoes` e `PUT /divulgacoes/{id}` recebem `imagemChave?` (a `chave` devolvida pelo `POST /divulgacoes/imagens`), e não uma URL. A resposta continua com `imagemUrl`, derivada da chave. `POST /divulgacoes/imagens` tem limite de **30 por hora por Admin** (429 como o resto).
- Sem mudança de DDL prevista. Se o Atlas pedir índice novo na E3-A01, ele entra no PR C1 com migration e `03` no mesmo commit (o `ddl-check` confere).

## Tarefas — Forge (Backend, Worker e contêineres)

| ID | Tarefa | Depende | Critério de aceite |
|---|---|---|---|
| **E3-F01** | **Criar pedido** (`POST /pedidos`): `CriarPedidoCommand` com `IThrottledRequest` no pipeline (`Logging → Validation → Throttle → Handler`, Fluxo B); validação com `MetadadoSeguro` (E3-D7); `X-Listener-Id` ausente ou que não é UUID → 400; `ProgramaId` pela janela (E3-D1); `UsuarioId` quando há Bearer válido; `ListenerDeviceHash` e `ListenerIpHash` pelo `IListenerIdentity`; throttle Redis na ordem do E3-D2, com fallback SQL (`EXISTS` nos últimos 5 min pelos índices de hash) e health `Degraded`; outbox `PedidoCriado` para `locutores` na mesma transação | — | TDD no handler e no behavior de throttle; 429 com `Retry-After` = TTL restante e `retryAfterSeconds`; teste do fallback com o Redis fora (`[SqlFact]`); nenhum IP nem `listenerId` em claro em log ou banco (teste que varre os logs da requisição) |
| **E3-F02** | **Consultas**: `GET /pedidos/meus` (últimos 20 por `ListenerDeviceHash`, ou por `UsuarioId` com Bearer); `GET /pedidos?status=&pagina=&tamanho=` para `PodeModerar` (padrão `Pendente`, mais antigos primeiro, `tamanho` ≤ 100). O `PedidoDto` **não** expõe hash nenhum | F01 | A consulta da fila com `Status = 1` usa o `IX_PedidoMusica_Fila` (predicado constante, ver A01); um ouvinte não vê pedido de outro dispositivo |
| **E3-F03** | **Moderação**: `aprovar` (`midiaId?`: precisa ser `ArquivoMidia` `Aprovado`, `Ativo`, tipo `Musica`, senão 400), `rejeitar` (`motivo` 1–280 pelo `MetadadoSeguro`), `marcar-tocado` (só `Aprovado`). `PedidoJaModerado` e `DbUpdateConcurrencyException` do `RowVersion` → **409** `/erros/transicao-invalida`. Outbox na mesma transação: `PedidoModerado` e `PedidoTocado` para `listener:{hash}` (quando o hash não é nulo) + `locutores` | F01 | `[SqlFact]` com dois `aprovar` concorrentes → um 200 e um 409; pedido anonimizado (hash nulo) ainda pode ser moderado e só vai para `locutores`; Ouvinte → 403 |
| **E3-F04** | **Expiração no Worker** (só o líder, ciclo de 60 s): `UPDATE ... SET Status = 5 OUTPUT ...` dos `Pendente` com `CriadoEmUtc < SYSUTCDATETIME() - Pedidos:ExpiracaoMinutos` (padrão 120), com `PedidoModerado { status: Expirado }` no outbox na mesma transação (E3-D3) | F03 | Idempotente (rodar duas vezes não gera evento repetido); teste com SQL real; o plano usa o `IX_PedidoMusica_Fila` |
| **E3-F05** | **Divulgações**: `GET /divulgacoes/ativas` (público, por prioridade desc, `Cache-Control: public, max-age=30`), `GET /divulgacoes` paginado (Admin), `POST` 201, `PUT` com `If-Match`/`ETag` (412/428 como na grade), `DELETE` 204. S-B03 completo no domínio: `https`, sem `userinfo`, host que não é IP literal privado, loopback ou link-local; o servidor **nunca** busca a URL. `imagemChave` conforme o E3-D5 | — | TDD no domínio (S-B03, janela, prioridade); `ativas` respeita a janela com o relógio do servidor; `PUT` sem `If-Match` → 428, com `ETag` velho → 412 |
| **E3-F06** | **Upload de imagem** (`POST /divulgacoes/imagens`, Admin): `multipart/form-data` com `RequestSizeLimit` de 2 MB **no endpoint** (e `client_max_body_size` compatível no nginx só nessa rota); E3-D4: formato pelo `SKCodec` (só JPEG, PNG, WebP), lado ≤ 4096 px e ≤ 16.777.216 px conferidos **antes** do decode; orientação do EXIF aplicada e todo metadado descartado; WebP q85; `PutAsync` no bucket `publico` com `Content-Type: image/webp` definido pelo servidor; limite de 30/h por Admin. Acrescenta `PutAsync`, `DeleteAsync` e a listagem por prefixo ao `IObjectStorage` | F05 | Testes com arquivos de fixture: JPEG com GPS → WebP sem EXIF; SVG, GIF, HTML com extensão `.jpg`, PNG de 10000×10000 com poucos bytes (bomba de pixel) e corpo > 2 MB → 400/413 sem decodificar; teste de que o `SKCodec` não decodifica antes da checagem (tempo e memória limitados) |
| **E3-F07** | **Ciclo de vida da imagem**: `DELETE` da divulgação e troca de imagem no `PUT` apagam o objeto antigo depois do commit (falha no storage só gera warning); a `Manutencao` diária apaga `divulgacoes/*` sem referência e com mais de 24 h (E3-D6) | F06 | Teste de integração com MinIO real: órfã velha some, órfã nova e imagem ligada ficam |
| **E3-F08** | **Smoke do Épico 3** no `compose-smoke`: DoD 2, 3 e 6 por script (`infra/scripts/smoke-pedidos.sh`), incluindo os dois IPs de origem do DoD 2 e o Redis parado do DoD 3 | F01–F07 | Job verde no CI; o script deixa o Redis de pé no fim, mesmo se falhar (`trap`) |

## Tarefas — Atlas (Banco de dados)

| ID | Tarefa | Depende | Critério de aceite |
|---|---|---|---|
| **E3-A01** | Planos de execução num banco descartável com dataset sintético (pelo menos 500.000 pedidos, 5% pendentes, 30 dias de hashes): (1) fila de moderação `Status = 1` por `CriadoEmUtc`; (2) `meus pedidos` por `ListenerDeviceHash` e por `UsuarioId`; (3) `UPDATE` da expiração; (4) fallback do throttle (`EXISTS` por hash nos últimos 5 min); (5) `divulgacoes/ativas` | E3-F02, E3-F04, E3-F01, E3-F05 | Parecer em `docs/pareceres/` com o plano real e o `STATISTICS IO` das cinco; seek nos índices filtrados (`IX_PedidoMusica_Fila`, `IX_Divulgacao_Ativas`) e nos de hash; se pedir índice novo, ele entra no C1 |
| **E3-A02** | Revisar se a anonimização de 30 dias da `Manutencao` (hashes nulos) segue barata com o volume do A01 e se `NomeOuvinte`/`Mensagem` precisam de retenção própria (LGPD) | E3-A01 | Recomendação no parecer; mudança de retenção, se houver, volta ao Nexus antes de virar tarefa |

## Tarefas — Prism (Frontend)

| ID | Tarefa | Depende | Critério de aceite |
|---|---|---|---|
| **E3-P01** | **Pedir Música** (substitui o placeholder do E1-P08): formulário com nome, música, artista e dedicatória (contadores de caractere pelos limites do `01 §2.4`), `X-Listener-Id` do E1-P07; 429 vira contagem regressiva a partir do `retryAfterSeconds`; erros de validação pelo `descreverErro` | E3-F01 | Vitest do formulário e do 429; Playwright de preview com a API mockada |
| **E3-P02** | **Meus pedidos**: lista dos últimos 20 com status; atualiza por `PedidoModerado` e `PedidoTocado` sem refresh; ressincroniza por REST ao reconectar o hub; motivo da rejeição visível como texto puro | E3-F02, E3-F03 | Playwright no `compose-smoke` (DoD 1, lado do ouvinte) |
| **E3-P03** | **Fila de moderação** no Estúdio (Locutor e Admin): pendentes mais antigos primeiro, chegando por `PedidoCriado`; aprovar, rejeitar (diálogo com motivo obrigatório, 1–280) e marcar tocado na lista de aprovados; 409 mostra "outro locutor já moderou" e tira o item; nome e dedicatória como texto puro | E3-F02, E3-F03 | Playwright no `compose-smoke` (DoD 1, lado do locutor); teste do 409 |
| **E3-P04** | **Administração de divulgações** (Admin): lista, criar, editar com `If-Match`, remover com confirmação; upload de imagem com prévia (aceita só `image/jpeg,image/png,image/webp`, avisa > 2 MB antes de enviar), prioridade 0–100, janela de exibição em horário de Brasília (dados em UTC), link só `https://` | E3-F05, E3-F06 | Vitest do formulário e dos erros (412, 413, 400 do link); teste do fuso como na grade |
| **E3-P05** | **Mural** (substitui o placeholder): cards por prioridade com imagem (`loading="lazy"`, `alt` = título), mensagem como texto puro com quebras de linha, link com `target="_blank" rel="noopener noreferrer"` | E3-F05 | Vitest de que HTML na mensagem aparece como texto; Playwright de preview (DoD 7) |

## Agrupamento de entrega

Por decisão do dono do produto, a entrega é por **feature grande**: um PR por linha, commits separados por tarefa, revisão no fim.

| PR | Feature | Tarefas | Revisão | Depende de |
|---|---|---|---|---|
| **C1** | Pedidos e moderação (backend) | F01, F02, F03, F04, A01, A02, parte de pedidos do F08 | Guardian, Atlas, Sentinel (throttle e LGPD) | — |
| **C2** | Divulgações (backend) | F05, F06, F07, parte de imagem do F08 | Guardian, Sentinel (S-A05, S-B03, S-B10) | — |
| **C3** | Telas | P01, P02, P03, P04, P05 | Guardian | contratos do C1 e do C2 |

O C1 e o C2 podem andar em paralelo no worktree do Forge (branches separadas). O Prism começa o C3 pelos contratos do `02` com a API mockada e abre o PR quando o C1 e o C2 estiverem mergeados, para o Playwright do `compose-smoke` rodar contra o stack real. O Atlas começa o A01 assim que o F02 e o F04 tiverem o SQL definido (pode pedir o texto ao Forge antes do PR).

## Riscos

| Nível | Risco | Mitigação |
|---|---|---|
| ALTO | `KnownProxies` errado: todo pedido parece vir do IP do nginx e o limite de 3 por IP bloqueia a audiência inteira (`05`, seção dos proxies) | DoD 2 com dois IPs de origem distintos no smoke |
| ALTO | Parser de imagem dentro da API, que tem JWT e pepper (S-B10) | Só Admin, 2 MB no endpoint, formato e dimensões pelo cabeçalho antes do decode, saída recodificada; move para o `sanitizer` no Épico 4 |
| MÉDIO | Texto de anônimo exibido para locutor e no Mural (XSS, bidi, spoofing) | `MetadadoSeguro` no servidor; texto puro no React (nunca `dangerouslySetInnerHTML`); CSP do S-M14 |
| MÉDIO | Locutor rebaixado continua recebendo `PedidoCriado` (nome e dedicatória) | Já coberto pelo S-M08 do Épico 1 (`CloseOnAuthenticationExpiration` e desconexão em `AlterarRole`/`Desativar`); o C1 acrescenta teste de que o rebaixado para de receber |
| BAIXO | Pedido `Aprovado` sem mídia fica `Aprovado` para sempre se o locutor não marcar tocado | Aceitável no Épico 3; a fila de aprovados do P03 deixa isso visível. Rever no Épico 4, com o playout |
| BAIXO | Imagem órfã acessível no bucket público por até 24 h | Chave com GUID; E3-D6 |
