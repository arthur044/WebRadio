# WebRadio — Especificação v1: API REST, SignalR e RBAC

> Base: `/api/v1` (via gateway nginx, mesma origem da SPA). JSON camelCase, enums como string, datas ISO 8601 UTC (`2026-09-27T21:00:00Z`).
> Erros: ProblemDetails (ver `01-dominio.md §3`). Paginação: `?pagina=1&tamanho=20` (máx. 100) → `{ itens, total, pagina, tamanho }`.
> Concorrência: `GET` de recurso editável devolve `ETag` (RowVersion em base64); `PUT`/`PATCH` exigem `If-Match` (sem ele → 428; divergente → 412).
> OpenAPI em `/openapi/v1.json` (só em Development, com UI Scalar em `/scalar`). O Prism gera os tipos a partir dele.

## 1. Matriz RBAC

| Recurso / ação | Anônimo | Ouvinte | Locutor | Admin |
|---|---|---|---|---|
| Ver grade, programa ao vivo, divulgações ativas, info do stream | ✅ | ✅ | ✅ | ✅ |
| Criar pedido de música | ✅ (com `X-Listener-Id`) | ✅ | ✅ | ✅ |
| Ver os próprios pedidos | ✅ (por listener) | ✅ | ✅ | ✅ |
| Fila de moderação, aprovar/rejeitar/marcar tocado | | | ✅ | ✅ |
| Encerrar programa ao vivo | | | ✅ (só o próprio) | ✅ |
| Criar/editar/cancelar programas | | | | ✅ |
| Upload e listagem de mídia | | | ✅ | ✅ |
| Desativar/excluir mídia | | | | ✅ |
| CRUD de divulgações | | | | ✅ |
| Gerir usuários e roles | | | | ✅ |

Políticas ASP.NET: `PodeModerar` (Locutor, Admin), `SomenteAdmin` (Admin). Quem não bate na política recebe 403.

## 2. Autenticação — `/auth`

| Método | Rota | Auth | Corpo → Resposta |
|---|---|---|---|
| POST | `/auth/registrar` | anônimo | `{ nome, email, senha }` → **201** `UsuarioDto` (Role = Ouvinte sempre) |
| POST | `/auth/login` | anônimo | `{ email, senha }` → **200** `{ accessToken, expiraEmUtc, deveTrocarSenha, usuario: UsuarioDto }` + cookie `__Secure-wr_refresh`. Qualquer falha (inclusive conta bloqueada) → **401** genérico (D21) |
| POST | `/auth/refresh` | cookie + `Origin` da mesma origem | → **200** mesmo formato do login + novo cookie (rotação). Inválido → 401 e o cookie é apagado |
| POST | `/auth/logout` | cookie + `Origin` da mesma origem | → **204**; revoga o token e apaga o cookie |
| POST | `/auth/trocar-senha` | Bearer | `{ senhaAtual, novaSenha }` → **204**; revoga as **outras** famílias de refresh e mantém a atual. O access token em uso continua restrito até expirar, então o cliente **chama `POST /auth/refresh` logo depois do 204** para receber um token sem a restrição (`deveTrocarSenha = false`). É a única rota (além de `/auth/me`) liberada enquanto `deveTrocarSenha = true` |
| GET | `/auth/me` | Bearer | → **200** `UsuarioDto` |

`UsuarioDto = { id, nome, email, role, deveTrocarSenha }`

Cliente HTTP: as rotas `/auth/login`, `/auth/registrar`, `/auth/refresh` e `/auth/logout` **nunca** passam pela lógica de "401 → refresh → repetir". Um login errado não pode disparar refresh nem ser reenviado, porque contaria 2 falhas no bloqueio do D21. A restauração de sessão no boot usa o **mesmo lock** entre abas do refresh.

