# Parecer E4-A01a — consultas da `proxima` (pedido e rotação), migration e índices

> Autor: Atlas (DBA) · tarefa E4-A01a (`08-epico-4-tarefas.md`, E4-D4 e E4-D5) · 2026-11-08
> Entrega: este texto das consultas (o Forge copia no E4-F08), a migration `RotacaoEEntregaDoPlayout`, o `03` e o `ddl-check` estendido. A medição com o código real e a simulação dos sorteios são do E4-A01b.

## Veredito

**Pronto para o Forge usar no F08**, com três correções em relação ao texto do `08` que a medição obrigou:

1. **`Sorteio` não pode ser `CHECKSUM(Id)`** (proposta minha no review do #92, errada). Com `NEWSEQUENTIALID()` os Ids são quase iguais, e o `CHECKSUM` dá **4.086 valores distintos para 40.000 mídias, todos negativos, numa faixa estreita** (de −268 milhões a −6.850). Um `@r` sorteado no `int` inteiro cai fora dessa faixa em metade das chamadas, e cada grupo de ~10 mídias com o mesmo valor só devolve a primeira por `Id`: a rotação tocaria ~10% da biblioteca. A coluna agora é **os 4 primeiros bytes do SHA-256 do `Id`**: 49.999 valores distintos em 50.000 mídias, decis uniformes (5.000 por decil) e o maior intervalo entre chaves em 10,5 vezes a média (era o ~10x previsto no `08`, que o A01b mede em sorteios).
2. **A consulta da rotação precisa do hint `INDEX(IX_Reproducao_Periodo)` no `NOT EXISTS`.** Sem o hint, o plano usou `IX_Reproducao_MidiaId` mais *key lookup* para ler `IniciadoEmUtc`: 285 leituras em vez de 6 (cada mídia tem ~40 reproduções).
3. **O `EXISTS` da mídia na CTE do pedido tira o *skip* do `READPAST`** (seção 3): o plano usa *nested loops* com pré-busca, que trava (U) as linhas seguintes do índice. A invariante do E4-D5 (nunca o mesmo pedido para duas chamadas) vale; só que uma segunda chamada simultânea pode receber 204 e cair na rotação mesmo com outro pedido elegível, que fica para a chamada seguinte. Não pede mudança no texto do `08`; o teste do F08 já pede um pedido elegível e dois `proxima`.

## 1. O que a migration muda (`20261108224043_RotacaoEEntregaDoPlayout`)

| Objeto | Mudança |
|---|---|
| `midia.ArquivoMidia.Sorteio` | coluna calculada **persistida** (`CONVERT(int, SUBSTRING(HASHBYTES('SHA2_256', CONVERT(binary(16), Id)), 1, 4))`). Nunca gravada pela aplicação: gravar a cada faixa trocaria o `RowVersion` e daria 412 na edição do Admin. Propriedade de sombra no EF (`HasComputedColumnSql(..., stored: true)`); o Forge consulta por SQL |
| `midia.IX_ArquivoMidia_Rotacao` | `(TipoMidia, Sorteio) INCLUDE (Titulo, Artista) WHERE StatusSanitizacao = 4 AND Ativo = 1` |
| `interacao.PedidoMusica.EnviadoPlayoutInstancia` | `uniqueidentifier NULL`, propriedade de sombra até o Forge levá-la ao domínio no F08 |
| `interacao.IX_PedidoMusica_Playout` | refeito: `(ModeradoEmUtc) INCLUDE (MidiaId, EnviadoPlayoutEmUtc, EnviadoPlayoutInstancia) WHERE Status = 2 AND MidiaId IS NOT NULL` (o filtro antigo exigia `EnviadoPlayoutEmUtc IS NULL`, que o `OR` do E4-D5 não implica) |
| `infra/scripts/ddl-catalogo.sql` | a linha da coluna passa a trazer a definição e a persistência da coluna calculada. Teste negativo: trocar o `Sorteio` do `03` por `CHECKSUM(Id)` faz o `ddl-check` falhar apontando a linha |

`ddl-check`: o `03` e as migrations geram o mesmo schema (219 itens). Em banco com dados, adicionar uma coluna calculada persistida reescreve a tabela `ArquivoMidia`; hoje ela está vazia em todo ambiente (nenhum upload existia antes do Épico 4).

## 2. Texto das consultas (para o F08)

Parâmetros: `@instancia uniqueidentifier` (cabeçalho `X-Playout-Instancia`, já validado), `@agora datetime2(3)`, `@corte24h = @agora − 24 h`, `@tipo tinyint`, `@r int` (sorteado em `[int.MinValue, int.MaxValue]`), `@corteJanela datetime2(0) = @agora − janela do tipo`, `@emVoo uniqueidentifier` (`Guid.Empty` quando não há faixa em voo).

### 2.1 Pedido (E4-D5): captura e marcação numa só instrução

```sql
;WITH c AS (
    SELECT TOP (1) p.Id, p.MidiaId, p.EnviadoPlayoutEmUtc, p.EnviadoPlayoutInstancia
    FROM interacao.PedidoMusica AS p WITH (UPDLOCK, READPAST, ROWLOCK, INDEX(IX_PedidoMusica_Playout))
    WHERE p.Status = 2 AND p.MidiaId IS NOT NULL AND p.ModeradoEmUtc >= @corte24h
      AND (p.EnviadoPlayoutEmUtc IS NULL OR p.EnviadoPlayoutInstancia IS NULL OR p.EnviadoPlayoutInstancia <> @instancia)
      AND EXISTS (SELECT 1 FROM midia.ArquivoMidia AS m WHERE m.Id = p.MidiaId AND m.StatusSanitizacao = 4 AND m.Ativo = 1)
    ORDER BY p.ModeradoEmUtc)
UPDATE c SET EnviadoPlayoutEmUtc = @agora, EnviadoPlayoutInstancia = @instancia
OUTPUT inserted.Id AS PedidoId, inserted.MidiaId;
```

Zero linhas → não há pedido elegível, segue para a rotação. Uma linha → o Forge carrega `Bucket` e `ChaveStorage` da mídia pela PK e assina a URL.

### 2.2 Rotação (E4-D4): três buscas em ordem, para no primeiro resultado

```sql
-- Busca 1: Sorteio >= @r. Busca 2 (volta ao início): a mesma com `a.Sorteio < @r`. Só se as duas não devolverem linha, a busca 3.
SELECT TOP (1) a.Id, a.Titulo, a.Artista
FROM midia.ArquivoMidia AS a WITH (INDEX(IX_ArquivoMidia_Rotacao))
WHERE a.TipoMidia = @tipo AND a.StatusSanitizacao = 4 AND a.Ativo = 1 AND a.Sorteio >= @r AND a.Id <> @emVoo
  AND NOT EXISTS (SELECT 1 FROM midia.Reproducao AS r WITH (INDEX(IX_Reproducao_Periodo))
                  WHERE r.IniciadoEmUtc >= @corteJanela AND r.MidiaId = a.Id)
ORDER BY a.Sorteio;

-- Busca 3 (fallback): nenhuma candidata fora da janela; a que tocou há mais tempo DENTRO do conjunto recente.
SELECT TOP (1) a.Id, a.Titulo, a.Artista
FROM (SELECT r.MidiaId, MAX(r.IniciadoEmUtc) AS Ultima
      FROM midia.Reproducao AS r WHERE r.IniciadoEmUtc >= @corteJanela GROUP BY r.MidiaId) AS u
JOIN midia.ArquivoMidia AS a ON a.Id = u.MidiaId
WHERE a.TipoMidia = @tipo AND a.StatusSanitizacao = 4 AND a.Ativo = 1 AND a.Id <> @emVoo
ORDER BY u.Ultima, a.Id;
```

Os pesos entre os tipos e a retirada de um tipo sem candidata do sorteio ficam na aplicação (três chamadas no máximo). Nenhuma linha em nenhum tipo → 204.

## 3. Medição

Banco descartável (imagem do `docker-compose.yml`, RCSI ligado, compatibilidade 160), schema do `03` da branch, `infra/perf/rodar-e4-a01a.sh` (≈ 10 min, remove o contêiner ao sair). Dataset (`gerar-dataset-e4.sql`): 50.000 mídias (80% `Musica`, 15% `Vinheta`, 5% `Comercial`; 5% desativadas; 2% `Rejeitado`), 2.000.000 de `Reproducao` em 12 meses (≈ 228 por hora; 352 linhas na janela de 90 min) e 105.000 pedidos (100.000 `Tocado` com mídia, 5.000 `Aprovado` manual sem mídia, 4 `Aprovado` com mídia em situações distintas). Plano real (`STATISTICS PROFILE`) e `STATISTICS IO`; `DROPCLEANBUFFERS` e `FREEPROCCACHE` antes das consultas de pedido e do R1.

| # | Consulta | Plano | Leituras lógicas |
|---|---|---|---|
| P1 | Pedido (2.1) com pedidos elegíveis | *seek* de faixa no `IX_PedidoMusica_Playout` (24 h) → *Clustered Index Update*; o `EXISTS` é *seek* na PK | `PedidoMusica` 7 + `ArquivoMidia` 16 = **23** |
| P2 | Pedido (2.1), nenhum elegível (o caso comum: uma chamada por faixa) | *seek* no `IX_PedidoMusica_Playout`, sem linha | **2** |
| R1 / R2 | Rotação, busca 1 e busca 2 (`Musica`, 40 mil candidatas) | *seek* no `IX_ArquivoMidia_Rotacao`; *anti semi join* por *seek* no `IX_Reproducao_Periodo` | **11** cada (5 + 6) |
| R4 | R1 + carga da linha pela PK (URL pré-assinada) | idem + *seek* na PK | **14** |
| R3 | Rotação, busca 3 (fallback) | agregação do conjunto recente (`IX_Reproducao_Periodo`) + *nested loops* com pré-busca na PK/`IX_ArquivoMidia_Biblioteca` | 6 + 4 a 124 (varia com a pré-busca do *nested loops*; **caminho raro**) |
| R5 | Pior caso: 300 comerciais ativos, todos na janela (R1 + R2 + R3 em sequência) | R1 e R2 percorrem as 300 candidatas, cada uma com um *seek* de janela | ≈ **1.630** (R1) + 130 (R2) + 66 (R3): **só quando a biblioteca do tipo é menor que as reproduções da janela** |

- **Critério do `08` (< 50 leituras por chamada)**: atendido no caso comum (11 a 14). O pior caso (biblioteca do tipo menor que o número de reproduções na janela de repetição, hoje ≈ 350) custa milhares de leituras por chamada, ainda em décimos de milissegundo por chamada a cada ~3 minutos; o A01b mede com o código real e registra o caso.
- **Distribuição da chave** (`medir-e4-a01a.sql`, seção D0): 49.999 valores distintos em 50.000, decis de 5.000 e maior intervalo 10,5 vezes a média. A faixa mais favorecida tem ~10 vezes a chance média; o A01b simula 100 mil sorteios e decide, com o dono do produto, se serve ou se a busca passa a sortear entre as K primeiras candidatas a partir de `@r`.

### Concorrência do `READPAST` (E4-D5)

Quatro sessões pegando pedido, uma por segundo, cada uma segurando o seu por 5 s, com 3 pedidos elegíveis: **nunca o mesmo pedido** (invariante do E4-D5), mas só **uma** recebeu um pedido; as outras receberam 204. Causa: o `EXISTS` do 2.1 vira *nested loops* com pré-busca, e a pré-busca trava (U) as linhas seguintes do índice. A mesma CTE **sem** o `EXISTS` entrega um pedido distinto a cada sessão (3 de 3). Consequência prática: uma segunda chamada simultânea cai na rotação em vez de pegar o segundo pedido; o pedido não se perde nem se duplica e sai na chamada seguinte. Como a `fila_api` do Liquidsoap chama a `proxima` em série, aceito. Se um dia houver vários chamadores simultâneos, a alternativa é capturar sem o `EXISTS` (a captura marca a instância) e conferir a mídia depois, descartando e capturando de novo se ela não servir.

## 4. Para o Forge (F08)

- O hint `INDEX(...)` nas três tabelas é parte do texto: o plano com o índice certo é o medido, e o teste do F08 acusa um índice renomeado (falha em tempo de execução).
- A mesma forma de CTE com `READPAST` vale para a captura do Worker (E4-F05): lá não há `JOIN` na CTE (a tabela é só `ArquivoMidia`), então o *skip* funciona como no medido sem o `EXISTS`; confirmar com um teste de dois Workers.
- `Sorteio` e `EnviadoPlayoutInstancia` são propriedades de sombra. Quando o Forge levar a instância ao domínio, mapear com o **mesmo nome de coluna**: a migration não muda.
- `@r` vem de `Random.Shared.Next(int.MinValue, int.MaxValue)`; usar um gerador injetável para o teste de rotação ser determinístico.

## 5. Reprodução

```bash
infra/perf/rodar-e4-a01a.sh [pasta-de-saida]   # carga.txt, medicao.txt e concorrencia.txt
```

`medir-e4-a01a.sql` traz o texto exato de cada consulta medida; `gerar-dataset-e4.sql` materializa os números aleatórios antes do `INSERT` (com `CHECKSUM(NEWID())` dentro do `JOIN` o plano reavalia por linha e a carga de 2 milhões não termina).
