#!/usr/bin/env bash
# E4-A01b (Atlas): dataset final do Épico 4 (50.000 mídias, 2 milhões de Reproducao) e medição das consultas do código real, antes e depois dos
# índices propostos, mais a simulação de 100 mil sorteios da rotação. Num SQL Server DESCARTÁVEL (imagem fixada do docker-compose.yml,
# MSSQL_PID=Express, sem porta publicada, schema do 03). Remove o contêiner ao sair; nunca toca o banco do stack.
#
# Uso: infra/perf/rodar-e4-a01b.sh [pasta-de-saida]
#   MANTER=1     não remove o contêiner ao sair (docker rm -f depois; a senha é impressa nesse modo)
#   PULAR_SIM=1  não roda a simulação do sorteio (a parte longa)
# Saída: <pasta>/carga.txt, medicao-antes.txt, medicao-depois.txt, custo-k.txt, mais-tocadas.txt e simulacao.txt.
set -euo pipefail

raiz="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
perf="$raiz/infra/perf"
saida="${1:-$(mktemp -d)}"
nome="atlas-e4-a01b-$$"
imagem="$(grep -o 'mcr.microsoft.com/mssql/server@sha256:[0-9a-f]*' "$raiz/docker-compose.yml" | head -1)"
[ -n "$imagem" ] || { echo "imagem do sqlserver não encontrada no docker-compose.yml" >&2; exit 1; }
mkdir -p "$saida"

senha="Aa1!$(head -c 48 /dev/urandom | base64 | tr -dc 'A-Za-z0-9' | head -c 24)"
if [ "${MANTER:-0}" = 1 ]; then echo "contêiner $nome; senha da SA: $senha"; else trap 'docker rm -f "$nome" >/dev/null 2>&1 || true' EXIT; fi

docker run -d --name "$nome" --memory 4g -e ACCEPT_EULA=Y -e "MSSQL_PID=Express" -e "MSSQL_SA_PASSWORD=$senha" "$imagem" >/dev/null
sql() { docker exec -e "SQLCMDPASSWORD=$senha" "$nome" /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -b -I "$@"; }
for _ in $(seq 1 60); do sql -Q "SELECT 1" >/dev/null 2>&1 && break; sleep 2; done
sql -Q "SELECT 1" >/dev/null

# Mesmos ajustes do init-db.sql: collation do banco, RCSI, compatibilidade 160.
criar_banco() {
    sql -Q "CREATE DATABASE $1 COLLATE Latin1_General_100_CI_AI_SC;
            ALTER DATABASE $1 SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE;
            ALTER DATABASE $1 SET COMPATIBILITY_LEVEL = 160;
            ALTER DATABASE $1 SET RECOVERY SIMPLE;"
}
criar_banco WebRadioPerf
for f in docs/specs/03-schema-sqlserver.sql:schema.sql infra/perf/gerar-dataset-e4.sql:gerar.sql infra/perf/gerar-dataset-e4b.sql:gerar-b.sql \
         infra/perf/medir-e4-a01b.sql:medir.sql infra/perf/medir-e4-a01b-k.sql:medir-k.sql infra/perf/medir-e4-a02.sql:medir-a02.sql infra/perf/indices-propostos-e4-a01b.sql:indices.sql infra/perf/simular-rotacao-e4b.sql:sim.sql; do
    [ -f "$raiz/${f%%:*}" ] && docker cp "$raiz/${f%%:*}" "$nome:/tmp/${f##*:}"
done
sql -d WebRadioPerf -i /tmp/schema.sql >/dev/null
{ sql -d WebRadioPerf -W -i /tmp/gerar.sql; sql -d WebRadioPerf -W -i /tmp/gerar-b.sql; } >"$saida/carga.txt"
cat "$saida/carga.txt"

# "Antes" = o índice por usuário SIMPLES que o 03 tinha até o A01b (o 03 de hoje já traz o índice novo): volta a ele, mede, e o indices-propostos-e4-a01b.sql aplica o novo.
sql -d WebRadioPerf -Q "DROP INDEX IX_ArquivoMidia_EnviadoPor ON midia.ArquivoMidia; CREATE INDEX IX_ArquivoMidia_EnviadoPor ON midia.ArquivoMidia (EnviadoPorUsuarioId);"
sql -d WebRadioPerf -y 0 -i /tmp/medir.sql >"$saida/medicao-antes.txt"
echo "ok: $saida/medicao-antes.txt"
if [ -f "$perf/indices-propostos-e4-a01b.sql" ]; then
    sql -d WebRadioPerf -i /tmp/indices.sql >"$saida/indices.txt"
    sql -d WebRadioPerf -y 0 -i /tmp/medir.sql >"$saida/medicao-depois.txt"
    echo "ok: $saida/medicao-depois.txt"
fi

# Custo de K = 1, 5 e 10 candidatas no sorteio (parecer, seção 5) e o DoD 9 (rel.usp_MaisTocadas, 7/30/366 dias) com o mesmo dataset.
sql -d WebRadioPerf -W -i /tmp/medir-k.sql >"$saida/custo-k.txt"
sql -d WebRadioPerf -y 0 -i /tmp/medir-a02.sql >"$saida/mais-tocadas.txt"
echo "ok: $saida/custo-k.txt e $saida/mais-tocadas.txt"

if [ "${PULAR_SIM:-0}" != 1 ] && [ -f "$perf/simular-rotacao-e4b.sql" ]; then
    criar_banco WebRadioSim
    sql -d WebRadioSim -i /tmp/schema.sql >/dev/null
    sql -d WebRadioSim -W -i /tmp/sim.sql >"$saida/simulacao.txt"
    echo "ok: $saida/simulacao.txt"
fi
