#!/usr/bin/env bash
# E3-A01/A02 (Atlas): carga do dataset sintético de pedidos e divulgações + medição, num SQL Server DESCARTÁVEL.
#
# Mesma ideia do rodar-a02.sh: contêiner próprio (imagem fixada do docker-compose.yml, MSSQL_PID=Express), sem porta publicada
# e fora de qualquer rede/volume do stack, schema do 03, índices propostos do 07, dataset do A02 (locutores e programas) e
# depois o dataset do A01 no cenário pedido. Remove o contêiner ao sair. Nunca toca o banco WebRadio do stack.
#
# Uso: infra/perf/rodar-a01.sh <A|B> [pasta-de-saida]
#   A   cenário realista (40 Pendente recentes)        B   acúmulo (25.000 Pendente vencidos, 10.000 divulgações inativas)
#   SCHEMA=<arquivo>   schema a aplicar (padrão: docs/specs/03-schema-sqlserver.sql)
#   MSSQL_PID=Developer  muda a edição (padrão Express)
#   MANTER=1           não remove o contêiner ao sair (docker rm -f depois; a senha é impressa nesse modo)
#   ATRASO=<dias>      atraso da retenção do E3-D8 no dataset (padrão 1 = regime; 10 = acúmulo para o A02)
#   SEM_MEDICAO=1      só carrega o dataset (para experimentar à mão com MANTER=1)
# Saída: <pasta>/carga.txt (resumo do dataset), <pasta>/medicao.txt e resumo.txt (medir-a01.sql) e <pasta>/retencao.txt e
# resumo-retencao.txt (medir-a01-retencao.sql, que GRAVA e roda por último).
set -euo pipefail

cenario="${1:?uso: rodar-a01.sh <A|B> [pasta-de-saida]}"
[[ "$cenario" == A || "$cenario" == B ]] || { echo "cenário deve ser A ou B" >&2; exit 1; }
raiz="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
perf="$raiz/infra/perf"
schema="${SCHEMA:-$raiz/docs/specs/03-schema-sqlserver.sql}"
saida="${2:-$(mktemp -d)}"
nome="atlas-a01-$cenario-$$"
imagem="$(grep -o 'mcr.microsoft.com/mssql/server@sha256:[0-9a-f]*' "$raiz/docker-compose.yml" | head -1)"
[ -n "$imagem" ] || { echo "imagem do sqlserver não encontrada no docker-compose.yml" >&2; exit 1; }
[ -f "$schema" ] || { echo "schema não encontrado: $schema" >&2; exit 1; }
mkdir -p "$saida"

senha="Aa1!$(head -c 48 /dev/urandom | base64 | tr -dc 'A-Za-z0-9' | head -c 24)"
[ "${MANTER:-0}" = 1 ] || trap 'docker rm -f "$nome" >/dev/null 2>&1 || true' EXIT

docker run -d --name "$nome" --memory 3g -e ACCEPT_EULA=Y -e "MSSQL_PID=${MSSQL_PID:-Express}" \
    -e "MSSQL_SA_PASSWORD=$senha" "$imagem" >/dev/null

sql() { docker exec -e "SQLCMDPASSWORD=$senha" "$nome" /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -b -I "$@"; }

for _ in $(seq 1 60); do sql -Q "SELECT 1" >/dev/null 2>&1 && break; sleep 2; done
sql -Q "SELECT 1" >/dev/null

sql -Q "CREATE DATABASE WebRadioPerf COLLATE Latin1_General_100_CI_AI_SC;
        ALTER DATABASE WebRadioPerf SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE;
        ALTER DATABASE WebRadioPerf SET ALLOW_SNAPSHOT_ISOLATION ON;
        ALTER DATABASE WebRadioPerf SET COMPATIBILITY_LEVEL = 160;
        ALTER DATABASE WebRadioPerf SET RECOVERY SIMPLE;"

docker cp "$schema" "$nome:/tmp/schema.sql"
docker cp "$perf/indices-propostos-a01.sql" "$nome:/tmp/indices.sql"
docker cp "$perf/gerar-dataset-a02.sql" "$nome:/tmp/gerar-a02.sql"
docker cp "$perf/gerar-dataset-a01.sql" "$nome:/tmp/gerar-a01.sql"

sql -d WebRadioPerf -i /tmp/schema.sql >/dev/null
sql -d WebRadioPerf -i /tmp/indices.sql >/dev/null
sql -d WebRadioPerf -W -i /tmp/gerar-a02.sql >/dev/null
sql -d WebRadioPerf -W -v CENARIO="$cenario" -v ATRASO="${ATRASO:-1}" -i /tmp/gerar-a01.sql >"$saida/carga.txt"
cat "$saida/carga.txt"

if [ -f "$perf/medir-a01.sql" ] && [ "${SEM_MEDICAO:-0}" != 1 ]; then
    docker cp "$perf/medir-a01.sql" "$nome:/tmp/medir.sql"
    sql -d WebRadioPerf -y 0 -v CENARIO="$cenario" -v ATRASO="${ATRASO:-1}" -i /tmp/medir.sql >"$saida/medicao.txt"
    python3 -I "$perf/resumir-plano.py" "$saida/medicao.txt" >"$saida/resumo.txt"
    echo "ok: $saida/medicao.txt e $saida/resumo.txt"
fi
if [ -f "$perf/medir-a01-retencao.sql" ] && [ "${SEM_MEDICAO:-0}" != 1 ]; then
    docker cp "$perf/medir-a01-retencao.sql" "$nome:/tmp/retencao.sql"
    sql -d WebRadioPerf -y 0 -i /tmp/retencao.sql >"$saida/retencao.txt"
    python3 -I "$perf/resumir-plano.py" "$saida/retencao.txt" >"$saida/resumo-retencao.txt"
    echo "ok: $saida/retencao.txt e $saida/resumo-retencao.txt"
fi
[ "${MANTER:-0}" != 1 ] || echo "contêiner mantido: $nome (senha da SA: $senha)"
