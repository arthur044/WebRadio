# Parecer E2-A02 — planos de execução da grade

> Autor: Atlas (DBA) · tarefa E2-A02 (`06-epico-2-tarefas.md`), parte do B2 · 2026-10-20
> Consultas medidas: `main` @ `192f4c0` (consultas 1 e 2) e `origin/forge/e2-b2-ao-vivo` @ `459f0de` (consulta 3, E2-F05).

## Veredito

**Aprovado, sem mudança de schema.** As três consultas fazem o que o critério de aceite pede, num banco com **222.895 programas** (5 anos de histórico):

| # | Consulta | Índice esperado | Plano real | Leituras lógicas (`Programa`) |
|---|---|---|---|---|
| 1 | Interseção da criação e da edição | `IX_Programa_Grade` | *Index Seek* | 3 a 6 (o índice tem 4.828 páginas) |
| 2 | `UPDATE` do Worker (`AoVivo` e `Finalizado`) | `IX_Programa_Worker` | *Index Seek* nos dois | ocioso: 3 + 27; com trabalho: 27 + 45 |
| 3 | Janela do `harbor/auth` | `IX_Programa_Grade` | *Index Seek* | 4 (no ar) a 8 (fora da janela) |

- **A consulta 3 não pede índice novo.** Não há migration, e o `03` não muda.
- Dois pontos para o Forge e o Nexus (nenhum bloqueia o B2): a premissa do `06` sobre a consulta 3 está meio errada (seção 4), e o segundo `UPDATE` do Worker lê o índice filtrado inteiro a cada ciclo (seção 3).

## 1. Como foi medido

- **Banco descartável**: contêiner novo, sem porta publicada, com a imagem fixada do `docker-compose.yml` (SQL Server 2022 RTM-CU27, 16.0.4295.3), `MSSQL_PID=Express` como na produção, collation `Latin1_General_100_CI_AI_SC`, RCSI ligado, compatibilidade 160. O schema é o `03` da branch do B2 (a `seg.CredencialTransmissao` ainda não está na `main`). Banco de nome `WebRadioPerf`, e os dois scripts SQL se recusam a rodar num banco sem `Perf` no nome.
- **Dataset** (`infra/perf/gerar-dataset-a02.sql`, fora do `seed-dev`): 201.243 `Finalizado`, 18.260 `Cancelado`, 3.391 `Agendado` (30 dias à frente) e 1 `AoVivo`, de 2021-10-20 a 2026-11-19.
  - **110 programas encadeados por dia** (5 a 13 min, intervalos que se encostam, sem sobreposição), **10 cancelados por dia** (5 min a 2 h, 5% até 12 h, podem se sobrepor) e **1 programa longo a cada 5 dias** (6 a 12 h, `Finalizado`). A maior duração é de 719 min, sob o `CK_Programa_DuracaoMax` (720).
  - Conferido por consulta: **0 pares `Agendado`/`AoVivo` sobrepostos**. 50 locutores, 2 admins, 1 credencial ativa e 2 revogadas por locutor.
  - O dataset é **mais denso que uma webradio real** (a faixa de 12 h tem 55 a 64 linhas, conforme o horário; uma grade real teria uma dezena). É o pior caso para as consultas com o limite de 12 h. As estatísticas são as do `UPDATE STATISTICS` padrão (amostra), e os índices foram criados na carga (fragmentação ~0%).
- **Tamanho das estruturas**: `PK_Programa` 9.264 páginas, `IX_Programa_Grade` 4.828, `IX_Programa_LocutorId` 1.456, `IX_Programa_Worker` 23 (3.392 linhas, filtrado).
- **Medição** (`infra/perf/medir-a02.sql`, resumo por `infra/perf/resumir-plano.py`): plano real (`SET STATISTICS XML`) e `SET STATISTICS IO`. Cada cenário frio roda depois de `CHECKPOINT` + `DBCC DROPCLEANBUFFERS` + `DBCC FREEPROCCACHE`, então o plano é compilado com os valores do próprio cenário. Os números variam em ±1 entre rodadas porque o dataset é aleatório.
- **Texto das consultas**: o que a aplicação manda. A consulta 1 é o SQL do EF Core 10 capturado com `ToQueryString`, e os tipos dos parâmetros foram conferidos no comando enviado (`datetime2(7)` contra colunas `datetime2(0)`, `tinyint`, `uniqueidentifier`). As consultas 1 a 3 e os dois `UPDATE` não têm aviso de conversão. O único aviso de todo o material é do `INSERT` no outbox (`CONVERT(varchar(36), p.Id)` sobre a variável de tabela): só afeta a estimativa de linhas. A consulta 2 é o `TransicoesDaGrade.Sql` com o enum interpolado. A consulta 3 é o `ConsultasDoHarbor.JanelaAsync` da branch, com `@p0` como o `SqlQuery` do EF.

