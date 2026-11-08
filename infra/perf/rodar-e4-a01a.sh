#!/usr/bin/env bash
# E4-A01a (Atlas): carga do dataset do Épico 4 e medição da `proxima` (pedido, E4-D5; rotação, E4-D4) num SQL Server DESCARTÁVEL.
#
# Mesma ideia do rodar-a01.sh: contêiner próprio (imagem fixada do docker-compose.yml, MSSQL_PID=Express), sem porta publicada,
# schema do 03, dataset de gerar-dataset-e4.sql, medir-e4-a01a.sql e o teste de concorrência do READPAST (duas sessões). Remove o
# contêiner ao sair. Nunca toca o banco WebRadio do stack.
#
# Uso: infra/perf/rodar-e4-a01a.sh [pasta-de-saida]
#   SCHEMA=<arquivo>   schema a aplicar (padrão: docs/specs/03-schema-sqlserver.sql)
#   MANTER=1           não remove o contêiner ao sair (docker rm -f depois; a senha é impressa nesse modo)
# Saída: <pasta>/carga.txt, <pasta>/medicao.txt (plano e leituras) e <pasta>/concorrencia.txt.
set -euo pipefail

raiz="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
perf="$raiz/infra/perf"
schema="${SCHEMA:-$raiz/docs/specs/03-schema-sqlserver.sql}"
saida="${1:-$(mktemp -d)}"
nome="atlas-e4-a01a-$$"
imagem="$(grep -o 'mcr.microsoft.com/mssql/server@sha256:[0-9a-f]*' "$raiz/docker-compose.yml" | head -1)"
[ -n "$imagem" ] || { echo "imagem do sqlserver não encontrada no docker-compose.yml" >&2; exit 1; }
[ -f "$schema" ] || { echo "schema não encontrado: $schema" >&2; exit 1; }
mkdir -p "$saida"

senha="Aa1!$(head -c 48 /dev/urandom | base64 | tr -dc 'A-Za-z0-9' | head -c 24)"
[ "${MANTER:-0}" = 1 ] && echo "senha da SA: $senha" || trap 'docker rm -f "$nome" >/dev/null 2>&1 || true' EXIT

docker run -d --name "$nome" --memory 3g -e ACCEPT_EULA=Y -e "MSSQL_PID=Express" -e "MSSQL_SA_PASSWORD=$senha" "$imagem" >/dev/null
sql() { docker exec -e "SQLCMDPASSWORD=$senha" "$nome" /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -b -I "$@"; }
for _ in $(seq 1 60); do sql -Q "SELECT 1" >/dev/null 2>&1 && break; sleep 2; done
sql -Q "SELECT 1" >/dev/null

# Mesmos ajustes do init-db.sql: collation do banco, RCSI, compatibilidade 160.
sql -Q "CREATE DATABASE WebRadioPerf COLLATE Latin1_General_100_CI_AI_SC;
        ALTER DATABASE WebRadioPerf SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE;
        ALTER DATABASE WebRadioPerf SET COMPATIBILITY_LEVEL = 160;
        ALTER DATABASE WebRadioPerf SET RECOVERY SIMPLE;"

for f in "$schema:schema.sql" "$perf/gerar-dataset-e4.sql:gerar.sql" "$perf/medir-e4-a01a.sql:medir.sql"; do
    docker cp "${f%%:*}" "$nome:/tmp/${f##*:}"
done
sql -d WebRadioPerf -i /tmp/schema.sql >/dev/null
sql -d WebRadioPerf -W -i /tmp/gerar.sql >"$saida/carga.txt"
cat "$saida/carga.txt"
sql -d WebRadioPerf -y 0 -i /tmp/medir.sql >"$saida/medicao.txt"
echo "ok: $saida/medicao.txt"

# Concorrência do READPAST: quatro sessões pegam um pedido, uma por segundo, e cada uma segura o seu por 5 s (transação aberta).
# Invariante que o E4-D5 exige: NUNCA o mesmo pedido para duas sessões. Medido: com o EXISTS da mídia na CTE o plano usa um nested loops
# com pré-busca, que trava (U) as linhas seguintes do índice; então uma sessão pode receber 204 mesmo com pedido elegível (o pedido fica
# para a chamada seguinte). Sem o EXISTS, as sessões recebem um pedido cada. O teste confere a invariante e imprime quantas receberam.
cat >/tmp/conc.sql <<'SQL'
SET NOCOUNT ON; SET QUOTED_IDENTIFIER ON; SET XACT_ABORT ON;
DECLARE @agora datetime2(3) = SYSUTCDATETIME(), @instancia uniqueidentifier = NEWID(), @corte datetime2(3) = DATEADD(HOUR, -24, SYSUTCDATETIME());
BEGIN TRAN;
;WITH c AS (
    SELECT TOP (1) p.Id, p.MidiaId, p.EnviadoPlayoutEmUtc, p.EnviadoPlayoutInstancia
    FROM interacao.PedidoMusica AS p WITH (UPDLOCK, READPAST, ROWLOCK, INDEX(IX_PedidoMusica_Playout))
    WHERE p.Status = 2 AND p.MidiaId IS NOT NULL AND p.ModeradoEmUtc >= @corte
      AND (p.EnviadoPlayoutEmUtc IS NULL OR p.EnviadoPlayoutInstancia IS NULL OR p.EnviadoPlayoutInstancia <> @instancia)
      AND EXISTS (SELECT 1 FROM midia.ArquivoMidia AS m WHERE m.Id = p.MidiaId AND m.StatusSanitizacao = 4 AND m.Ativo = 1)
    ORDER BY p.ModeradoEmUtc)
UPDATE c SET EnviadoPlayoutEmUtc = @agora, EnviadoPlayoutInstancia = @instancia OUTPUT inserted.Id AS PedidoId;
WAITFOR DELAY '00:00:05';
ROLLBACK;
SQL
docker cp /tmp/conc.sql "$nome:/tmp/conc.sql"; rm -f /tmp/conc.sql
: >"$saida/concorrencia.txt"
for sessao in A B C D; do
    ( sql -d WebRadioPerf -h -1 -W -i /tmp/conc.sql | grep -E '^[0-9A-F]{8}-' | sed "s/^/$sessao /" >>"$saida/concorrencia.txt" ) &
    sleep 1
done
wait
recebidos="$(wc -l <"$saida/concorrencia.txt")"
repetidos="$(awk '{print $2}' "$saida/concorrencia.txt" | sort | uniq -d | wc -l)"
cat "$saida/concorrencia.txt"
echo "concorrência: $recebidos de 4 sessões receberam um pedido; pedidos repetidos: $repetidos"
[ "$repetidos" = 0 ] && [ "$recebidos" -ge 1 ] || { echo "FALHA: o mesmo pedido foi entregue a duas sessões (ou nenhuma recebeu)" >&2; exit 1; }