Limites: `/auth/login` 5/min por IP (**a 6ª tentativa no minuto também responde 401 genérico**, com o mesmo status, corpo e headers de uma senha errada (sem `Retry-After`) e **sem custo de hash**, nunca 429: um status distinto serviria de oráculo, e ser limitado depende só do IP, então não há oráculo de timing por conta; F07a); `/auth/registrar` 3/h por IP; senha de 12 a 128 caracteres, checada contra uma lista local de senhas vazadas (S-B02).

Claims do JWT: `sub` (id), `name`, `role`, `jti`, `iat`, `exp`; `iss = webradio-api`, `aud = webradio`; tolerância de relógio de 30 s.

## 3. Usuários — `/usuarios` (SomenteAdmin)

| Método | Rota | Descrição |
|---|---|---|
| GET | `/usuarios?role=&busca=&pagina=&tamanho=` | lista paginada de `UsuarioDto & { ativo, criadoEmUtc, versao }`. **Não existe `GET /usuarios/{id}`**: `versao` (RowVersion em base64) é o valor que o cliente manda no `If-Match` do `PATCH` |
| POST | `/usuarios` | `{ nome, email, senhaInicial, role }` → 201 + `ETag`; o corpo também traz `versao` |
| PATCH | `/usuarios/{id}` | `{ role?, ativo? }` + `If-Match` → 200 + `ETag` novo (e `versao` no corpo). Mudar o papel ou desativar revoga as sessões e derruba o hub do usuário; o último Admin ativo não pode ser rebaixado nem desativado → 409 |
| POST | `/usuarios/{id}/desbloquear-login` | → 204; limpa os contadores de falha de login (Redis) da conta |

## 4. Programas — `/programas`

`ProgramaDto = { id, titulo, descricao, locutor: { id, nome }, inicioUtc, fimUtc, status }`

| Método | Rota | Auth | Descrição |
|---|---|---|---|
| GET | `/programas?deUtc=&ateUtc=` | público | grade no intervalo (máx. 31 dias; os dois parâmetros são obrigatórios) ordenada por `inicioUtc`; exclui `Cancelado` a menos que `incluirCancelados=true` (Admin) |
| GET | `/programas/ao-vivo` | público | **200** `ProgramaDto` ou **204** |
| GET | `/programas/{id}` | público | **200** + `ETag` |
| POST | `/programas` | Admin | `{ titulo, descricao?, locutorId, inicioUtc, fimUtc }` → **201** + `Location`; **409** conflito de grade |
| PUT | `/programas/{id}` | Admin | mesmo corpo + `If-Match`; só `Agendado` |
| POST | `/programas/{id}/cancelar` | Admin | → 200; só `Agendado` |
| POST | `/programas/{id}/encerrar` | Locutor dono / Admin | → 200; vale para `AoVivo` e para `Agendado` cuja janela já começou; `fimUtc = mín(fimUtc, máx(agora truncado ao segundo, inicioUtc + 1 s))` (nunca estende); publica `ProgramaStatusAlterado`; Locutor que não é o dono → **403** |

Validação de horário: `inicioUtc`/`fimUtc` **precisam** terminar em `Z` (offset ≠ 0 → 400). O frontend converte o horário local para UTC antes de enviar.

### 4.1 Credencial de transmissão do locutor (Épico 2)

Rotas `GET`/`POST`/`DELETE /locutores/me/credencial-transmissao` (Locutor, Admin; o `POST` exige `{ senhaAtual }`), `GET /credenciais-transmissao` e `DELETE /usuarios/{id}/credencial-transmissao` (Admin). O segredo só aparece na resposta do `POST`, com `Cache-Control: no-store`. As respostas do `GET` e do `POST` do próprio locutor trazem `conexao: { host, porta, mount }` (o que ele digita no source client; `host` é `null` sem `Harbor__HostPublico`). `encerrar` e `cancelar` de `/programas` publicam `ProgramaStatusAlterado`. Contrato completo em `06-epico-2-tarefas.md`.

## 5. Pedidos de música — `/pedidos`

`PedidoDto = { id, nomeOuvinte, tituloMusica, artista, mensagem, status, criadoEmUtc, moderadoEmUtc, motivoRejeicao, tocadoEmUtc, programaId }`

