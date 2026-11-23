#!/usr/bin/env bash
# =============================================================================
# WebRadio — infra/scripts/smoke-backup.sh (E5-F03, DoD 4 e 5 do Épico 5, no stack REAL do Compose)
#
# Roda contra a base de PRODUÇÃO já no ar (job `compose-producao` do CI), DEPOIS dos smokes da borda. Complementa o backup-restore-check.sh (servidor novo, falhas por etapa,
# storage externo): aqui o `backup` e o `restaurar` rodam como o operador os roda, com os segredos, as redes e os volumes do docker-compose.yml.
#   1. marcadores: uma tabela dbo.smoke_backup_marker (1 linha) no WebRadio e um objeto midia/2026/ci-marker.bin no MinIO (SHA-256 anotado)
#   2. `backup` (profile backup): BACKUP_OK, e BACKUP_EXTERNO_NAO_CONFIGURADO como AVISO (os segredos backup_s3_* nascem vazios), código 0
#   3. estraga: apaga a tabela e o objeto
#   4. `restaurar` COM a api no ar: aborta (21), o banco não é tocado (a trava do E5-D13)
#   5. para a api e o worker; `restaurar` (confirmando o nome do banco): RESTAURACAO_OK
#   6. confere: a tabela e o objeto (mesmo SHA-256) voltaram, nenhum usuário órfão; sobe tudo de novo (migrator, api, worker) e espera healthy
# O stack é descartável (o job derruba com down -v). Nunca imprime segredo.
# =============================================================================
set -euo pipefail
export LC_ALL=C

RAIZ="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$RAIZ"
C=(docker compose -f docker-compose.yml -f docker-compose.smoke.yml -f docker-compose.ci-prod.yml)
falhas=0
ok()    { printf '  ok    %s\n' "$1"; }
falha() { printf '  FALHA %s\n' "$1"; falhas=$((falhas + 1)); }
esperar() { if [[ "$2" == "$3" ]]; then ok "$1 ($3)"; else falha "$1: esperado $2, veio $3"; fi; }

# O sqlcmd do próprio SQL Server (com o `sa`, lido do arquivo DENTRO do contêiner) e o mc do restaurar (credencial root, lida de arquivo dentro do contêiner).
sa_sql() { "${C[@]}" exec -T sqlserver bash -c 'SQLCMDPASSWORD="$(cat /run/secrets/mssql_sa_password)" /opt/mssql-tools18/bin/sqlcmd -C -N -S localhost -U sa -b -h -1 -W "$@"' _ "$@"; }
mc_raiz() { # mc no serviço restaurar, só com o entrypoint trocado (a imagem e os segredos são os do restaurar)
  "${C[@]}" --profile restore run --rm -T --no-deps --entrypoint bash restaurar -c 'export MC_HOST_x="http://$(cat /run/secrets/minio_root_user):$(cat /run/secrets/minio_root_password)@minio:9000"; "$@"' _ mc "$@"
}

echo "== 1. marcadores: uma tabela com uma linha e um objeto na biblioteca"
sa_sql -d WebRadio -Q "IF OBJECT_ID(N'dbo.smoke_backup_marker') IS NOT NULL DROP TABLE dbo.smoke_backup_marker; CREATE TABLE dbo.smoke_backup_marker (v int NOT NULL); INSERT dbo.smoke_backup_marker VALUES (4242);" >/dev/null
esperar "tabela marcadora" 4242 "$(sa_sql -d WebRadio -Q 'SET NOCOUNT ON; SELECT v FROM dbo.smoke_backup_marker' | tr -d '[:space:]')"
# O objeto nasce DENTRO do contêiner (aleatório) e o SHA-256 volta pela saída; nada passa por arquivo do runner.
SHA_ANTES="$("${C[@]}" --profile restore run --rm -T --no-deps --entrypoint bash restaurar -c 'export MC_HOST_x="http://$(cat /run/secrets/minio_root_user):$(cat /run/secrets/minio_root_password)@minio:9000"; head -c 150000 /dev/urandom > /tmp/m.bin; sha256sum /tmp/m.bin | cut -d" " -f1; mc cp /tmp/m.bin x/midia/2026/ci-marker.bin >/dev/null 2>&1' | tail -1 | tr -d '\r')"
[[ "$SHA_ANTES" =~ ^[0-9a-f]{64}$ ]] && ok "objeto marcador gravado em midia (sha256 anotado)" || { falha "não consegui gravar o objeto marcador"; exit 1; }

echo "== 2. backup (docker compose --profile backup run --rm backup)"
set +e; SAIDA="$("${C[@]}" --profile backup run --rm -T backup 2>&1)"; RC=$?; set -e
printf '%s\n' "$SAIDA" | sed 's/^/        /'
esperar "código de saída do backup" 0 "$RC"
grep -q 'BACKUP_OK' <<<"$SAIDA" && ok "BACKUP_OK" || falha "sem BACKUP_OK"
grep -q 'BACKUP_EXTERNO_NAO_CONFIGURADO' <<<"$SAIDA" && ok "BACKUP_EXTERNO_NAO_CONFIGURADO (aviso: os segredos backup_s3_* nascem vazios)" || falha "sem o aviso de cópia externa não configurada"
grep -q 'BACKUP_FALHOU' <<<"$SAIDA" && falha "BACKUP_FALHOU no log" || ok "sem BACKUP_FALHOU"

