# Parecer E4-A01b — mídia com o dataset final e o código real

> Autor: Atlas (DBA) · tarefa E4-A01b (`08-epico-4-tarefas.md`, E4-D4 e E4-D5, DoD 9) · 2026-11-23
> Fecha o parecer do A01 (a primeira metade é o `e4-a01a-consultas-playout.md`). Entrega: este texto, um índice (migration `IndiceLimitesDeUploadPorUsuario`, `03` e EF), os scripts `infra/perf/gerar-dataset-e4b.sql`, `medir-e4-a01b.sql`, `medir-e4-a01b-k.sql`, `simular-rotacao-e4b.sql`, `indices-propostos-e4-a01b.sql` e `rodar-e4-a01b.sh`.

## Veredito

**Pronto, com uma decisão para o Forge e uma para o Nexus.**

1. **Sorteio da rotação (E4-D4): trocar para "uma entre as K = 5 primeiras candidatas a partir de `@r`".** A chave fixa, medida no código real e com a janela de repetição ativa, passa de 3× a média a partir de 300 faixas e chega a 12,8× em 50 mil (seção 5). Com K = 5 o pior caso fica em 2,1× (300), 2,6× (1.000) e 3,6× (50 mil), e a chamada completa da `proxima` custa 53 a 58 leituras lógicas numa janela realista (63 a 68 na do dataset do `08`): poucas leituras acima do critério de 50 (~1 ms; o `ORDER BY NEWID()` lia 786). K = 10 daria 2,7× em 50 mil, mas 93 a 123 leituras. O texto das consultas está na seção 5; a mudança é do Forge, no `FilaDoPlayout.cs`.
2. **Limites do S-M06 pelo SQL: um índice novo** (`IX_ArquivoMidia_EnviadoPor` passa a `(EnviadoPorUsuarioId, DataUploadUtc) INCLUDE (StatusSanitizacao, TamanhoBytes, UploadExpiraEmUtc)`). Sem ele, cada `POST /midias/uploads` lia a tabela inteira (1.116 a 2.375 leituras, de qualquer usuário); com ele, 3 a 12 leituras para um usuário comum e **155 para o Admin com 10.000 envios** (o S1 lê todas as linhas do usuário; seção 4 tem o gatilho). Migration e `03` neste PR; o `ddl-check` passa (228 itens).
3. **Biblioteca (`GET /midias`), captura da sanitização e `proxima`: nada a mudar.** A biblioteca custa ~2.400 leituras por página com 50 mil mídias (45 a 96 ms de CPU), e a busca por texto ~2.400 leituras (80 a 190 ms): aceito, com o gatilho de revisão da seção 3. Um índice para a listagem foi testado e **rejeitado** (seção 3).
4. **DoD 9 (`rel.usp_MaisTocadas`)** repetido com o dataset final: igual ao parecer do A02 (seção 6).

Critério do `08` ("rotação abaixo de 50 leituras lógicas por chamada no código real"): **cumprido com a chave fixa (K = 1): 21 a 23 leituras.** Com K = 5, que recomendo, fica em **53 a 58 (janela realista) e 63 a 68 (janela do dataset)**: passa do 50 por poucas leituras, porque o custo é ~8 a 10 leituras por candidata. Peço ao Nexus que aceite (a intenção do critério, não varrer a biblioteca, está atendida) ou fixe K = 3 (37 a 43 leituras por chamada, mas 5,2× de viés em 50 mil; K = 4: 45 a 53 leituras e 4,4×).

## 1. Como medi

Contêiner descartável do SQL Server 2022 Express (imagem fixada do `docker-compose.yml`), schema do `03`, `gerar-dataset-e4.sql` (50.000 mídias: 80% Música, 15% Vinheta, 5% Comercial, 5% desativadas, 2% descartadas; 2.000.000 de `Reproducao` em 12 meses; 105.000 pedidos) e o complemento `gerar-dataset-e4b.sql`, que dá ao dataset o que o A01a não tinha: `DataUploadUtc` espalhada em 12 meses (antes todas as linhas tinham o mesmo instante), `seg.Usuario`, um Admin com 10.000 envios (15 na última hora), 40 locutores com ~1.000 cada, um locutor no limite de 5 abertos, 30 mídias `Pendente` e os 2% descartados como `Rejeitado` (6), como o `08` pede.