| Método | Rota | Auth | Descrição |
|---|---|---|---|
| POST | `/pedidos` | público + header `X-Listener-Id` (UUID) | `{ nomeOuvinte, tituloMusica, artista, mensagem? }` → **201** `PedidoDto`; **429** com `Retry-After` (anti-spam ou teto global de pendentes, `07` E3-D10); header ausente, inválido ou `Guid.Empty` → 400 |
| GET | `/pedidos/meus` | público + `X-Listener-Id` (ou Bearer) | `PedidoDto[]` sem paginação: últimos 20 pedidos desse dispositivo/usuário |
| GET | `/pedidos?status=Pendente&pagina=&tamanho=` | PodeModerar | fila de moderação (padrão `Pendente`, mais antigos primeiro). `status` só `Pendente` ou `Aprovado` (aprovados das últimas 24 h); outro valor → 400 (Épico 3, `07`) |
| POST | `/pedidos/{id}/aprovar` | PodeModerar | `{ midiaId? }` → 200; mídia inválida → 400; já moderado → 409 `/erros/pedido-ja-moderado` |
| POST | `/pedidos/{id}/rejeitar` | PodeModerar | `{ motivo }` (1–280) → 200; já moderado → 409 `/erros/pedido-ja-moderado` |
| POST | `/pedidos/{id}/marcar-tocado` | PodeModerar | → 200; só `Aprovado`, senão 409 `/erros/pedido-ja-moderado` |

## 6. Mídia — `/midias` (PodeModerar, salvo indicação)

`MidiaDto = { id, nomeOriginal, titulo, artista, tipoMidia, mimeType, tamanhoBytes, duracaoSegundos, statusSanitizacao, motivoRejeicao, dataUploadUtc, ativo }` (o hash sai em hex só para Admin)

| Método | Rota | Descrição |
|---|---|---|
| POST | `/midias/uploads` | `{ nomeOriginal, tipoMidia, tamanhoBytes, mimeTypeDeclarado, titulo?, artista? }` → **201** `{ midiaId, upload: { url, campos: {chave:valor}, expiraEmUtc } }`. `url` = `<origem>/storage/quarentena` (mesma origem). O browser faz `multipart/form-data POST` para `url` com os `campos` + `file` por último. `tamanhoBytes` acima do limite do tipo → 400; mais de 20/h ou 5 abertos por usuário → 429 (S-M06). `nomeOriginal` sem caracteres de controle ou bidi, sem `/ \ :` (S-M07) |
| POST | `/midias/{id}/concluir` | → **202**; objeto ausente ou de tamanho diferente → 400 (o registro continua em `AguardandoUpload` até expirar) |
| GET | `/midias?tipo=&status=&busca=&pagina=&tamanho=` | `busca` em Título/Artista/NomeOriginal |
| GET | `/midias/{id}` | + `ETag` |
| GET | `/midias/{id}/preview` | **302** para uma URL pré-assinada pública de GET (5 min) em `/storage/midia/…`, com `response-content-type=audio/mpeg` e `response-content-disposition=inline; filename*=UTF-8''{id}.mp3`; só `Aprovado`. Nunca há URL pré-assinada para a `quarentena` |
| PATCH | `/midias/{id}` | `{ titulo?, artista?, ativo? }` + `If-Match` (**Admin** para `ativo`) |
| DELETE | `/midias/{id}` | Admin; → 204. Se já houver reprodução registrada, só desativa (histórico preservado) → 200 |

## 7. Divulgações — `/divulgacoes`

`DivulgacaoDto = { id, titulo, mensagem, imagemChave, imagemUrl, linkDestino, prioridade, inicioExibicaoUtc, fimExibicaoUtc, ativo }` (`imagemUrl` = URL pública derivada de `imagemChave`; as duas nulas sem imagem)