Reproduzir: `SCHEMA=<03 do B2> infra/perf/rodar-a02.sh [pasta]` (≈ 30 s; remove o contêiner ao sair).

## 2. Consulta 1 — interseção da criação e da edição

Plano (igual para criação e edição; a edição só acrescenta `p.Id <> @ignorarId` ao predicado residual):

```
Top
└ Nested Loops
  ├ Merge Interval (Constant Scan ×2)          -- os dois limites da faixa vêm de parâmetros
  └ Index Seek IX_Programa_Grade               -- busca: InicioUtc > @limite AND InicioUtc < @fimUtc
      residual: FimUtc > @inicioUtc AND Status IN (1, 2) [AND Id <> @ignorarId]
```

| Cenário | Lógicas | Físicas (frio) | Linhas lidas na faixa | Resultado |
|---|---|---|---|---|
| 1a criação com conflito (amanhã 20:00–21:00) | 6 | 2 | 55 | 1 linha |
| 1b criação sem conflito, no histórico denso | 5 | 2 | 59 | 0 |
| 1c criação sem conflito, +90 dias (além do histograma) | 3 | 3 | 0 | 0 |
| 1d **edição** sem mudar a janela (exclui o próprio `Id`) | 6 | 2 | 56 | 0 (vizinhos encostam) |
| 1e edição com conflito | 6 | 2 | 55 | 1 |
| 1f/1g/1h plano compilado num cenário e reusado nos outros | 3 a 6 | | | seek em todos |

- **Cobertura total**: a consulta sai do `IX_Programa_Grade` sem *lookup* (`Id` é a chave do clustered, e `FimUtc`, `Status` estão no `INCLUDE`).
- **Sensibilidade a parâmetro**: nada. O plano compilado com a janela vazia de +90 dias (estimativa de 1 linha) e reusado com a janela densa lê as mesmas 55 a 64 linhas e faz o mesmo seek (3 a 6 leituras).
- **Controle negativo** (1i, não é código da aplicação): a mesma consulta **sem** `InicioUtc > @limite` lê **104.832 linhas e 2.268 páginas**. O limite de 12 h é o que segura o seek. Ele só vale enquanto o `CK_Programa_DuracaoMax` for 720 min: se a duração máxima subir, a constante `Programa.DuracaoMaxima` (que é de onde o código tira o limite) tem que subir junto, no mesmo commit.

## 3. Consulta 2 — `UPDATE` do Worker

Os dois `UPDATE` usam `IX_Programa_Worker` (*Index Seek*), e o plano de escrita atualiza `PK_Programa`, `IX_Programa_Grade` (a coluna `Status` está no `INCLUDE`) e o índice filtrado:

```
UPDATE 1 (Agendado → AoVivo)
  Index Seek IX_Programa_Worker   -- busca: Status = 1 AND InicioUtc ≤ @agora; residual FimUtc > @agora
UPDATE 2 (→ Finalizado)
  Index Seek IX_Programa_Worker   -- busca: Status = 1 e Status = 2 (duas buscas por prefixo); residual FimUtc ≤ @agora
```

| Cenário | `Programa` (lógicas) | Outras tabelas | Observação |
|---|---|---|---|
| 2a com trabalho (1 → `AoVivo`, 1 → `Finalizado`) | 27 + 45 | `EventoOutbox` 10, `Usuario` 4 | 2 eventos no outbox |
| **2b ocioso** (o caso de ~99,9% dos ciclos de 10 s) | **3 + 27** | 0 | 0 linhas, 0 eventos: idempotente |
| 2c backlog de 500 vencidos (Worker parado e de volta) | 8 + 8.951 | `EventoOutbox` 1.180 | uma vez só: ~18 leituras por linha em 3 estruturas |

**Observação (não pede ação).** O primeiro `UPDATE` busca por `(Status = 1, InicioUtc ≤ @agora)` e lê só o que venceu. O segundo, com `Status IN (1, 2) AND FimUtc <= @agora`, também é *seek*, mas só pelo prefixo `Status`: `FimUtc` entra como residual, então ele **lê o índice filtrado inteiro** (3.392 linhas, 27 a 32 páginas) em todo ciclo. O custo cresce com o número de `Agendado`/`AoVivo` à frente, e não com o histórico: ~1 página a cada 147 programas futuros. Medi a variante com `AND InicioUtc < @agora` (redundante pelo `CK_Programa_Intervalo`) e **o plano não muda** (cenário 2e: 32 leituras, `InicioUtc` continua residual), então não vale mexer no SQL. Com 30 dias de grade a 110 por dia são 27 leituras a cada 10 s; numa grade real, bem menos. Reavaliar só se a grade futura passar de algumas dezenas de milhares de programas.