O texto das consultas é o do **código real**, não uma tradução minha:
- captura, pedido e as três buscas da rotação: copiados de `CapturaDeMidia.cs` e `FilaDoPlayout.cs`;
- biblioteca: o SQL que o EF Core 10 gera para o mesmo LINQ do `ListarMidiasHandler` (`ToQueryString()`, com o join em `seg.Usuario` e as 25 colunas da página);
- limites do S-M06: o SQL do `IniciarUploadHandler` (abertos, 20/h, bytes/h, espera), pelo mesmo caminho.

Cada consulta roda por `sp_executesql` com os tipos de parâmetro do código, como o EF faz: o plano nasce com o parâmetro "farejado" (isso importa na seção 3). Custo = `STATISTICS IO` (leituras lógicas) e `TIME`.

## 2. Captura da sanitização (`CapturaDeMidia`)

| Cenário | Leituras lógicas | CPU / tempo |
|---|---|---|
| fila com 30 pendentes (o dia a dia) | 15 a 18 | < 5 ms |
| fila vazia (a sondagem do Worker) | 2 a 3 | < 5 ms |
| rajada de 1.000 pendentes (importação em massa) | 15 a 18 | < 5 ms |

Usa `IX_ArquivoMidia_FilaSanitizacao` (filtrado `StatusSanitizacao IN (1, 2, 3)`, 4 páginas com 36 linhas), e custa o mesmo com 30 ou com 1.000 na fila: o `TOP (1) ... ORDER BY DataUploadUtc` é um *seek* no início do índice. **Atenção para o Forge:** o índice só é usado porque a consulta escreve `StatusSanitizacao = 2` como **constante**. Com o valor em parâmetro, o SQL Server não casa o filtro do índice e varre a tabela (é o que acontece com os `IN (@a1, @a2, @a3)` do limite de abertos, seção 4). Não parametrizar `StatusSanitizacao` nem `Ativo` nas consultas da fila e da rotação.

## 3. Biblioteca (`GET /midias`)

Sem índice novo, em 50.000 mídias (COUNT + página de 20, com o join em `seg.Usuario`):

| Consulta | COUNT (leituras) | Página (leituras) | CPU da página |
|---|---|---|---|
| L1 Admin, sem filtro | 239 a 414 | 2.415 | 56 a 96 ms |
| L1b a mesma, página 2.000 (`OFFSET 39.980`) | 239 a 414 | 2.415 | 68 a 107 ms |
| L2 locutor, tipo Música | 819 | 2.415 | 52 a 59 ms |
| L3 Admin, Música, Aprovado, ativo | 783 | 2.415 | 49 a 67 ms |
| L3b Admin, status Pendente (a tela da fila) | 461 a 1.025 | 604 a 1.168 | 5 ms |
| L4 locutor, busca rara (`numero 1234`) | 2.375 | 2.415 | 187 ms |
| L5 Admin, Música, busca que casa tudo (`ao vivo`) | 2.375 | 2.415 | 147 ms |

A página lê a tabela inteira (varredura do clustered com *sort* parcial) porque não há índice por `DataUploadUtc` e a consulta devolve as 25 colunas. O custo cresce linear com a biblioteca (2.400 leituras ≈ 19 MB por página; 10× mais mídia seriam ~24 mil). Com 50 mil faixas, que é o teto da v1, é aceitável para uma tela de Admin e Locutor.

**Índice testado e rejeitado:** `(DataUploadUtc DESC, Id) INCLUDE (TipoMidia, StatusSanitizacao, Ativo)`. Em condições normais derruba a página 1 para **111 leituras** (e a L5 para 127). Mas o EF **parametriza o `OFFSET`**, o plano é compilado para o primeiro valor que chega, e o plano "índice em ordem + *key lookup* por linha" volta para qualquer página: a página 2.000 foi a **122.862 leituras** e a busca rara (L4) a **105.427**, contra 2.415 sem o índice (o otimizador acha que vai encontrar 20 linhas logo e não encontra). Troca um custo estável de 2.400 por um que vai de 111 a 120 mil conforme quem chegou primeiro ao cache de planos. Não vale.

**Gatilho para revisar:** biblioteca acima de ~200 mil mídias, ou a página acima de 200 ms de CPU no Admin. Caminhos, em ordem de custo: (a) o handler projeta só as colunas do DTO em vez da entidade inteira e o índice acima passa a ser *covering* (sem *lookup*, sem o problema do plano); (b) paginação por chave (`DataUploadUtc, Id`) no lugar de `OFFSET`; (c) *full-text* para a busca. Nenhum é necessário na v1.