| Método | Rota | Auth |
|---|---|---|
| GET | `/divulgacoes/ativas` | público (ordenadas por `prioridade` desc e depois `id`; cache HTTP `max-age=30`) |
| GET | `/divulgacoes?pagina=&tamanho=` | Admin |
| GET | `/divulgacoes/{id}` | Admin → **200** `DivulgacaoDto` + `ETag` (fonte do `If-Match` do `PUT`); inexistente → 404 |
| POST | `/divulgacoes` | Admin → 201 `DivulgacaoDto` + `ETag` + `Location: /api/v1/divulgacoes/{id}`. Corpo: `{ titulo, mensagem, imagemChave?, linkDestino?, prioridade, inicioExibicaoUtc?, fimExibicaoUtc?, ativo }`. O corpo leva `imagemChave?` (a `chave` do `POST /divulgacoes/imagens`), nunca uma URL (Épico 3, E3-D5 do `07`) |
| PUT | `/divulgacoes/{id}` | Admin + `If-Match`. **Substitui o recurso inteiro**: o corpo é o mesmo do `POST`, e `imagemChave` ausente ou `null` = divulgação sem imagem. Para manter a imagem, o cliente reenvia a `imagemChave` do DTO. → 200 + `ETag` novo |
| DELETE | `/divulgacoes/{id}` | Admin → 204 |
| POST | `/divulgacoes/imagens` | Admin; **`multipart/form-data`** com `file` (≤ 2 MB, `RequestSizeLimit`) → **201** `{ chave, imagemUrl }`. A API confere formato (JPEG ou PNG; WebP só na saída) e dimensões pelo cabeçalho, **recusa** lado acima de 4096 px, recodifica para WebP, tira os metadados e grava em `publico/` com `Content-Type` do servidor. Corpo acima de 2 MB → **413**; não é imagem aceita ou passa de 4096 px → **400** (S-A05); mais de **30 por hora** por Admin → **429** |

## 8. Stream e saúde

| Método | Rota | Descrição |
|---|---|---|
| GET | `/stream/info` | público → `{ url: "/stream/radio.mp3", formato: "audio/mpeg", bitrateKbps: 128, tocandoAgora: { titulo, artista, tipo } \| null, aoVivo: { noAr, programaId?, locutorNome? } }` (`aoVivo` a partir do Épico 2) |
| GET | `/health/live` | **só na porta interna `:8081`** (D17, S-B05): processo de pé |
| GET | `/health/ready` | **só na `:8081`**: SQL, MinIO, Redis (Redis fora → `Degraded`, não `Unhealthy`) |

## 9. Endpoints internos — só no listener Kestrel `:8081` (D17)

O grupo `/api/v1/internal` (e `/health/*`) tem um filtro de endpoint que exige `HttpContext.Connection.LocalPort == 8081` (a porta real do socket; **nunca** `RequireHost`, que compara o cabeçalho `Host`, controlado pelo cliente), com o listener `:8081` do Kestrel ligado só ao IP da rede `playout` e a `127.0.0.1` (o healthcheck roda dentro do contêiner; S-A06); a porta 8081 só está na rede `playout` e o nginx nunca a encaminha. Autenticação: Bearer `Playout__Token` comparado com `CryptographicOperations.FixedTimeEquals`, aceitando também `Playout__TokenAnterior` durante a rotação. O Liquidsoap chama `http://api:8081/api/v1/internal/...`.

