# Épico 2 — Motor de transmissão, grade e ao vivo: pacote de tarefas

> Autor: Nexus · 2026-10-15 · Referências: `00-arquitetura.md` (Fluxos A e D), `01-dominio.md` (§2.3 `Programa`), `02-api-rest.md` (§4, §9, §10), `03-schema-sqlserver.sql`, `05-seguranca-auditoria.md` (S-A08, S-A09).
> Donos: **Forge** (backend, Worker, Liquidsoap), **Atlas** (banco), **Prism** (frontend), **Sentinel** (revisão de segurança do ao vivo).
> Escopo decidido pelo dono do produto em 2026-10-15: **inclui o locutor ao vivo**, com credencial de transmissão própria (identificador opaco + segredo aleatório guardado só como hash com salt).
> O `05` continua **normativo**: cada achado que cita uma tarefa `E2-*` vira critério de aceite dela. A checklist da §8 do `05` é o quality gate de todo PR.

## O que o Épico 2 entrega

1. **Grade de programas.** O Admin cria, edita e cancela programas sem sobreposição. O Worker muda o status sozinho (`Agendado → AoVivo → Finalizado`) e o selo "Ao Vivo" troca na tela sem refresh.
2. **Locutor ao vivo.** Só o locutor dono do programa `AoVivo` consegue conectar o source client (BUTT, Mixxx) no harbor, e a conexão cai quando o programa termina ou é encerrado.
3. **Estado da rádio em tempo real.** "Tocando agora" e "transmissão ao vivo no ar" chegam a todos pelo SignalR.
4. **Telas.** Admin gerencia a grade; locutor tem um painel com a credencial de transmissão, o estado "no ar" e o botão de encerrar; o público vê a grade da semana, o selo Ao Vivo e o que está tocando.

## Fora do Épico 2

- **Fila de pedidos** (`fila_api` com pedidos aprovados): Épico 3. O `GET /internal/playout/proxima` continua respondendo **204**.
- **Biblioteca de mídia, upload e sanitização**: Épico 4. A rotação continua com a **playlist local** montada em volume, e **nada é gravado em `midia.Reproducao`** (a tabela exige `MidiaId`, e a playlist local não tem). O "tocando agora" segue pelos metadados, como no Épico 1.
- **Grade recorrente**: fora da v1 (`00 §6`).

## Definição de pronto do Épico

1. No `compose-smoke`: um programa criado pela API para "agora" vira `AoVivo` em até 20 s; um teste Playwright vê o selo Ao Vivo aparecer **sem recarregar a página**.
2. Criar um programa que se sobrepõe a outro responde **409** com `programaConflitanteId`; dois `POST` concorrentes para o mesmo horário resultam em **um** 201 e **um** 409 (teste com SQL real).
3. Com a credencial do locutor dono do programa `AoVivo`, um source client de teste conecta no harbor e o stream passa a tocar o ao vivo. Com credencial errada, revogada, de outro locutor ou fora do horário do programa, a conexão é **recusada** (teste no `teste.sh` do Liquidsoap e no smoke).
4. Ao encerrar o programa (`POST /programas/{id}/encerrar`) ou quando `FimUtc` passa, o ao vivo é **derrubado em até 30 s** e o stream volta para a rotação **sem cair** (o `curl` do stream não perde a conexão).
5. O segredo da credencial de transmissão **nunca** é gravado em claro, nunca aparece em log e só é mostrado uma vez, na geração.
6. Os eventos `ProgramaStatusAlterado` e `TransmissaoAoVivoAlterada` chegam ao frontend pelo hub, e o cliente se ressincroniza por REST ao reconectar.
7. Tudo que já era exigido no Épico 1 continua verde (CI completo, cobertura ≥ 80% em Domain e Application).

## Contratos novos e alterados

Esta seção é o contrato. O `02-api-rest.md` é atualizado no mesmo PR desta spec; o DDL entra no `03` **junto com a migration**, no PR B2 (o job `ddl-check` compara o `03` com as migrations e falharia antes disso).

### Credencial de transmissão (S-A08, decisão de 2026-10-15)