A estimativa do otimizador para esses `UPDATE` é de 270 a 500 linhas (o `@agora` é variável local, então ele usa a heurística de 30%), contra 0 a 1 real. Isso só infla o custo estimado e escolhe o plano de escrita largo (*Split* + *Table Spool*), que fica a custo zero quando não há linhas. Nenhuma ação.

## 4. Consulta 3 — janela do `harbor/auth`

```
Top
└ Nested Loops
  ├ Nested Loops
  │ ├ Nested Loops
  │ │ ├ Index Seek UX_CredencialTransmissao_Identificador   -- Identificador = @p0
  │ │ └ Clustered Index Seek PK_CredencialTransmissao        -- residual RevogadaEmUtc IS NULL
  │ └ Nested Loops
  │   ├ Merge Interval
  │   └ Index Seek IX_Programa_Grade   -- busca: InicioUtc > agora-12h AND InicioUtc ≤ agora
  │       residual: FimUtc > agora AND Status IN (1, 2); o LocutorId = c.UsuarioId vai no join
  └ Index Seek IX_Usuario_Role         -- Role IN (1, 2), residual Ativo = 1
```

| Cenário | `Programa` | `CredencialTransmissao` | `Usuario` |
|---|---|---|---|
| 3a credencial do locutor no ar (devolve a linha) | 4 | 4 | 4 |
| 3b locutor sem programa na janela (0 linhas; lê as 57 da faixa) | 8 | 4 | 0 |
| 3c credencial revogada | não acessa | 4 | não acessa |
| 3d identificador inexistente | não acessa | 2 | não acessa |
| 3e plano compilado em 3d, reusado em 3a/3b | 4 / 8 | | seek igual |

O trabalho no `Programa` é a faixa de 12 h (no máximo ~60 linhas aqui), e os caminhos de recusa por credencial (revogada, inexistente) nem chegam nele. A estimativa da faixa (~60 linhas) bate com o que o seek lê (57), apesar de os limites virem de `SYSUTCDATETIME()`.

**Correção de premissa no `06`.** O texto da E2-A02 diz que o filtro `Status NOT IN (3, 4)` "por isso não usa o `IX_Programa_Worker`". Não é bem assim. Com o `CK_Programa_Status` (`Status IN (1, 2, 3, 4)`) confiável, o otimizador reescreve `NOT IN (3, 4)` para `IN (1, 2)`: é o que se vê no residual do plano (`Status = 1 OR Status = 2`), e esse predicado casa com o filtro do `IX_Programa_Worker`. Ele **pode** usar o índice filtrado, e usa quando o limite de 12 h sai (controle 3f: lê o índice filtrado inteiro, 26 a 29 páginas). Com o limite, escolhe o `IX_Programa_Grade` (4 a 8 páginas) por custo. As duas opções são limitadas e baratas, então **o critério (seek no `IX_Programa_Grade`) é cumprido, mas por custo, não por impossibilidade**: numa grade pequena o otimizador pode preferir o filtrado, e tudo bem. Peço ao Nexus ou ao Lore para ajustar o texto do `06`.

## 5. Vizinhas (informativo, fora das três pedidas)

Mesmas tabelas, medidas porque são rotas quentes da E2-F02:

- `GET /programas/ao-vivo`: 8 leituras no `Programa` (+2 no `Usuario`). Seek no `IX_Programa_Worker` (`Status = 2`) + *lookup* de 1 linha no clustered. Sem ação.
- `GET /programas` (semana atual, sem cancelados): seek no `IX_Programa_Grade` (23 leituras), mas depois 1 *lookup* no clustered e 1 seek no `Usuario` **por linha** (`Descricao`, `RowVersion` e `CriadoEmUtc` não estão no índice): 770 linhas = 3.516 + 1.540 leituras (~6,6 por linha). Cresce linear com o número de programas da semana, e a seek não é o problema. Não vale pôr `Descricao` (`nvarchar(1000)`) no `INCLUDE`. Sem ação; se a leitura pública da grade virar gargalo, o remédio é cache de resposta/`ETag`, não índice.

## 6. O que entra no B2

- `infra/perf/` (4 arquivos: gerador, medição, resumo, runner) e este parecer.
- Nenhuma migration, nenhuma mudança no `03`.
- Pendências que não são minhas: o ajuste de texto do `06` (seção 4) e, no B2, `03` §3.1 + migration iguais no mesmo commit (E2-A01, já combinado).