| Método | Rota | Descrição |
|---|---|---|
| GET | `/internal/playout/proxima` | **200** `{ midiaId, pedidoId, url, titulo, artista }` (URL pré-assinada interna `http://minio:9000/midia/…`, 30 min) ou **204** |
| POST | `/internal/playout/eventos` | `{ tipo: "FaixaIniciada", midiaId?, pedidoId?, titulo?, artista?, ocorridoEmUtc }` → 202. `midiaId` nulo = faixa da rotação local ou do ao vivo (só atualiza "tocando agora"). `titulo`/`artista` com até 200 caracteres, sem caracteres de controle (S-A09) |
| POST | `/internal/harbor/auth` | **Épico 2**: `{ usuario, senha, enderecoIp }` do source client → **200** `{ programaId, fimUtc }` ou 401. `usuario` é o **identificador opaco** da credencial de transmissão do locutor (nunca o e-mail) e `senha` é o segredo dela, não a senha da conta. Aceita só o locutor dono do programa cuja **janela** (`InicioUtc <= agora < FimUtc`, calculada no SQL) está em curso (S-A08). Contrato completo em `06-epico-2-tarefas.md` |
| GET | `/internal/harbor/autorizacao/{identificador}` | **Épico 2**: **200** `{ programaId, fimUtc }` enquanto a autorização do `auth` valer; 403 quando deixar de valer (o Liquidsoap derruba o ao vivo). Ver `06` |
| POST | `/internal/harbor/eventos` | **Épico 2**: `{ tipo: "Conectado" \| "Desconectado", identificador, ocorridoEmUtc }` → 202; publica `TransmissaoAoVivoAlterada`. Ver `06` |

## 10. SignalR — hub `/hubs/radio`

Conexão: `/hubs/radio?listenerId={uuid}` e opcionalmente `access_token={jwt}` (o padrão do SignalR para WebSocket). Transporte preferido: WebSockets. Com várias instâncias da API, backplane Redis.

Grupos atribuídos pelo servidor no `OnConnectedAsync` (o cliente **não** escolhe grupo):
- todos → canal global
- `listener:{deviceHash}` → derivado do `listenerId`
- `locutores` → se o JWT tiver `role ∈ {Locutor, Admin}`
- `admins` → se o JWT tiver `role = Admin` (Épico 2, E2-F12)
- `usuario:{usuarioId}` → todo JWT autenticado, a partir do `sub` (Épico 2, E2-F12)

Nenhum grupo autenticado é atribuído com o token restrito de troca de senha.

Eventos servidor → cliente (todos com `eventoId` e `ocorridoEmUtc`; o cliente descarta `eventoId` repetido). **A ordem de chegada não é garantida** quando há mais de uma réplica da API: para eventos de estado (`ProgramaStatusAlterado` por `programaId`, `TocandoAgora`), o cliente guarda o último `eventoId` aplicado por chave e ignora o que chegar com `eventoId` menor. A comparação é **numérica** (o `eventoId` é o id inteiro do outbox enviado como texto; como texto, "10" < "9"); `TocandoAgora` tem uma chave só:

| Evento | Destino | Payload |
|---|---|---|
| `ProgramaStatusAlterado` | todos | `{ programaId, titulo, status, locutorNome }` |
| `TocandoAgora` | todos | `{ titulo, artista, tipoMidia, pedidoId? }` |
| `TransmissaoAoVivoAlterada` | todos | `{ noAr, programaId?, locutorNome? }` (Épico 2) |
| `AlertaHarborFalhas` | `admins` (+ `usuario:{usuarioId}` do dono no tipo `credencial`) | `{ tipo, falhas, janelaMinutos, locutorId?, locutorNome?, credencialAtiva? }` (Épico 2; contrato no `06`, E2-F12) |
| `PedidoCriado` | `locutores` | `PedidoDto` |
| `PedidoModerado` | `listener:{hash}` + `locutores` | `{ pedidoId, status, motivoRejeicao? }`; `status` = `Aprovado`, `Rejeitado` ou `Expirado` (o Worker emite o `Expirado`; Épico 3, E3-D3 do `07`) |
| `PedidoTocado` | `listener:{hash}` + `locutores` | `{ pedidoId }` |
| `MidiaSanitizada` | `locutores` | `{ midiaId, statusSanitizacao, motivoRejeicao? }` |

No MVP não há métodos cliente → servidor: toda ação passa pela REST (`MaximumReceiveMessageSize` = 1 KB).
Segurança do hub (S-M08, S-M09): `CloseOnAuthenticationExpiration = true`; mudar o papel, desativar o usuário ou trocar a senha derruba as conexões dele; `listenerId` precisa ser um UUID válido (senão a conexão é recusada); a query string do hub **não** vai para os logs do nginx nem do Serilog.