- **Identificador** (o "usuário" que o locutor digita no source client): um GUID gerado pelo servidor, **opaco**. Não é o e-mail, não é o nome e não é o `Id` do usuário: quem interceptar a conexão do harbor não descobre de quem é a conta.
- **Segredo** (a "senha" do source client): 32 bytes do `RandomNumberGenerator`, em base64url sem padding (43 caracteres, só `[A-Za-z0-9_-]`). Um GUID **não** serve como segredo: tem só 122 bits aleatórios e não vem de gerador criptográfico em toda implementação.
- **Armazenamento**: só `SHA-256(salt ‖ segredo)`, com salt de 16 bytes aleatório por credencial. Como o segredo tem 256 bits de entropia, um hash rápido basta (não há dicionário possível) e mantém a autenticação do harbor em milissegundos. Comparação com `CryptographicOperations.FixedTimeEquals`.
- **Ciclo de vida**: uma credencial ativa por locutor. Gerar uma nova **revoga** a anterior. O segredo aparece **uma única vez**, na resposta da geração; depois disso só o identificador e a data são consultáveis.
- **Não é o login do site.** A senha da conta nunca trafega pelo harbor, que não tem TLS (o protocolo source manda Basic auth em claro). Vazar a credencial de transmissão permite, no máximo, transmitir no horário do próprio programa daquele locutor, e o locutor ou o Admin a revoga na hora.

DDL proposto (entra no `03` e na migration no B2; revisão do Atlas):

```sql
/* ---------- 3.1 seg.CredencialTransmissao (Épico 2, S-A08) ---------- */
CREATE TABLE seg.CredencialTransmissao (
    Id               uniqueidentifier NOT NULL CONSTRAINT DF_CredencialTransmissao_Id DEFAULT NEWSEQUENTIALID(),
    UsuarioId        uniqueidentifier NOT NULL,
    Identificador    uniqueidentifier NOT NULL,          -- o "usuário" do source client; opaco
    SegredoHash      binary(32)       NOT NULL,          -- SHA-256(salt || segredo)
    Salt             binary(16)       NOT NULL,
    CriadaEmUtc      datetime2(3)     NOT NULL CONSTRAINT DF_CredencialTransmissao_CriadaEmUtc DEFAULT SYSUTCDATETIME(),
    RevogadaEmUtc    datetime2(3)     NULL,
    UltimoUsoEmUtc   datetime2(3)     NULL,
    CONSTRAINT PK_CredencialTransmissao PRIMARY KEY CLUSTERED (Id),
    CONSTRAINT FK_CredencialTransmissao_Usuario FOREIGN KEY (UsuarioId) REFERENCES seg.Usuario (Id) ON DELETE CASCADE
);
CREATE UNIQUE INDEX UX_CredencialTransmissao_Identificador ON seg.CredencialTransmissao (Identificador);
-- Uma credencial ativa por locutor.
CREATE UNIQUE INDEX UX_CredencialTransmissao_Ativa ON seg.CredencialTransmissao (UsuarioId) WHERE RevogadaEmUtc IS NULL;
```

Revogadas são apagadas pela manutenção do Worker depois de 90 dias (seção 11 do `03`, no B2).

### REST pública (acrescenta ao `02 §4`)

| Método | Rota | Quem | Descrição |
|---|---|---|---|
| GET | `/locutores/me/credencial-transmissao` | Locutor, Admin | **200** `{ identificador, criadaEmUtc, ultimoUsoEmUtc }` ou **204** se não houver ativa. Nunca devolve o segredo |
| POST | `/locutores/me/credencial-transmissao` | Locutor, Admin | Gera uma nova e revoga a anterior → **201** `{ identificador, segredo, criadaEmUtc }`. **Única** resposta que contém o segredo; `Cache-Control: no-store` |
| DELETE | `/locutores/me/credencial-transmissao` | Locutor, Admin | Revoga a ativa → **204** |
| DELETE | `/usuarios/{id}/credencial-transmissao` | Admin | Revoga a de outro usuário → **204** |