echo "== 3. estraga: apaga a tabela e o objeto"
sa_sql -d WebRadio -Q "DROP TABLE dbo.smoke_backup_marker" >/dev/null
mc_raiz rm x/midia/2026/ci-marker.bin >/dev/null 2>&1 || true
[[ -z "$(mc_raiz ls x/midia/2026/ 2>/dev/null | grep ci-marker || true)" ]] && ok "objeto apagado" || falha "o objeto marcador não foi apagado"

echo "== 4. a trava: restaurar COM a api e o worker no ar aborta (21) sem tocar no banco"
set +e; SAIDA="$("${C[@]}" --profile restore run --rm -T -e RESTAURAR_CONFIRMA=WebRadio restaurar 2>&1)"; RC=$?; set -e
esperar "código de saída com a api no ar" 21 "$RC"
grep -q 'RESTAURACAO_ABORTADA motivo=api_ou_worker_no_ar' <<<"$SAIDA" && ok "RESTAURACAO_ABORTADA motivo=api_ou_worker_no_ar" || { falha "a trava não acusou a api no ar"; printf '%s\n' "$SAIDA" | sed 's/^/        /'; }

echo "== 5. para a api e o worker e restaura (a confirmação do nome do banco vem por RESTAURAR_CONFIRMA, sem terminal)"
"${C[@]}" stop api worker >/dev/null
# As sessões da api e do worker no SQL Server levam alguns segundos para sumir.
for _ in $(seq 1 30); do
  n="$(sa_sql -Q "SET NOCOUNT ON; SELECT COUNT(*) FROM sys.dm_exec_sessions WHERE program_name LIKE N'WebRadio.Api%' OR program_name LIKE N'WebRadio.Worker%'" | tr -d '[:space:]')"
  [[ "$n" == "0" ]] && break
  sleep 2
done
set +e; SAIDA="$("${C[@]}" --profile restore run --rm -T -e RESTAURAR_CONFIRMA=WebRadio restaurar 2>&1)"; RC=$?; set -e
printf '%s\n' "$SAIDA" | sed 's/^/        /'
esperar "código de saída do restaurar" 0 "$RC"
grep -q 'RESTAURACAO_FONTE fonte=local' <<<"$SAIDA" && ok "restaurou do repositório LOCAL (sem cópia externa configurada)" || falha "a fonte não foi o repositório local"
grep -q 'RESTAURACAO_OK' <<<"$SAIDA" && ok "RESTAURACAO_OK" || falha "sem RESTAURACAO_OK"

echo "== 6. o que voltou"
esperar "tabela marcadora de volta" 4242 "$(sa_sql -d WebRadio -Q 'SET NOCOUNT ON; SELECT v FROM dbo.smoke_backup_marker' | tr -d '[:space:]')"
# O SHA-256 é calculado DENTRO do contêiner (como o do "antes"): o stdout do `docker compose run` pode trazer linhas do próprio Compose, e os bytes do objeto passariam misturados a elas.
SHA_DEPOIS="$("${C[@]}" --profile restore run --rm -T --no-deps --entrypoint bash restaurar -c 'export MC_HOST_x="http://$(cat /run/secrets/minio_root_user):$(cat /run/secrets/minio_root_password)@minio:9000"; mc cat x/midia/2026/ci-marker.bin 2>/dev/null | sha256sum | cut -d" " -f1' 2>/dev/null | tail -1 | tr -d '\r')"
esperar "objeto marcador de volta, com o mesmo SHA-256" "$SHA_ANTES" "$SHA_DEPOIS"
if [[ "$SHA_ANTES" != "$SHA_DEPOIS" ]]; then
  echo "  (diagnóstico) objetos em midia/2026/ depois da restauração:"
  mc_raiz ls --recursive x/midia/2026/ 2>&1 | sed 's/^/        /'
fi
esperar "usuários órfãos no WebRadio" 0 "$(sa_sql -d WebRadio -Q "SET NOCOUNT ON; SELECT COUNT(*) FROM sys.database_principals dp WHERE dp.type = 'S' AND dp.authentication_type = 1 AND dp.name NOT IN (N'dbo', N'guest', N'INFORMATION_SCHEMA', N'sys') AND NOT EXISTS (SELECT 1 FROM sys.server_principals sp WHERE sp.sid = dp.sid)" | tr -d '[:space:]')"
sa_sql -d WebRadio -Q "DROP TABLE dbo.smoke_backup_marker" >/dev/null
echo "-- sobe o migrator, a api e o worker de novo (o banco restaurado)"
"${C[@]}" up -d --wait --wait-timeout 240 >/dev/null 2>&1 && ok "migrator, api e worker no ar e healthy depois da restauração" || falha "o stack não voltou healthy depois da restauração"

echo
if [[ $falhas -gt 0 ]]; then echo "smoke-backup: $falhas falha(s)."; exit 1; fi
echo "smoke-backup: tudo ok."
