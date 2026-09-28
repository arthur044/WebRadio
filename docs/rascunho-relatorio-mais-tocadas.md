# Rascunho — `rel.usp_MaisTocadas` (E1-A07)

> Autor: Atlas (DBA) · 2026-09-27
> Escopo do Épico 1: só validar que o índice serve. Entrega efetiva (schema `rel`,
> procedure de verdade) fica para o Épico 3 — de propósito **não** é um `.sql`
> executável em `infra/sql/`, para o job `ddl-check` da CI não tentar rodar contra
> um schema (`rel`) que ainda não existe.

## Forma da consulta

```sql
SELECT TOP (@Top) r.MidiaId, COUNT(*) AS Reproducoes
FROM midia.Reproducao r
WHERE r.IniciadoEmUtc >= @DeUtc AND r.IniciadoEmUtc < @AteUtc
GROUP BY r.MidiaId
ORDER BY Reproducoes DESC;
```

(No Épico 3, provavelmente com `JOIN midia.ArquivoMidia` para trazer Titulo/Artista —
isso não muda a validação do índice abaixo, que é só sobre o acesso a `Reproducao`.)

## Índice de referência

`03-schema-sqlserver.sql` já tem:

```sql
CREATE INDEX IX_Reproducao_Periodo ON midia.Reproducao (IniciadoEmUtc) INCLUDE (MidiaId);
```

## Validação (análise estática — sem instância para confirmar o plano real, ver E1-A01)

- O predicado `IniciadoEmUtc >= @DeUtc AND IniciadoEmUtc < @AteUtc` é um *range* sobre a
  chave do índice → **seek**, não scan da tabela inteira.
- `MidiaId` está no `INCLUDE`: o `GROUP BY MidiaId` e o `COUNT(*)` são resolvidos **só**
  com as colunas do índice — sem key lookup na tabela base (índice cobre a consulta).
- Agregação: como o seek devolve linhas ordenadas por `IniciadoEmUtc` (não por `MidiaId`),
  o otimizador tende a escolher **Hash Match (Aggregate)** em vez de Stream Aggregate
  (evita um sort extra) — aceitável; o ponto que importa (seek em vez de scan) não muda.
- Períodos muito longos (ex.: relatório do ano inteiro) ainda fazem seek, só que sobre
  um range maior — não degrada para scan. Não há necessidade de índice adicional para o
  Épico 1.

## Tentativa de confirmar o plano real (2026-09-28, Docker já disponível — Guardian, L3)

Rodei a consulta contra SQL Server 2022 de verdade, mas só com **1 linha** em
`midia.Reproducao` (dado de teste avulso, não volume representativo). O otimizador
NÃO escolheu `IX_Reproducao_Periodo`: escolheu `IX_Reproducao_MidiaId` (Index Scan) +
`PK_Reproducao` (Clustered Index Seek por lookup). Isso é esperado e **não invalida**
a análise estática acima — com estatística de tabela quase vazia, o custo estimado de
qualquer plano é ~0, e o otimizador pode escolher qualquer índice sem diferença
prática. Confirmar o seek em `IX_Reproducao_Periodo` de verdade exige volume
representativo (milhares de linhas, distribuição realista de `MidiaId`), não uma
tabela vazia — por isso isso continua para o Épico 3, agora só por falta de dado, não
mais por falta de Docker.

## O que falta para fechar de verdade (Épico 3, ligado ao E1-A01/A07)
- Decidir se o schema `rel` fica junto de `midia`/`grade`/`interacao` (least privilege
  do `webradio_relatorio`, `infra/sql/logins.sql`) ou schema próprio `rel` como já
  assumido no nome da procedure — se for `rel`, `logins.sql` precisa do
  `GRANT EXECUTE ON SCHEMA::rel TO webradio_relatorio` que hoje está comentado como
  pendente.