Desativar o usuário, rebaixar o papel para `Ouvinte` ou trocar a senha da conta **revoga** a credencial de transmissão (mesmo gatilho do `UsuarioSessoesRevogadas`).

Os endpoints de `/programas` já estão no `02 §4` e não mudam.

### Endpoints internos (substitui a linha reservada do `02 §9`)

Todos na `:8081`, com o Bearer `Playout__Token` e o filtro de porta do S-A06, como os do Épico 1.

| Método | Rota | Descrição |
|---|---|---|
| POST | `/internal/harbor/auth` | `{ usuario, senha, enderecoIp }` → **200** se `usuario` é o identificador de uma credencial ativa, o hash confere, o dono é `Locutor`/`Admin` ativo **e** é o locutor do programa `AoVivo` agora; senão **401** sem corpo e sem dizer qual condição falhou. Throttle por identificador e por IP (10 tentativas/5 min, `IRequestThrottle`). Grava `UltimoUsoEmUtc` no sucesso. Log: identificador, IP, resultado; **nunca** o segredo |
| GET | `/internal/harbor/autorizacao/{identificador}` | **200** enquanto a condição do `auth` continuar valendo (credencial ativa e programa do dono ainda `AoVivo`); **403** quando deixar de valer. É o que o Liquidsoap consulta para derrubar o ao vivo (ver o *spike* E2-F07) |
| POST | `/internal/harbor/eventos` | `{ tipo: "Conectado" \| "Desconectado", identificador, ocorridoEmUtc }` → **202**. Publica `TransmissaoAoVivoAlterada` |

### SignalR (acrescenta ao `02 §10`)

| Evento | Destino | Payload |
|---|---|---|
| `TransmissaoAoVivoAlterada` | todos | `{ noAr: bool, programaId?, locutorNome? }` |

Estado: o cliente guarda o último `eventoId` aplicado e, ao reconectar, ressincroniza por `GET /stream/info`, que ganha o campo `aoVivo: { noAr, programaId?, locutorNome? }`.

## Tarefas — Forge (Backend, Worker e Liquidsoap)

