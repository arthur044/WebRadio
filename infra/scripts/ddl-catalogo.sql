/* =====================================================================
   WebRadio — infra/scripts/ddl-catalogo.sql (E1-F17, job ddl-check)
   Descreve o schema de um banco em linhas de texto comparáveis: colunas (tipo, tamanho, nulabilidade, collation), índices
   (únicos, filtro, colunas e INCLUDEs), FKs (com a ação), CHECKs e DEFAULTs. O ddl-check.sh roda isto no banco criado pelo
   03-schema-sqlserver.sql e no criado pelo `dotnet ef migrations script --idempotent` e compara as saídas. Só leitura.
   O COLLATE explícito evita "Cannot resolve collation conflict" entre as colunas do catálogo e os literais.
   ===================================================================== */
SET NOCOUNT ON;

SELECT 'COLUNA|' + s.name COLLATE Latin1_General_BIN2 + '.' + t.name COLLATE Latin1_General_BIN2 + '.' + c.name COLLATE Latin1_General_BIN2
       + '|' + ty.name COLLATE Latin1_General_BIN2 + '(' + CAST(c.max_length AS varchar) + ',' + CAST(c.precision AS varchar) + ',' + CAST(c.scale AS varchar) + ')'
       + '|null=' + CAST(c.is_nullable AS varchar) + '|id=' + CAST(c.is_identity AS varchar)
       + '|col=' + ISNULL(c.collation_name COLLATE Latin1_General_BIN2, '')
FROM sys.columns c
JOIN sys.tables t ON t.object_id = c.object_id
JOIN sys.schemas s ON s.schema_id = t.schema_id
JOIN sys.types ty ON ty.user_type_id = c.user_type_id
WHERE t.name COLLATE Latin1_General_BIN2 <> '__EFMigrationsHistory'
UNION ALL
SELECT 'INDICE|' + s.name COLLATE Latin1_General_BIN2 + '.' + t.name COLLATE Latin1_General_BIN2 + '.' + i.name COLLATE Latin1_General_BIN2
       + '|unico=' + CAST(i.is_unique AS varchar) + '|pk=' + CAST(i.is_primary_key AS varchar)
       + '|filtro=' + ISNULL(i.filter_definition COLLATE Latin1_General_BIN2, '')
       + '|cols=' + (SELECT STRING_AGG(c.name COLLATE Latin1_General_BIN2
                                       + CASE WHEN ic.is_descending_key = 1 THEN ' DESC' ELSE '' END
                                       + CASE WHEN ic.is_included_column = 1 THEN ' INC' ELSE '' END, ',')
                            WITHIN GROUP (ORDER BY ic.is_included_column, ic.key_ordinal, ic.index_column_id)
                     FROM sys.index_columns ic
                     JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                     WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id)
FROM sys.indexes i
JOIN sys.tables t ON t.object_id = i.object_id
JOIN sys.schemas s ON s.schema_id = t.schema_id
WHERE i.name IS NOT NULL AND t.name COLLATE Latin1_General_BIN2 <> '__EFMigrationsHistory'
UNION ALL
SELECT 'FK|' + s.name COLLATE Latin1_General_BIN2 + '.' + t.name COLLATE Latin1_General_BIN2 + '.' + f.name COLLATE Latin1_General_BIN2
       + '|ref=' + rs.name COLLATE Latin1_General_BIN2 + '.' + rt.name COLLATE Latin1_General_BIN2
       + '|del=' + f.delete_referential_action_desc COLLATE Latin1_General_BIN2
       + '|upd=' + f.update_referential_action_desc COLLATE Latin1_General_BIN2
FROM sys.foreign_keys f
JOIN sys.tables t ON t.object_id = f.parent_object_id
JOIN sys.schemas s ON s.schema_id = t.schema_id
JOIN sys.tables rt ON rt.object_id = f.referenced_object_id
JOIN sys.schemas rs ON rs.schema_id = rt.schema_id
UNION ALL
SELECT 'CHECK|' + s.name COLLATE Latin1_General_BIN2 + '.' + t.name COLLATE Latin1_General_BIN2 + '.' + k.name COLLATE Latin1_General_BIN2
       + '|' + k.definition COLLATE Latin1_General_BIN2
FROM sys.check_constraints k
JOIN sys.tables t ON t.object_id = k.parent_object_id
JOIN sys.schemas s ON s.schema_id = t.schema_id
UNION ALL
SELECT 'DEFAULT|' + s.name COLLATE Latin1_General_BIN2 + '.' + t.name COLLATE Latin1_General_BIN2 + '.' + d.name COLLATE Latin1_General_BIN2
       + '|' + d.definition COLLATE Latin1_General_BIN2
FROM sys.default_constraints d
JOIN sys.tables t ON t.object_id = d.parent_object_id
JOIN sys.schemas s ON s.schema_id = t.schema_id
ORDER BY 1;