## 4. Limites do S-M06 pelo SQL (`IniciarUploadHandler`)

| Consulta | Usuário | Antes | Depois |
|---|---|---|---|
| S1 abertos (`CountAsync`) | B1 (6 envios) | 1.155 | **4** |
| S1 abertos (`CountAsync`) | C1 (958 envios) | 1.155 | **12** |
| S1 abertos (`CountAsync`) | A1, o Admin (10.000 envios) | 1.164 | **155** |
| S2 20/h (datas da hora) | qualquer | 2.375 | **3** |
| S3 bytes/h (data e tamanho) | qualquer | 2.375 | **3** |
| S4 espera dos abertos | qualquer | 11 | 11 |

Antes, o `IX_ArquivoMidia_EnviadoPor` só tinha `EnviadoPorUsuarioId` e o plano preferia varrer (S2 e S3 liam a tabela inteira mesmo para um usuário com poucos envios). O índice novo, `(EnviadoPorUsuarioId, DataUploadUtc) INCLUDE (StatusSanitizacao, TamanhoBytes, UploadExpiraEmUtc)`, serve as quatro consultas: *seek* no usuário e na hora para S2, S3 e S4. O S1 é diferente: `StatusSanitizacao` está no `INCLUDE`, não na chave, então ele faz *seek* no usuário e lê **todas as linhas dele** no índice (cobertas, sem *lookup*): custo proporcional ao histórico do usuário (4 leituras com 6 envios, 12 com ~1.000, 155 com 10.000). Mesmo nome do anterior, então a FK continua com índice que começa por `EnviadoPorUsuarioId`. Custo: o índice passa de ~240 para 384 páginas (+1,1 MB com 50 mil mídias); o `COUNT(*)` sem filtro da biblioteca passa a usar outro índice (239 para 414 leituras), irrelevante.

**Decisão do `08` ("o A01b mede e decide se ainda vale um índice filtrado `(EnviadoPorUsuarioId) WHERE StatusSanitizacao IN (1, 2, 3)`"):** não vale. (1) O EF parametriza o `IN` (`IN (@abertos1, @abertos2, @abertos3)`), e o filtro de um índice filtrado não casa com parâmetro (seção 2). (2) O S1 do Admin com 10.000 envios custa 155 leituras com o índice novo (de 1.164), e o `sp_getapplock` por usuário do F02 serializa as chamadas desse usuário: é uma leitura de ~1 ms por POST de quem já está no limite de 20 por hora.

**Gatilho para revisar o S1:** um usuário passar de ~100 mil envios (S1 ≈ 1.500 leituras) ou o S1 passar de 500 leituras. Aí entra um segundo índice **não filtrado**, `(EnviadoPorUsuarioId, StatusSanitizacao) INCLUDE (UploadExpiraEmUtc)`, que o `IN (@abertos1, @abertos2, @abertos3)` parametrizado usa com *seek* por status (3 *seeks*, ~10 leituras, independente do histórico). Não o crio agora: 155 leituras no pior usuário da v1 não justificam o segundo índice na tabela mais escrita do Épico 4.

Migration `20261123100833_IndiceLimitesDeUploadPorUsuario` (`DropIndex` + `CreateIndex`, `Down` volta ao índice simples); `03` e `ArquivoMidiaConfiguration.cs` no mesmo commit. `ddl-check`: o `03` e as migrations geram o mesmo schema (228 itens).

## 5. `proxima`: pedido, rotação e o sorteio

### 5.1 Custo (código real, 50.000 mídias, 2 milhões de `Reproducao`, janela de 90 min com 324 linhas)

| Instrução | Leituras lógicas |
|---|---|
| pedido, fila sem elegível (a chamada comum) | 3 |
| pedido com elegível | 11 |
| rotação, busca 1 (`Sorteio >= @r`) | 12 a 13 (média de 300 sorteios: 17, máx. 23 com a janela cheia) |
| rotação, busca 2 (volta ao início) | 12 |
| rotação, conjunto recente (3ª busca) | 17 |
| carga da faixa pela PK (a que o Forge faz para a URL) | 3 |
| **chamada completa da rotação (pedido vazio + busca 1 + carga)** | **23** (300 sorteios; a busca 2 não foi necessária em nenhum) |

