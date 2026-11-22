# Parecer E4-A02 — `rel.usp_MaisTocadas`

> Autor: Atlas (DBA) · tarefa E4-A02 (`08-epico-4-tarefas.md`, E4-D12, DoD 9) · 2026-11-19
> Entrega: schema `rel` no `init-db.sql`, `GRANT EXECUTE` no `logins.sql`, a procedure no `03` (seção 9.1) e na migration `RelatorioMaisTocadas`, o `ddl-catalogo.sql` comparando schemas e procedures, e os scripts `infra/perf/medir-e4-a02.sql` e `rodar-e4-a02.sh`. O rascunho do E1-A07 (`docs/rascunho-relatorio-mais-tocadas.md`) virou o texto da procedure e foi apagado.

## Veredito

**Pronto para o Forge ligar o `GET /relatorios/mais-tocadas` no D3.** O plano faz *seek* em `IX_Reproducao_Periodo` nos três períodos do DoD 9, inclusive em 366 dias (que o `08` aceitava como varredura do índice). O custo de 366 dias é o do volume (2 milhões de linhas lidas, ~1,2 s de CPU), não do plano.

## Contrato para o Forge

`EXEC rel.usp_MaisTocadas @DeUtc, @AteUtc, @Top` → `MidiaId, Titulo, Artista, TipoMidia, Reproducoes` (`int`), da mais tocada para a menos, desempate por `MidiaId`. Parâmetros `datetime2(0)`, `datetime2(0)`, `int`, **sem valor padrão** (o padrão de 20 é da API). `@AteUtc` é exclusivo.

| Entrada | Erro (`SqlException.Number`, severidade 16) |
|---|---|
| `@DeUtc` ou `@AteUtc` nulo, ou `@AteUtc <= @DeUtc` | `50101` |
| período maior que 366 dias (`@AteUtc > DATEADD(DAY, 366, @DeUtc)`; 366 dias exatos passa) | `50102` |
| `@Top` nulo, menor que 1 ou maior que 100 | `50103` |

A API já valida e devolve 400 antes de chamar (`02`); os `THROW` são a segunda linha, para quem chamar a procedure por outro caminho. O Forge pode mapear 50101–50103 para 400 se quiser uma terceira.

## Medição (dataset do `gerar-dataset-e4.sql`: 50.000 mídias, 2.000.000 de `Reproducao` em 12 meses; SQL Server 2022 Express em contêiner, cache de dados frio)

| Período | Linhas lidas | Plano | Leituras lógicas (`Reproducao` + `ArquivoMidia`) | CPU / tempo |
|---|---|---|---|---|
| 7 dias, Top 20 | 38.812 | *seek* `IX_Reproducao_Periodo` → *hash aggregate* → *top N sort* → *seek* na PK | 245 + 60 | 29 ms / 63 ms |
| 30 dias, Top 20 | 165.845 | idem | 1.039 + 60 | 107 ms / 153 ms |
| 366 dias, Top 20 | 2.000.000 | idem (o *seek* cobre o índice inteiro) | 12.501 + 60 | 1.188 ms / 1.560 ms (1.177 ms com cache quente) |
| 366 dias, Top 100 | 2.000.000 | idem | 12.501 + 528 | 1.007 ms / 1.337 ms |

Sem *key lookup* na tabela base (o `INCLUDE (MidiaId)` cobre o agrupamento). O `JOIN` com `ArquivoMidia` roda só sobre as 20 (ou 100) linhas do `TOP`: 60 leituras com Top 20, contra 187 na forma ingênua do rascunho (o `JOIN` antes do `GROUP BY` e do `TOP`, 30 dias). O ganho é pequeno com Top 20 e cresce com o Top; o que importa mais é que o custo da mídia não depende do período.

Os 366 dias são a pior chamada e só o Admin a faz. Se um dia pesar (relatório aberto muitas vezes por minuto), o caminho é cache de curta duração na API, ou uma tabela de contagem diária; não há índice que a melhore, porque o gargalo é agregar 2 milhões de linhas. Não proponho nada disso agora.

## Segurança

- `webradio_app` e `webradio_relatorio` executam a procedure (o `GRANT EXECUTE ON SCHEMA::rel` está no `logins.sql`); nenhum dos dois cria objeto no `rel` (`CREATE PROCEDURE` negado ao `webradio_app`, conferido). `webradio_relatorio` continua sem acesso a `interacao`.
- A procedure lê `midia` pela cadeia de propriedade (todos os schemas são `dbo`), então o `SELECT` em `midia` não é pré-requisito dela.
- `init-db.sql` e `logins.sql` rodam duas vezes sem erro (idempotência conferida).

## `ddl-check`

O catálogo agora traz `SCHEMA|nome|dono=` e `PROC|schema.nome|params=…|hash=…`; o hash é o SHA-256 da definição sem espaços em branco e com `CREATE OR ALTER` lido como `CREATE`. 226 itens iguais entre o `03` e as migrations. **Teste negativo:** trocar `366` por `365` no corpo da procedure da migration faz o job falhar apontando a linha `PROC|` (e uma dica no fim da saída).

Duas armadilhas que o teste achou, para quem mexer na procedure:

1. Um comentário **antes** do `CREATE PROCEDURE` no mesmo lote entra na definição gravada e quebra o hash; no `03` o cabeçalho está num lote à parte (`GO`).
2. O texto é mantido em dois lugares (`03` e migration); o `ddl-check` é o que impede que divirjam. Mudar a procedure pede uma migration nova (`CREATE OR ALTER`) e o mesmo texto no `03`.

## Limites desta medição

- O volume é o do `gerar-dataset-e4.sql` (distribuição uniforme de `MidiaId`). O A01b repete o DoD 9 com o dataset final e o código real e fecha o parecer `e4-a01-midia.md`.
- Express limita CPU e memória; em produção o tempo de 366 dias tende a ser menor.