| ID | Tarefa | Depende | Critério de aceite |
|---|---|---|---|
| **E2-F01** | **Comandos e consultas de `Programa`**: criar, editar (só `Agendado`, com `If-Match`), cancelar, encerrar (locutor dono ou Admin, só `AoVivo`, `FimUtc = agora`), listar por intervalo (máx. 31 dias), `ao-vivo` e por id com `ETag`. Criar e editar passam pelo `sp_getapplock 'grade:programa' Exclusive` + consulta de interseção do Fluxo A, com o limite de 12 h. Validações do `01 §2.3` (duração 5 min–12 h, início não antes de `agora − 1 min`, locutor com papel `Locutor`/`Admin`) | — | TDD no Domain e na Application; **dois POST concorrentes no mesmo horário → um 201 e um 409** com SQL real (`[SqlFact]`); intervalos encostados (`[a, b)` e `[b, c)`) **não** conflitam; 409 traz `programaConflitanteId` |
| **E2-F02** | **Endpoints `/programas`** conforme o `02 §4` e a matriz RBAC do `02 §1`; `ConflitoDeGradeException` → 409 `/erros/conflito-grade` pelo `TratadorDeExcecoes` | F01 | Testes de integração para cada rota e papel (ouvinte anônimo só lê; Locutor só encerra o próprio; Admin tudo); `PUT` sem `If-Match` → 428, com `ETag` velho → 412 |
| **E2-F03** | **Transições de grade no Worker** (só o líder, ciclo de 10 s): o SQL do Fluxo A, passo 2, com `@agora = SYSUTCDATETIME()` (nunca o `IClock`), e o `ProgramaStatusAlterado` no outbox com o envelope `{ grupos: null, dados }` | F01 | Idempotente: rodar duas vezes não gera evento repetido; programa criado para "agora" vira `AoVivo` no próximo ciclo; `FimUtc` passado vira `Finalizado`; teste com SQL real |
| **E2-F04** | **Credencial de transmissão**: entidade, migration (o DDL da seção de contratos, também no `03`), endpoints `/locutores/me/credencial-transmissao` e `/usuarios/{id}/credencial-transmissao`, geração e hash conforme o contrato, revogação nos gatilhos de conta | A01 | O segredo não aparece em nenhum log nem em nenhuma resposta além do `POST` (teste que varre os logs da requisição); gerar uma nova revoga a anterior; índice único garante uma ativa por usuário; resposta com `Cache-Control: no-store` |
| **E2-F05** | **`POST /internal/harbor/auth`** e **`GET /internal/harbor/autorizacao/{identificador}`** conforme o contrato | F01, F04 | Testes para cada motivo de recusa (credencial inexistente, revogada, segredo errado, dono desativado, dono sem programa `AoVivo`, programa de outro locutor) → todos **401** idênticos; comparação em tempo constante; throttle → 429 depois do limite; só na `:8081` (o teste de bypass do S-A06 cobre as novas rotas) |
| **E2-F06** | **`POST /internal/harbor/eventos`** + evento `TransmissaoAoVivoAlterada` pelo outbox + campo `aoVivo` em `GET /stream/info` | F05 | Conectar → `noAr: true` com `programaId`; desconectar → `noAr: false`; evento repetido não muda o estado; metadados validados como no S-A09 |
| **E2-F07** | ***Spike* (primeira tarefa do B2): derrubar o harbor pelo Liquidsoap.** A API não empurra comandos ao Liquidsoap (D2). Provar no `teste.sh`, com a imagem fixada do Liquidsoap, que: (a) o `auth` do `input.harbor` chama `POST /internal/harbor/auth` e respeita o 200/401; (b) uma tarefa periódica no `radio.liq` (`thread.run`, a cada 10 s) consulta `GET /internal/harbor/autorizacao/{identificador}` e, no 403, **desconecta o source client** e volta para a rotação sem derrubar o `output.icecast`; (c) API fora do ar no meio do ao vivo **não** derruba o locutor (falha aberta só para quem já foi autorizado, por no máximo o fim do programa + 1 min) | F05 | Relatório no PR com o que funcionou na versão fixada. **Se (b) não for possível**, o Forge para e avisa o Nexus antes de seguir: a alternativa (canal de controle do Liquidsoap, ou proxy de source na frente do harbor) é decisão de arquitetura |
| **E2-F08** | **`radio.liq` do ao vivo**: `input.harbor` com o `auth` da F07, sem a senha única `liquidsoap_harbor_password` (o segredo sai do `gerar-segredos` e do compose); `on_connect`/`on_disconnect` chamando `POST /internal/harbor/eventos`; metadados do source client validados (200 caracteres, sem controle) antes do `TocandoAgora`; log de conexão com identificador e IP, nunca o segredo | F06, F07 | `teste.sh` cobre: credencial certa no horário → toca o ao vivo; errada → recusa; programa encerrado → derruba em ≤ 30 s e volta para a rotação; o stream de saída não cai em nenhum dos casos |
| **E2-F09** | **Manutenção**: apagar credenciais revogadas há mais de 90 dias, no job diário do Worker, em lotes | F04 | Teste com SQL real; seção 11 do `03` atualizada |
| **E2-F10** | **Smoke e CI**: o `smoke-compose.sh` cobre os itens 1, 3 e 4 da Definição de pronto (programa vira `AoVivo`; source client de teste com `ffmpeg` conecta e é derrubado no encerramento) | F08 | Job `compose-smoke` verde com os itens novos |

## Tarefas — Atlas (Banco de dados)

| ID | Tarefa | Depende | Critério de aceite |
|---|---|---|---|
| **E2-A01** | Revisar o DDL de `seg.CredencialTransmissao` (tipos, índice único filtrado, `ON DELETE CASCADE`, collation) antes da migration | — | Parecer no PR desta spec ou no B2; o `03` e a migration ficam iguais (o `ddl-check` confere) |
| **E2-A02** | Plano de execução da consulta de sobreposição (com `sp_getapplock` e o limite de 12 h) e do `UPDATE` das transições do Worker sobre `IX_Programa_Grade` e `IX_Programa_Worker` | E2-F01, E2-F03 | *Seek* nos dois índices com a grade de exemplo do `seed-dev.sql` ampliada para 1 ano; parecer no PR B1 |
| **E2-A03** | Seed de desenvolvimento com locutores e uma semana de grade (o `SEED_DEV` hoje está desligado, porque depende das tabelas do migrator e dos locutores do `DevDataSeeder`) | E2-F01 | Com `SEED_DEV=true`, o stack de dev sobe com grade de exemplo; nunca roda em produção |