Critério de 50: ok. Pior caso artificial (300 comerciais ativos, todos tocados nos últimos 5 minutos, janela de 20 min): as três buscas em sequência leem ~2.200 leituras (~2 ms). Não acontece em produção com a janela de 20 min (seriam 300 comerciais em 20 minutos) e, se a biblioteca do tipo for pequena, o custo é proporcional a ela.

### 5.2 Viés da chave fixa, medido

`Sorteio` é uniforme e sem colisão (a coluna calculada do A01a), mas a faixa que chega primeiro é a que tem o **maior espaço da chave antes dela**; os espaços entre chaves aleatórias têm distribuição exponencial, então a probabilidade de cada faixa varia muito (viés ~`ln n`). Simulei **100.000 sorteios com a janela de 90 min ativa** (cada sorteio avança 200 s, a faixa em voo excluída, as três buscas do `FilaDoPlayout`) e calculei a distribuição **exata** do mesmo critério (a probabilidade de cada faixa é o espaço da chave que a antecede, dividido por K · 2³²):

| Biblioteca | K | **Simulado, com a janela**: máx / média | p99 / média | mín / média | nunca saiu | **Exato, sem janela**: máx / média |
|---|---|---|---|---|---|---|
| 100 | 1 | 2,27 | 2,09 | 0,04 | 0 | 5,09 |
| 100 | 5 | 1,54 | 1,52 | 0,43 | 0 | 2,19 |
| 300 | 1 | **3,85** | 2,89 | 0,03 | 0 | 5,37 |
| 300 | 5 | 2,08 | 1,93 | 0,26 | 0 | 2,67 |
| 1.000 | 1 | **5,60** | 4,02 | 0,00 | **2** | 6,75 |
| 1.000 | 5 | 2,60 | 2,10 | 0,14 | 0 | 3,00 |
| 50.000 | 1 | (exato) | | | | **12,80** (p99 4,6; mín 0,00004) |
| 50.000 | 3 | (exato) | | | | 5,16 (p99 2,8; mín 0,0096) |
| 50.000 | 4 | (exato) | | | | 4,40 (p99 2,5; mín 0,031) |
| 50.000 | 5 | (exato) | | | | **3,61** (p99 2,3; mín 0,065) |
| 50.000 | 10 | (exato) | | | | 2,67 (p99 1,9; mín 0,18) |
| 50.000 | 20 | (exato) | | | | 2,28 (p99 1,6; mín 0,30) |

- A janela **achata** o viés nas bibliotecas pequenas (a faixa favorecida é excluída logo depois de tocar e a vez passa para a vizinha), mas não o elimina: 3,85× em 300 e 5,60× em 1.000 (duas faixas não saíram em 100 mil sorteios). Em 50 mil a janela exclui ~27 faixas, o que não muda nada: vale o número exato, **12,8×**.
- O gatilho que o `08` fixou ("acima de ~3×, sortear entre as K primeiras") **disparou**.
- Custo de cada K (300 sorteios, busca 1, 50 mil mídias; `medir-e4-a01b-k.sql`). O `NOT EXISTS` da janela varre as linhas da janela a cada candidata, então o custo depende da densidade dela: **janela realista** (faixas de 200 s: ~28 linhas em 90 min) e **janela do dataset do `08`** (326 linhas em 90 min, ~4× mais densa):

  | K | janela realista (28 linhas) | janela do dataset (326 linhas) |
  |---|---|---|
  | 1 | 15 a 16 | 17 a 18 |
  | 3 | 31 | 37 |
  | 4 | 39 | 47 |
  | 5 | **47 a 52** | **57 a 62** |
  | 10 | 87 a 97 | 107 a 117 |

  Chamada completa (pedido 3 + busca + carga 3): K = 1: 21 a 24; K = 3: 37 a 43; K = 4: 45 a 53; **K = 5: 53 a 68**; K = 10: 93 a 123. (Os intervalos são de duas cargas do dataset, a de trabalho e a do `rodar-e4-a01b.sh` do zero: o layout físico muda ~10% de uma carga para outra.) Tentei uma variante com `OPTION (HASH JOIN)` para o custo não crescer com K: pior (352 leituras, varre o índice da rotação). O *nested loops* do hint `INDEX(...)` é o plano certo. K = 5 leva o viés a 3,6× em 50 mil e a ≤ 2,6× até mil faixas; K = 3 e 4 cabem no critério de 50 leituras, mas deixam 5,2× e 4,4× em 50 mil. A escolha é do Nexus (seção 7); recomendo K = 5.

