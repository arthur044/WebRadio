#!/usr/bin/env bash
# E4-A02 (Atlas): carga do dataset do Épico 4 e medição de rel.usp_MaisTocadas (DoD 9: 7, 30 e 366 dias) num SQL Server DESCARTÁVEL.
#
# Mesma ideia do rodar-e4-a01a.sh: contêiner próprio (imagem fixada do docker-compose.yml, MSSQL_PID=Express), sem porta publicada, schema
# do 03 (que já traz o schema rel e a procedure), dataset de gerar-dataset-e4.sql e medir-e4-a02.sql. Remove o contêiner ao sair.
# Nunca toca o banco WebRadio do stack.
#
# Uso: infra/perf/rodar-e4-a02.sh [pasta-de-saida]
#   SCHEMA=<arquivo>   schema a aplicar (padrão: docs/specs/03-schema-sqlserver.sql)
#   MANTER=1           não remove o contêiner ao sair (docker rm -f depois; a senha é impressa nesse modo)
# Saída: <pasta>/carga.txt e <pasta>/medicao.txt (leituras, tempos e planos).
set -euo pipefail

raiz="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
perf="$raiz/infra/perf"
schema="${SCHEMA:-$raiz/docs/specs/03-schema-sqlserver.sql}"
saida="${1:-$(mktemp -d)}"
nome="atlas-e4-a02-$$"
imagem="$(grep -o 'mcr.microsoft.com/mssql/server@sha256:[0-9a-f]*' "$raiz/docker-compose.yml" | head -1)"
[ -n "$imagem" ] || { echo "imagem do sqlserver não encontrada no docker-compose.yml" >&2; exit 1; }
[ -f "$schema" ] || { echo "schema não encontrado: $schema" >&2; exit 1; }
mkdir -p "$saida"

senha="Aa1!$(head -c 48 /dev/urandom | base64 | tr -dc 'A-Za-z0-9' | head -c 24)"
if [ "${MANTER:-0}" = 1 ]; then echo "senha da SA: $senha"; else trap 'docker rm -f "$nome" >/dev/null 2>&1 || true' EXIT; fi

docker run -d --name "$nome" --memory 3g -e ACCEPT_EULA=Y -e "MSSQL_PID=Express" -e "MSSQL_SA_PASSWORD=$senha" "$imagem" >/dev/null
sql() { docker exec -e "SQLCMDPASSWORD=$senha" "$nome" /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -b -I "$@"; }
for _ in $(seq 1 60); do sql -Q "SELECT 1" >/dev/null 2>&1 && break; sleep 2; done
sql -Q "SELECT 1" >/dev/null

# Mesmos ajustes do init-db.sql: collation do banco, RCSI, compatibilidade 160.
sql -Q "CREATE DATABASE WebRadioPerf COLLATE Latin1_General_100_CI_AI_SC;
        ALTER DATABASE WebRadioPerf SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE;
        ALTER DATABASE WebRadioPerf SET COMPATIBILITY_LEVEL = 160;
        ALTER DATABASE WebRadioPerf SET RECOVERY SIMPLE;"

for f in "$schema:schema.sql" "$perf/gerar-dataset-e4.sql:gerar.sql" "$perf/medir-e4-a02.sql:medir.sql"; do
    docker cp "${f%%:*}" "$nome:/tmp/${f##*:}"
done
sql -d WebRadioPerf -i /tmp/schema.sql >/dev/null
sql -d WebRadioPerf -W -i /tmp/gerar.sql >"$saida/carga.txt"
cat "$saida/carga.txt"
sql -d WebRadioPerf -y 0 -i /tmp/medir.sql >"$saida/medicao.txt"
echo "ok: $saida/medicao.txt"