## Tarefas — Prism (Frontend)

| ID | Tarefa | Depende | Critério de aceite |
|---|---|---|---|
| **E2-P01** | **Grade pública**: semana atual em horário de Brasília (dados em UTC), selo "Ao Vivo" no programa corrente, navegação entre semanas; atualiza por `ProgramaStatusAlterado` sem refresh | E2-F02 | Vitest nos cálculos de fuso; Playwright: o selo aparece sem recarregar quando o Worker muda o status |
| **E2-P02** | **Administração da grade** (Admin): criar, editar, cancelar; o 409 mostra qual programa conflita; `If-Match` com o `ETag` | E2-F02 | Erros da API mapeados para mensagens úteis; teste do fluxo de conflito |
| **E2-P03** | **Painel do locutor**: credencial de transmissão (gerar, ver o segredo **uma vez** com botão de copiar e aviso de que não será mostrado de novo, revogar), instruções para BUTT/Mixxx com servidor, porta, mount e identificador; estado "no ar" por `TransmissaoAoVivoAlterada`; botão "Encerrar programa" | E2-F04, E2-F06 | O segredo não fica em `localStorage`, `sessionStorage` nem no cache do TanStack Query depois de fechar o diálogo; teste que confere |
| **E2-P04** | **Player e "tocando agora"**: metadados do `TocandoAgora` e do `TransmissaoAoVivoAlterada` na barra do player; reconexão do áudio quando o stream volta | E2-F06 | O player continua sem interromper entre páginas (Playwright do Épico 1 segue verde) |

## Agrupamento de entrega

Por decisão do dono do produto, a entrega é por **feature grande**: um PR por linha, commits separados por tarefa, revisão no fim (Guardian em todos; Sentinel no B2 e no B3; Atlas no B1 e no B2).

| PR | Feature | Tarefas | Depende de |
|---|---|---|---|
| **E1-Z** | Fechamento do Épico 1 | Playwright no `compose-smoke` (DoD 2 do `04`), limite de cobertura ≥ 80% no CI (DoD 6), B3-13 | — |
| **B1** | Grade | F01, F02, F03, A02, A03 | E1-Z |
| **B2** | Ao vivo | **F07 primeiro** (*spike*), depois F04, F05, F06, F08, F09, F10, A01 | B1 |
| **B3** | Telas | P01, P02, P03, P04 | contratos do B1 (P01, P02); B2 (P03, P04) |

O B3 pode começar pelo P01 e pelo P02 assim que o B1 estiver mergeado, em paralelo com o B2.

## Riscos

| Nível | Risco | Mitigação |
|---|---|---|
| ALTO | O Liquidsoap não conseguir derrubar o source client por uma tarefa periódica | *Spike* E2-F07 antes de qualquer outra tarefa do B2, com critério de parada explícito |
| ALTO | Credencial de transmissão interceptada (o harbor não tem TLS) | Credencial separada do login, revogável, que só vale no horário do programa do próprio locutor; harbor publicado só com lista de IPs ou VPN em produção (S-A08). TLS no harbor fica como avaliação do Sentinel no B2 |
| MÉDIO | Força bruta no `harbor/auth` | Segredo de 256 bits + throttle por identificador e por IP + 401 idêntico para todo motivo |
| MÉDIO | API fora do ar derruba o locutor no meio do programa | Falha aberta só para quem já foi autorizado e só até o fim do programa + 1 min (F07, item c) |
| BAIXO | Relógio divergente na transição | Só o `SYSUTCDATETIME()` do SQL vale na transição (`00 §5`) |