### 5.3 O que o Forge muda (`FilaDoPlayout.cs`, `SortearForaDaJanelaAsync`)

A busca 1 passa a devolver até 5 candidatas, e a 2 completa só se vierem menos de 5; a escolha entre elas é da aplicação (`IAleatorio.Proximo(0, n)`), o que mantém o teste determinístico. O resto (a 3ª busca, a faixa em voo, o tipo por peso) não muda.

```sql
-- busca 1: @k = 5
SELECT TOP (@k) a.Id, a.Titulo, a.Artista
FROM midia.ArquivoMidia AS a WITH (INDEX(IX_ArquivoMidia_Rotacao))
WHERE a.TipoMidia = @tipo AND a.StatusSanitizacao = 4 AND a.Ativo = 1 AND a.Sorteio >= @r AND a.Id <> @emVoo
  AND NOT EXISTS (SELECT 1 FROM midia.Reproducao AS r WITH (INDEX(IX_Reproducao_Periodo))
                  WHERE r.IniciadoEmUtc >= @corteJanela AND r.MidiaId = a.Id)
ORDER BY a.Sorteio;
-- se vieram menos de 5: a mesma consulta com `a.Sorteio < @r` e TOP (5 - já vindas), e junta as listas
```

`TOP (@k)` e `StatusSanitizacao = 4 AND Ativo = 1` ficam como estão (constantes no texto; o filtro de `IX_ArquivoMidia_Rotacao` precisa disso). Medição do texto exato: `infra/perf/medir-e4-a01b-k.sql`. O custo da janela (as linhas dela são lidas por candidata) é a razão de a janela da música ser o que o `08` fixou (90 min): janela maior custa mais leituras por chamada, não mais do que isso. A simulação (`simular-rotacao-e4b.sql`) usa a mesma lógica e pode ser rodada de novo depois da mudança (`rodar-e4-a01b.sh`).

## 6. DoD 9 — `rel.usp_MaisTocadas` com o dataset final

`medir-e4-a02.sql` sobre o mesmo dataset (50 mil mídias, 2 milhões de `Reproducao`, `rel.usp_MaisTocadas` do `03`), cache de dados frio:

| Período | Leituras lógicas (`Reproducao` + `ArquivoMidia`) | CPU / tempo | No parecer do A02 |
|---|---|---|---|
| 7 dias, Top 20 | 238 + 60 | 23 ms / 65 ms | 245 + 60, 29 / 63 ms |
| 30 dias, Top 20 | 1.037 + 60 | 117 ms / 174 ms | 1.039 + 60, 107 / 153 ms |
| 366 dias, Top 20 | 12.504 + 60 | 948 ms / 1.245 ms | 12.501 + 60, 1.188 / 1.560 ms |
| 366 dias, Top 100 | 12.504 + 535 | 926 ms / 1.277 ms | 12.501 + 528 |

Sem mudança: o *seek* em `IX_Reproducao_Periodo` nos três períodos, e o custo de 366 dias é o do volume (2 milhões de linhas).

## 7. O que ficou de fora e o que vem depois

- **Não medi** as consultas da limpeza (`LimpezaDeMidia`) e do lease vencido do Worker: não estão na lista do A01b, rodam em lote e com `StatusSanitizacao` constante, na mesma família da captura.
- **Do Forge:** o K = 5 (seção 5.3). Não proponho teste de plano; os números deste parecer são a referência (se alguém parametrizar `StatusSanitizacao` na rotação ou na captura, a leitura salta de 13 para ~900, e o `rodar-e4-a01b.sh` com o texto novo mostra isso).
- **Para o Nexus decidir:** (1) K = 5 na v1 (53 a 68 leituras por chamada, viés máximo 3,6× em 50 mil), K = 3 ou aceitar o viés da chave fixa e documentar (12,8× em 50 mil); recomendo K = 5 e aceitar as leituras acima de 50. (2) O gatilho da biblioteca (seção 3) no `05` ou no `09` como risco aceito com número.
- **Reproduzir:** `infra/perf/rodar-e4-a01b.sh [pasta]` (contêiner descartável; `MANTER=1` para inspecionar; `PULAR_SIM=1` pula a simulação, a parte longa: ~13 min). Carga + medição "antes" + índice + medição "depois": ~4 min.
