# WebRadio — Especificação v1: Modelo de Domínio

> Contrato entre o Forge (entidades C#) e o Atlas (tabelas). Nomes de propriedade em C# = nomes de coluna.
> Convenções: Ids `Guid` (gerados pelo EF como GUID sequencial compatível com SQL Server); datas `DateTime` **UTC**, sempre com sufixo `Utc`;
> concorrência otimista por `RowVersion` (`byte[]`, exposto na API como ETag); enums gravados como `tinyint`.
> Collation: o banco é `Latin1_General_100_CI_AI_SC` (busca sem acento). Colunas de **comparação exata** usam `Latin1_General_100_BIN2`: `EmailNormalizado`, `Bucket`, `ChaveStorage`, `MimeTypeDeclarado`, `MimeType`, `EtagUpload`. No EF, `.UseCollation("Latin1_General_100_BIN2")` (decisão do Atlas, A01).
> Eventos de domínio: padrão D22 (`00-arquitetura.md`).
> **Estado persistido:** todo estado que influencia comportamento de uma entidade precisa ter coluna no `03` e mapeamento no EF. Campo privado não persistido só serve para cache derivado. Cada regra que depende de estado precisa de um teste que **reidrata** a entidade pelo caminho do EF (construtor sem parâmetros + propriedades), porque um teste que reutiliza a mesma instância não pega esse erro (achado N1 do Guardian no PR #1).

## 1. Enums (valores fixos: **nunca renumerar**)

| Enum | Valores |
|---|---|
| `Role` | 1 `Admin`, 2 `Locutor`, 3 `Ouvinte` |
| `StatusPrograma` | 1 `Agendado`, 2 `AoVivo`, 3 `Finalizado`, 4 `Cancelado` |
| `StatusPedido` | 1 `Pendente`, 2 `Aprovado`, 3 `Rejeitado`, 4 `Tocado`, 5 `Expirado` |
| `TipoMidia` | 1 `Vinheta`, 2 `Comercial`, 3 `Musica` |
| `StatusSanitizacao` | 1 `AguardandoUpload`, 2 `Pendente`, 3 `EmAnalise`, 4 `Aprovado`, 5 `Quarentena`, 6 `Rejeitado` |

Na API os enums trafegam como **string** (`"AoVivo"`) via `JsonStringEnumConverter`.

## 2. Entidades

### 2.1 `Usuario` (schema `seg`)
| Campo | Tipo | Regra |
|---|---|---|
| Id | Guid | PK |
| Nome | string(120) | obrigatório |
| Email | string(254) | obrigatório, formato de e-mail |
| EmailNormalizado | string(254) | `ToUpperInvariant()` do e-mail; **único** |
| SenhaHash | string(512) | `PasswordHasher<Usuario>`; nunca serializado |
| Role | Role | um papel por usuário; hierarquia de políticas `Admin ⊃ Locutor ⊃ Ouvinte` |
| Ativo | bool | inativo não autentica, e o refresh dele falha |
| DeveTrocarSenha | bool | `true` no Admin semeado; enquanto for `true`, o token só permite `POST /auth/trocar-senha` e `GET /auth/me` |
| CriadoEmUtc / AtualizadoEmUtc | DateTime | |
| RowVersion | byte[] | |

> v1.1: `FalhasLogin` e `BloqueadoAteUtc` **saíram** da entidade. O bloqueio de login fica em Redis por (conta, IpHash), e o domínio não o conhece (D21 / S-A12).

Comportamentos: `AlterarRole(role)`, `Desativar()`, `TrocarSenha(hash)` (zera `DeveTrocarSenha`). Rebaixar **ou desativar** o último Admin ativo é proibido, venha de quem vier. O Domain recebe `ehUltimoAdminAtivo` do handler, que o calcula **dentro da transação sob `sp_getapplock 'seg:admins'` exclusivo**. Sem o lock, dois Admins rebaixando um ao outro ao mesmo tempo veem "não sou o último" e o sistema fica sem Admin. Evento emitido: `UsuarioCredenciaisAlteradas { UsuarioId, Motivo: RoleAlterada | Desativado | SenhaTrocada }`. `AlterarRole`, `Desativar` e `TrocarSenha` disparam a revogação de todas as famílias de refresh e a desconexão do hub (S-M11, S-M08).

### 2.2 `RefreshToken` (schema `seg`)
| Campo | Tipo | Regra |
|---|---|---|
| Id | Guid | PK |
| UsuarioId | Guid | FK Usuario |
| TokenHash | byte[32] | SHA-256 do token opaco (256 bits aleatórios, base64url); **único** |
| FamiliaId | Guid | igual para toda a cadeia de rotação |
| ExpiraEmUtc | DateTime | criação + 7 dias |
| CriadoEmUtc | DateTime | |
| CriadoPorIpHash | byte[32]? | HMAC do IP |
| RevogadoEmUtc | DateTime? | |
| SubstituidoPorId | Guid? | token seguinte da rotação |

Regra: usar um token já revogado → revoga **todos** da `FamiliaId` (sinal de roubo) → 401.

### 2.3 `Programa` (schema `grade`)
| Campo | Tipo | Regra |
|---|---|---|
| Id | Guid | PK |
| Titulo | string(120) | obrigatório |
| Descricao | string(1000)? | |
| LocutorId | Guid | FK Usuario com Role ∈ {Locutor, Admin}, checado no handler |
| InicioUtc | DateTime | precisão de segundo; `Kind = Utc` obrigatório (o validador rejeita `Unspecified`/`Local`) |
| FimUtc | DateTime | `FimUtc > InicioUtc`; duração entre 5 min e 12 h |
| Status | StatusPrograma | |
| CanceladoEmUtc | DateTime? | |
| CriadoEmUtc / AtualizadoEmUtc / RowVersion | | |

Máquina de estados:
```
Agendado ──(worker: Inicio ≤ agora < Fim)──► AoVivo ──(worker: Fim ≤ agora)──► Finalizado
   │                                           │
   │                                           └─(locutor dono/admin: Encerrar) ──► Finalizado (FimUtc = agora)
   ├──(worker: Fim ≤ agora, janela perdida)──► Finalizado
   └──(admin: Cancelar)──► Cancelado
```
- Só `Agendado` pode ser editado (título, descrição, locutor, horários). Mudar o horário passa de novo pela checagem de sobreposição.
- **Invariante de grade** (checada na aplicação, com o lock descrito no D7): nenhum par de programas com `Status ∈ {Agendado, AoVivo}` tem intervalos que se cruzam. Os intervalos são semiabertos `[Inicio, Fim)`, então programas encostados (fim de um = início do outro) são válidos.
- Não se cria programa com `InicioUtc < agora - 1 min`.

### 2.4 `PedidoMusica` (schema `interacao`) — *SongRequest*
| Campo | Tipo | Regra |
|---|---|---|
| Id | Guid | PK |
| ProgramaId | Guid? | programa AoVivo no momento do pedido, se houver |
| UsuarioId | Guid? | preenchido se o ouvinte estiver logado |
| NomeOuvinte | string(60) | obrigatório; sem controle/HTML (validador) |
| TituloMusica | string(200) | obrigatório |
| Artista | string(200) | obrigatório |
| Mensagem | string(280)? | dedicatória opcional |
| ListenerDeviceHash | byte[32]? | HMAC do `X-Listener-Id`; zerado (NULL) após 30 dias (LGPD) |
| ListenerIpHash | byte[32]? | HMAC do IP; mesma retenção |
| Status | StatusPedido | |
| MidiaId | Guid? | FK ArquivoMidia `Aprovado` do tipo `Musica`, definida na aprovação |
| ModeradoPorUsuarioId | Guid? | |
| ModeradoEmUtc | DateTime? | |
| MotivoRejeicao | string(280)? | obrigatório se Rejeitado |
| EnviadoPlayoutEmUtc | DateTime? | quando o Liquidsoap retirou o pedido da fila |
| TocadoEmUtc | DateTime? | |
| CriadoEmUtc / RowVersion | | |

Máquina de estados:
```
Pendente ─(locutor: Aprovar[midiaId?])─► Aprovado ─(playout: FaixaIniciada | locutor: MarcarTocado)─► Tocado
   ├─(locutor: Rejeitar[motivo])─► Rejeitado
   └─(worker: CriadoEm < agora - 120 min)─► Expirado
```
Moderar um pedido que não está `Pendente` → erro de domínio `PedidoJaModerado` → **409**. Dois locutores clicando ao mesmo tempo: o `RowVersion` garante que só um vence.

### 2.5 `ArquivoMidia` (schema `midia`) — *MediaUpload*
| Campo | Tipo | Regra |
|---|---|---|
| Id | Guid | PK |
| NomeOriginal | string(255) | só exibição; **nunca** usado como caminho |
| Titulo / Artista | string(200)? | Titulo obrigatório para `Musica` |
| TipoMidia | TipoMidia | |
| Bucket | string(63) | `quarentena` até ser aprovado, depois `midia` |
| ChaveStorage | string(512) | `{yyyy}/{MM}/{Id}` (+ extensão após a recodificação); gerada pelo servidor |
| MimeTypeDeclarado | string(100) | o que o cliente disse (só auditoria) |
| **MimeType** | string(100)? | tipo **detectado** (magic bytes + ffprobe); obrigatório quando Aprovado |
| TamanhoBytes | long | declarado na criação e conferido no `concluir`; depois o tamanho final |
| **HashSHA256** | byte[32]? | da **saída recodificada** (determinística com `+bitexact`); único entre os `Aprovado` |
| HashOriginalSHA256 | byte[32]? | do arquivo **enviado**, antes da recodificação (auditoria, reenvio, inteligência de malware); não único |
| EtagUpload | string(64)? | ETag do objeto registrado no `concluir`; se mudar antes da análise → `Rejeitado` (TOCTOU, S-M02) |
| **DuracaoSegundos** | decimal(9,3)? | medida no **arquivo de saída** (S-A01); obrigatório quando Aprovado |
| **StatusSanitizacao** | StatusSanitizacao | |
| TentativasSanitizacao | byte | +1 a cada vez que entra em `EmAnalise`; na 3ª falha → `Rejeitado` ("falha de processamento") |
| EmAnaliseDesdeUtc | DateTime? | lease; obrigatório em `EmAnalise`; um lease vencido (> timeout do job) volta para `Pendente` |
| MotivoRejeicao | string(500)? | |
| EnviadoPorUsuarioId | Guid | FK Usuario |
| DataUploadUtc | DateTime | criação do registro |
| UploadExpiraEmUtc | DateTime | criação + 1 h (limpeza de `AguardandoUpload`) |
| SanitizadoEmUtc | DateTime? | |
| Ativo | bool | desativar tira da rotação sem apagar o histórico |
| RowVersion | byte[] | |

Limites por tipo (configuráveis em `Midia:Limites`):
| Tipo | Tamanho máx. | Duração |
|---|---|---|
| Vinheta | 10 MB | 1 s – 60 s |
| Comercial | 20 MB | 5 s – 120 s |
| Musica | 250 MB | 30 s – 20 min |

Máquina de estados:
```
AguardandoUpload ─(API: concluir, HEAD ok)─► Pendente ─(worker pega)─► EmAnalise ─┬─► Aprovado
      └─(worker: expirou)─► [registro e objeto removidos]                          ├─► Quarentena (antivírus)
                                                                                    └─► Rejeitado (tipo/tamanho/duração/duplicado)
```
Invariante: `Aprovado ⇒ MimeType, HashSHA256, DuracaoSegundos, SanitizadoEmUtc ≠ null`, reforçado também por CHECK no banco. Só mídia `Aprovado` e `Ativo` pode tocar ou ser ligada a um pedido.

### 2.6 `Divulgacao` (schema `interacao`) — *Announcement*
| Campo | Tipo | Regra |
|---|---|---|
| Id | Guid | PK |
| Titulo | string(120) | obrigatório |
| Mensagem | string(1000) | obrigatório; texto puro (o frontend nunca renderiza como HTML) |
| ImagemChaveStorage | string(512)? | objeto no bucket `publico` (substitui `ImagemUrl` do plano base; D11). A imagem é recodificada pela API antes de gravar (S-A05) |
| LinkDestino | string(2048)? | só `https://` |
| Ativo | bool | |
| Prioridade | byte | 0–100, maior aparece antes |
| InicioExibicaoUtc / FimExibicaoUtc | DateTime? | janela opcional; `Fim > Inicio` |
| CriadoPorUsuarioId | Guid | |
| CriadoEmUtc / AtualizadoEmUtc / RowVersion | | |

"Ativa para o público" = `Ativo && (Inicio == null || Inicio ≤ agora) && (Fim == null || agora < Fim)`.

### 2.7 `Reproducao` (schema `midia`), histórico de playout
`Id bigint identity`, `MidiaId`, `PedidoId?`, `ProgramaId?`, `IniciadoEmUtc`. Só inserção. É a base dos relatórios do Atlas ("mais tocadas no mês").

### 2.8 `EventoOutbox` (schema `infra`)
`Id bigint identity`, `Tipo varchar(100)`, `Payload nvarchar(max)` (JSON), `CriadoEmUtc`, `ProcessadoEmUtc?`, `Tentativas`, `UltimoErro?`. Gravado na **mesma transação** da mudança de estado. Depois de 10 tentativas o evento fica parado para análise (não entra mais na fila de envio).

## 3. Erros de domínio → HTTP

| Erro | HTTP | `type` (ProblemDetails) |
|---|---|---|
| Falha de validação (FluentValidation) | 400 | `/erros/validacao` (com `errors{campo:[msgs]}`) |
| Credenciais inválidas / usuário inativo / **bloqueado** / e-mail inexistente | 401 | `/erros/credenciais`: mesma mensagem e mesmo tempo de resposta (hash fictício) em todos os casos. **Não existe 423** (D21) |
| Token com `DeveTrocarSenha` em rota diferente da troca de senha | 403 | `/erros/troca-de-senha-obrigatoria` |
| Sem permissão | 403 | `/erros/proibido` |
| Não encontrado | 404 | `/erros/nao-encontrado` |
| `ConflitoDeGrade` | 409 | `/erros/conflito-grade` (+ `programaConflitanteId`) |
| `TransicaoInvalida` / `PedidoJaModerado` | 409 | `/erros/transicao-invalida` |
| `RowVersion` divergente (`If-Match`) | 412 | `/erros/versao-desatualizada` |
| Throttle de pedido | 429 | `/erros/muitos-pedidos` (+ `retryAfterSeconds`) |
