#!/usr/bin/env bash
# =============================================================================
# WebRadio — infra/scripts/ddl-check.sh (E1-F17, job ddl-check; contrato do 03 × migrations do EF, ADR D14)
#
# Cria DOIS bancos descartáveis no SQL Server informado, aplica num deles docs/specs/03-schema-sqlserver.sql e no outro o
# script de `dotnet ef migrations script --idempotent`, ambos com `sqlcmd -b -I` (-b aborta no primeiro erro; -I liga o
# QUOTED_IDENTIFIER, sem o qual todo índice filtrado falha), e compara o catálogo dos dois (infra/scripts/ddl-catalogo.sql).
# Falha se algum script não rodar ou se o schema divergir. A única diferença tolerada é a de renderização do DEFAULT, que o
# SQL Server devolve como CONVERT([tinyint],(1)) quando o DDL foi escrito como (1): mesmo valor (Atlas, E1-A03).
# O resultado completo vai para o $GITHUB_STEP_SUMMARY (para o Atlas) quando o script roda no Actions.
#
# Entrada (variáveis de ambiente):
#   DDL_SQL_CONTAINER   nome do contêiner do SQL Server 2022 (obrigatória)
#   SQLCMDPASSWORD      senha da SA (obrigatória; nunca vai por argumento, só pelo ambiente do docker exec)
#   DOTNET_EF           comando do dotnet-ef (padrão: dotnet-ef; versão = a do EF Core, 10.0.0)
# Os bancos WebRadioDdl03 e WebRadioDdlEf são recriados no início e apagados no fim.
# =============================================================================
set -euo pipefail
export LC_ALL=C

RAIZ="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$RAIZ"
: "${DDL_SQL_CONTAINER:?Informe DDL_SQL_CONTAINER}"
: "${SQLCMDPASSWORD:?Informe SQLCMDPASSWORD (senha da SA)}"
DOTNET_EF="${DOTNET_EF:-dotnet-ef}"
TMP="$(mktemp -d)"
DB_03="WebRadioDdl03"
DB_EF="WebRadioDdlEf"

# O arquivo entra por stdin (-i /dev/stdin): o rootfs do contêiner do SQL Server pode ser só leitura (docker cp falharia).
sql() { docker exec -i -e SQLCMDPASSWORD "$DDL_SQL_CONTAINER" /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -b -I "$@"; }
apagar_bancos() {
  for db in "$DB_03" "$DB_EF"; do
    sql -Q "IF DB_ID(N'$db') IS NOT NULL BEGIN ALTER DATABASE [$db] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$db]; END" >/dev/null 2>&1 || true
  done
}
limpar() { apagar_bancos; rm -rf "$TMP"; }
trap limpar EXIT

apagar_bancos
for db in "$DB_03" "$DB_EF"; do
  # Mesma collation do init-db.sql (03, item 0): colunas sem COLLATE próprio herdam esta.
  sql -Q "CREATE DATABASE [$db] COLLATE Latin1_General_100_CI_AI_SC"
done

echo "== 03-schema-sqlserver.sql em $DB_03"
sql -d "$DB_03" -i /dev/stdin < docs/specs/03-schema-sqlserver.sql > "$TMP/03.log" 2>&1 || { cat "$TMP/03.log"; echo "FALHA: o 03 não rodou." >&2; exit 1; }

echo "== dotnet ef migrations script --idempotent em $DB_EF"
"$DOTNET_EF" migrations script --idempotent --project backend/src/WebRadio.Infrastructure --output "$TMP/ef.sql" > "$TMP/ef-gerar.log" 2>&1 \
  || { cat "$TMP/ef-gerar.log"; echo "FALHA: o EF não gerou o script." >&2; exit 1; }
# O script do EF começa com BOM UTF-8, que o sqlcmd leria como lixo na linha 1.
sed '1s/^\xEF\xBB\xBF//' "$TMP/ef.sql" | sql -d "$DB_EF" -i /dev/stdin > "$TMP/ef.log" 2>&1 || { cat "$TMP/ef.log"; echo "FALHA: o script do EF não rodou." >&2; exit 1; }

echo "== catálogo dos dois bancos"
catalogo() {
  # Normaliza só a renderização do DEFAULT: ((CONVERT([tinyint],(1)))) == ((1)).
  sql -d "$1" -h -1 -W -w 4000 -i /dev/stdin < infra/scripts/ddl-catalogo.sql | grep -v '^$' \
    | sed -E 's/\(CONVERT\(\[[a-z]+\],\(([0-9]+)\)\)\)/((\1))/' | sort
}
catalogo "$DB_03" > "$TMP/03.cat"
catalogo "$DB_EF" > "$TMP/ef.cat"
linhas_03="$(wc -l < "$TMP/03.cat")"
linhas_ef="$(wc -l < "$TMP/ef.cat")"
# Piso: um catálogo vazio dos dois lados "bateria" sem provar nada (ex.: erro de SQL engolido pelo pipe).
if [[ "$linhas_03" -lt 100 || "$linhas_ef" -lt 100 ]]; then
  echo "FALHA: catálogo pequeno demais (03=$linhas_03 linhas, EF=$linhas_ef); esperado mais de 100." >&2
  head -5 "$TMP/03.cat" >&2
  exit 1
fi

if diff -u --label "03-schema-sqlserver.sql" --label "migrations do EF" "$TMP/03.cat" "$TMP/ef.cat" > "$TMP/diff.txt"; then
  echo "ddl-check: o 03 e as migrations do EF geram o mesmo schema ($linhas_03 itens: colunas, índices, FKs, CHECKs e DEFAULTs)."
  resultado="OK — o 03 e as migrations geram o mesmo schema ($linhas_03 itens comparados)."
  rc=0
else
  cat "$TMP/diff.txt"
  echo "FALHA: o 03 e as migrations do EF divergem (acima)." >&2
  resultado="FALHA — divergência entre o 03 e as migrations do EF."
  rc=1
fi

if [[ -n "${GITHUB_STEP_SUMMARY:-}" ]]; then
  {
    echo "### ddl-check (03 × migrations do EF)"
    echo "$resultado"
    echo
    echo "<details><summary>Catálogo do 03 (${linhas_03} itens)</summary>"
    echo
    echo '```'
    cat "$TMP/03.cat"
    echo '```'
    echo "</details>"
    if [[ $rc -ne 0 ]]; then
      echo
      echo '```diff'
      cat "$TMP/diff.txt"
      echo '```'
    fi
  } >> "$GITHUB_STEP_SUMMARY"
fi
exit $rc
