# Parecer E3-A01 e E3-A02 — planos dos pedidos de música e retenção (LGPD)

> Autor: Atlas (DBA) · tarefas E3-A01 e E3-A02 (`07-epico-3-tarefas.md`), parte do C1 · 2026-10-29
> Consultas medidas: `origin/forge/e3-c1-pedidos` @ `8b635c0` (PR #81): `ConsultasDePedido.cs`, `ExpiracaoDePedidos.cs`, `ConsultasDePedidos.cs`, `Manutencao.cs` e o índice `IX_PedidoMusica_Aprovados` (migration + `03`). A consulta 5 (`divulgacoes/ativas`) é do C2 e foi medida com o **texto provisório do `07`**; refazer quando o C2 definir o SQL.

## Veredito

**Aprovado com uma correção obrigatória: a expiração em lote (E3-F04, consulta 3) precisa de hint no `IX_PedidoMusica_Fila`.** O resto cumpre o critério sem mudança de schema além do índice que o `07` já previu.

| # | Consulta | Plano real | Leituras lógicas em `PedidoMusica` (A / B) |
|---|---|---|---|
| 1a | Fila `Status = 1`, página de 20 | varredura ordenada do `IX_PedidoMusica_Fila` (para no 20º) | 3 / 4 |
| 1b | `COUNT(*)` da fila (o `CountAsync` do handler) | varredura inteira do `IX_PedidoMusica_Fila` | 4 / **731** |
| 2 | Meus pedidos (dispositivo e usuário) | *Seek* em `IX_PedidoMusica_Device` / `_Usuario` + 1 *key lookup* por linha | 2 a 179 (o dispositivo mais ativo) |
| 3 | Expiração, 1 lote de 1.000 | **varredura do clustered** (`PK_PedidoMusica`) | **19.845** / **26.880** |
| 3 com hint | idem | *Seek* no `IX_PedidoMusica_Fila` | **3** / 12.449 |
| 4 | Fallback do throttle (dispositivo, usuário, IP) | *Seek* nos três índices de hash | 2 a 3 |
| 6 | Aprovados das últimas 24 h, página de 20 | *Seek* no `IX_PedidoMusica_Aprovados` | 5 / 4 (controle sem o índice: 19.844 / 20.053) |
| 7 | `COUNT` do teto sobre `TOP (300)` | varredura parada em 300 linhas | 4 / 16 |
| 9 | `ProgramaId` da janela | *Seek* no `IX_Programa_Grade` | 4 (`Programa`) |
| 5 | `divulgacoes/ativas` (provisório) | varredura do `IX_Divulgacao_Ativas` inteiro (18 linhas, 4 páginas) | 4 (`Divulgacao`) |

- **A = cenário realista** (40 `Pendente`); **B = acúmulo** (25.000 `Pendente` vencidos, Worker parado). A tabela tem **500.004 linhas** (155 MB o clustered).
- **Nenhum índice novo além do `IX_PedidoMusica_Aprovados`**, que já está no PR com a chave em `ModeradoEmUtc`, o `INCLUDE` e a migration iguais ao proposto, e no `03`. O índice candidato do A02 (`CriadoEmUtc` filtrado) **não vale**: seção 5.
- Quatro pontos para o Forge (seção 6), só o primeiro obrigatório.

## 1. Como foi medido

Igual ao A02 do Épico 2: contêiner descartável (`infra/perf/rodar-a01.sh <A|B>`, ≈ 45 s, remove o contêiner ao sair), imagem fixada do `docker-compose.yml`, `MSSQL_PID=Express`, collation `Latin1_General_100_CI_AI_SC`, RCSI ligado, compatibilidade 160, schema do `03` da branch do C1. Cenário frio: `CHECKPOINT` + `DROPCLEANBUFFERS` + `FREEPROCCACHE` antes de cada consulta, plano real (`SET STATISTICS XML`) e `STATISTICS IO`.

- **Dataset** (`infra/perf/gerar-dataset-a01.sql`, sobre o do A02, que dá os locutores e os programas): 500.000 pedidos em 365 dias (Rejeitado 25%, Tocado 20%, Aprovado 15%, Expirado 40%; o `Aprovado` é o pior caso, o que nunca é marcado como tocado: 71 a 75 mil linhas). Dispositivos com cauda longa (18 mil distintos; o mais ativo tem 325 pedidos no cenário A e 431 no B), 6 mil IPs e **um IP quente** com 888 pedidos em 7 dias no cenário A e 3.507 no B (o caso do `KnownIPNetworks` errado). 8% dos pedidos de um dos 2.000 ouvintes cadastrados. Retenção do E3-D8 **em regime**: hash de IP só nos últimos 8 dias, hash de dispositivo só nos últimos 31 dias, e antes disso `UsuarioId`/`Mensagem` nulos e `NomeOuvinte = '(anônimo)'`. Divulgações: 43 (A) e 10.043 (B, 10.000 inativas).
- **Texto das consultas**: o que o C1 manda. Fila, aprovados e meus pedidos são o SQL do EF no formato do `ToQueryString` (`Status = CAST(1 AS tinyint)`, constante; projeção só do `INCLUDE`). Teto, fallback, janela e expiração são o SQL explícito de `ConsultasDePedidos.cs` e `ExpiracaoDePedidos.cs`, com os parâmetros que o `ExecuteSql`/`SqlQuery` interpolado gera (`@p0`, `@p1`…).
- **Suposição aberta**: o `byte[]` do `SqlQuery` interpolado vai como `varbinary(8000)` (4a a 4g). O controle 4h manda `binary(32)`, o tipo da coluna: o mesmo plano e 3 leituras. **Seja qual for o tipo, não há conversão que quebre o *seek*.**
- Scripts: `infra/perf/medir-a01.sql` (consultas) e `infra/perf/medir-a01-retencao.sql` (E3-A02, grava por último), resumo por `infra/perf/resumir-plano.py`.

## 2. Consultas que passam

- **Fila (1a) e aprovados (6a)**: `Status` constante, e as colunas lidas são todas do `INCLUDE`; sem *key lookup*. O `IX_PedidoMusica_Aprovados` com chave `ModeradoEmUtc` faz *seek* de faixa: **5 leituras contra 19.844 do controle forçado ao clustered**. Funciona porque o `COUNT` e a lista usam o mesmo corte. Com 71 mil `Aprovado` acumulados o *seek* só lê as ~230 linhas das últimas 24 h.
- **Meus pedidos (2)**: um *seek* por caminho. O dispositivo mais ativo custa 174 a 179 leituras (3 do índice + os *key lookups* das 20 linhas, 3 a 4 páginas cada, porque a entidade inteira não está no índice). O plano compilado com um dispositivo desconhecido (2c) serve para o mais ativo (2f): sem sensibilidade a parâmetro. Uma projeção para o `PedidoDto` não reduz nada, porque as colunas continuam fora do índice.
- **Fallback do throttle (4)**: 2 a 3 leituras nos três caminhos, inclusive o IP quente com milhares de pedidos em 7 dias (4f: 3 linhas na janela de 5 min) e o dispositivo mais ativo sem pedido na janela (4c). `TOP (@p0)` do IP e o `TOP (1)` dos outros dois param no primeiro registro fora da janela.
- **Teto (7)**: o `TOP (@p0)` interno para a leitura em 300 linhas: **16 leituras no cenário B** (25.000 pendentes), contra 731 de um `COUNT(*)` sem `TOP`. Ver o ponto 3 da seção 6 sobre o `COUNT` da fila.
- **`ProgramaId` da janela (9)**: *seek* de faixa no `IX_Programa_Grade`, **4 leituras** com um programa em curso e 7 sem programa (o *seek* lê as 56 linhas da faixa de 12 h de `Programa`, como na Q3 do A02). Não há credencial no meio, é a mesma forma que o A02 aprovou.
- **Divulgações ativas (5, provisório)**: o `IX_Divulgacao_Ativas` cobre a consulta (4 páginas, 18 linhas, o resto das 10.025 inativas fica fora do índice filtrado), mas o plano é **varredura**, não *seek*: com a ordenação `Prioridade DESC, Id` e os predicados de janela residuais não há chave de busca, e com 18 linhas ativas isso é o certo. O índice filtrado só pareceria necessário com muitas divulgações ativas, e essa tabela não cresce assim.

## 3. Consulta 3 — expiração em lote (E3-F04): obrigatório

O `UPDATE TOP (@lote) ... WHERE Status = 1 AND CriadoEmUtc < DATEADD(MINUTE, -@expiracao, SYSUTCDATETIME())` do `ExpiracaoDePedidos.cs`, **compilado com os parâmetros que o Worker manda** (`@lote = 1000`, `@expiracao = 120`), escolhe **varredura do clustered** em vez do `IX_PedidoMusica_Fila`.

| | Leituras no `PedidoMusica` por lote | Observação |
|---|---|---|
| Cenário A (nada a expirar) | **19.845** (todo o clustered, 500.004 linhas) | cada ciclo de 60 s lê ~155 MB sem fazer nada |
| Cenário B (25.000 vencidos) | 26.880 por lote; 26 lotes, **6,3 a 9,5 s** | o `TOP` não tem `ORDER BY`: a varredura reinicia do começo do clustered a cada lote |
| Com hint no `IX_PedidoMusica_Fila` (A) | **3** | idem, sem trabalho |
| Com hint (B) | 12.449 por lote | os 1.000 *key lookups* no clustered e a manutenção do índice |

- Medido isoladamente por lote em B (1.000 linhas, `ROLLBACK`, parâmetros sniffados): sem hint 611 ms; `UPDATE TOP` com hint 91 ms; CTE com `ORDER BY CriadoEmUtc` sem hint 240 ms (**a CTE ordenada sozinha não resolve**, o plano continua varrendo o clustered com os parâmetros reais); CTE com `ORDER BY` e hint 59 ms. Em A: 295 ms sem hint contra 20 ms com hint.
- **Correção pedida**: o mesmo `UPDATE TOP`, com o hint na tabela:
  ```sql
  UPDATE TOP (@lote) p SET Status = 5
  OUTPUT inserted.Id, inserted.ListenerDeviceHash INTO @expirados
  FROM interacao.PedidoMusica AS p WITH (INDEX(IX_PedidoMusica_Fila))
  WHERE p.Status = 1 AND p.CriadoEmUtc < DATEADD(MINUTE, -@expiracao, SYSUTCDATETIME());
  ```
  O hint exige o predicado `Status = 1` literal (o texto já tem) e falha em tempo de execução se o índice for renomeado ou removido: o `[SqlFact]` do lote com mais de um lote cobre isso. Uma CTE `TOP (@lote) ... ORDER BY CriadoEmUtc` com o mesmo hint é equivalente (a mais rápida em B) e deixa a ordem determinística (mais velho primeiro); ficou como alternativa.
- **Locks do lote de 1.000** (cenário B): 1.000 chaves no `IX_PedidoMusica_Fila` e 1.000 no clustered, mais ~300 nos índices do outbox; só `IX` na tabela. Os 26 lotes do laço inteiro não promoveram nenhum lock (`index_lock_promotion_count` = 0) e gravaram ~32 MB de log. **O lote de 1.000 não escala para lock de tabela.**
- A parte do outbox do mesmo comando (INSERT de 1.000 eventos): 3.4 de custo, sem problema. Avisos de conversão só na estimativa do `CONVERT(varchar(36), Id)` da variável de tabela, como no A02.

## 4. E3-A02 — retenção do E3-D8

`Manutencao.cs` @ `8b635c0`. **Aprovado sem índice novo**: todas as etapas rodam uma vez por dia e custam décimos de segundo.

| Etapa | Regime (≈ 1.300 linhas por corte) | Acúmulo (10 dias parado, ≈ 13.000 linhas) |
|---|---|---|
| Hash do IP aos 7 dias | 1.312 linhas, 1 lote, 60 ms | 13.056 linhas, 4 lotes, 267 ms |
| Anonimização de 30 dias (`UPDATE` com `CASE`) | 1.314 linhas, 1 lote, 410 ms | 13.185 linhas, 4 lotes, 1.651 ms |
| Eventos de pedido do outbox (30 dias, entregues ou parados) | 500 linhas, 53 ms | 500 linhas, 56 ms |
| Limpeza antiga do outbox (7 dias) | 28 ms | 32 ms |
| **Locks / log** | 0 promoções, 3,7 MB | **0 promoções**, 16 MB |

- **Sem escalada de lock no lote de 4.000**, nem com acúmulo: em R2 o lote cheio segura 4.000 chaves no clustered, 4.000 no `IX_PedidoMusica_Device`, ~580 no `IX_PedidoMusica_Aprovados` e ~330 no `_Usuario`, e a tabela fica só com `IX`. O limite de ~5.000 vale por índice, não pela soma. **A margem é de 20%**: se alguém acrescentar uma quarta coluna indexada que o `UPDATE` toque, ou um segundo índice sobre `Device`, revisar o lote (2.000 dá folga larga a quase nenhum custo).
- **Plano**: as duas etapas do `PedidoMusica` varrem uma estrutura inteira (o `IX_PedidoMusica_Ip` filtrado, 342 páginas, para o IP; o clustered, 20.053 páginas, para os 30 dias). O "dia vazio" da anonimização (R6b, nada a fazer) lê as mesmas 20.053 páginas (~155 MB) uma vez por dia. É barato demais para justificar índice.
- **O índice candidato (`IX_PedidoMusica_Anonimizar`, `CriadoEmUtc` filtrado em `ListenerDeviceHash IS NOT NULL`) não ajuda.** Criado em banco de teste e medido: com o texto do Forge (braço `OR`) o otimizador não o usa (R5a, mesmo plano, 33.359 leituras); reescrevendo o `WHERE` só com o braço do hash do dispositivo (R5b), ele **continua preferindo a varredura do clustered** (37.365 leituras). Cada novo índice custa uma escrita extra em todo `INSERT` de pedido e em cada anonimização, para economizar décimos de segundo por dia. **Recomendação: não criar.**
- **O braço raro do `WHERE`** (terminal que só virou terminal depois dos 30 dias: `Pendente` que o Worker parado deixou passar, `Aprovado` moderado na véspera) faz o `UPDATE` casar linhas que já não têm hash; isso funciona e é idempotente (a segunda passada não encontra nada). Mantido como está.
- **Outbox**: `infra.EventoOutbox` não tem índice por `Tipo` nem por `CriadoEmUtc`, e o `DELETE` dos eventos de pedido varre o clustered (55.500 linhas, 2.784 páginas, 53 ms). O outbox é purgado a cada 7 dias para os entregues, então fica pequeno. Sem índice novo.
- **Segurança do `CASE`**: o `UPDATE` só troca `UsuarioId`, `Mensagem` e `NomeOuvinte` nos terminais (`Status IN (3, 4, 5)` ou `Aprovado` com `ModeradoEmUtc` há mais de 24 h) e os hashes em todos; `Pendente`/`Aprovado` recentes mantêm o texto. Conferido no dataset (R7, depois da rotina): zero hash de IP com mais de 7 dias, zero hash de dispositivo com mais de 30 dias, zero terminal com mais de 30 dias guardando texto ou usuário e zero pedido recente anonimizado por engano.

## 5. Índices: o que entra e o que não entra

- **Entra** (já no PR #81): `IX_PedidoMusica_Aprovados ON (ModeradoEmUtc) INCLUDE (NomeOuvinte, TituloMusica, Artista, Mensagem, ProgramaId, CriadoEmUtc) WHERE Status = 2`. Coberto pela migration `IndicePedidoAprovados`, pelo `HasIndex` nomeado e pelo `03`.
- **Não entra**: `IX_PedidoMusica_Anonimizar` (A02, seção 4).
- **C2** (não medido aqui): `UX_Divulgacao_ImagemChave` (único, filtrado em `ImagemChaveStorage IS NOT NULL`) é pequeno e entra no C2 com a migration.

## 6. Para o Forge

1. **Obrigatório — `ExpiracaoDePedidos.cs`**: o hint do `IX_PedidoMusica_Fila` (seção 3). Sem ele, cada ciclo de 60 s lê o clustered inteiro e o acúmulo de 25.000 leva 6 a 9 s em vez de ~2 s. Mantenha o teste com mais de um lote; ele pega um hint quebrado.
2. **`ListarPedidosHandler` — `CountAsync` da fila**: o `total` é um `COUNT(*)` sem limite e lê o índice inteiro em cada `GET /pedidos`: 4 leituras no cenário A, **731 no B**. É o cenário de exceção (o teto de 300 impede a fila de crescer assim), então não bloqueia. Se quiser blindar, `Math.Min` do total contra um teto ou um `COUNT` sobre `TOP (n)`; a lista de aprovados (11 leituras) não precisa.
3. **`Manutencao.cs`**: manter o lote de 4.000, mas anotar no código que a margem é de 20% do limite de ~5.000 locks por índice (seção 4).
4. **Confirmar o tipo do parâmetro `byte[]`** capturando o comando do `SqlQuery` interpolado (a suposição `varbinary(8000)` da seção 1). Já medi os dois tipos e dá o mesmo plano, então é só para o registro.

Reproduzir: `infra/perf/rodar-a01.sh <A|B> [pasta]` (`ATRASO=10` para o acúmulo de retenção; `SCHEMA=<03 da branch>` se o `03` da `main` ainda não tiver o `IX_PedidoMusica_Aprovados`).
