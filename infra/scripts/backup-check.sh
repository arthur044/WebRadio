#!/usr/bin/env bash
# =============================================================================
# WebRadio — infra/scripts/backup-check.sh (E5-A01; 09-epico-5-tarefas.md, E5-D3 e E5-D13)
#
# Teste com SQL Server REAL do backup e da restauração, no cenário que importa: o servidor morre e o banco volta numa INSTÂNCIA NOVA
# (outro contêiner, volumes novos, logins recriados com SID novo), a partir só do .bak.
#
#   A (origem)  : init-db.sql (Production) -> logins.sql -> logins-backup.sql -> schema do 03 -> dados de teste -> BACKUP como svc_backup
#   B (instância nova): init-db.sql -> logins.sql -> logins-backup.sql (logins com SID novo) -> o .bak copiado para o volume dela
#                       -> restore.sql (sa) -> pos-restore.sql (sa) -> confere tudo
#
# Confere: RECOVERY SIMPLE; o dono do volume sql-backup pelo mesmo `chown` do sql-backup-init (root, só CAP_CHOWN, sem rede); o que o
# svc_backup NÃO pode (ler tabela, RESTORE VERIFYONLY, CREATE DATABASE, COMPRESSION na Express); os usuários ÓRFÃOS antes do
# pos-restore.sql e o login da API, do migrator e do relatório abrindo o banco depois; contagem de linhas igual em TODAS as tabelas;
# DBCC CHECKDB limpo; verificar-restauracao.sql; restore com conexão aberta (SINGLE_USER); e .bak corrompido recusado sem tocar no banco.
#
# Entrada (opcional): MSSQL_IMAGEM (padrão: o digest do docker-compose.yml), MSSQL_PID (padrão: Express, o do compose).
# Precisa de docker. Cria 2 contêineres e 3 volumes com o sufixo do PID deste processo e apaga tudo no fim. Senhas descartáveis.
# =============================================================================
set -euo pipefail
export LC_ALL=C

RAIZ="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$RAIZ"
IMAGEM="${MSSQL_IMAGEM:-mcr.microsoft.com/mssql/server@sha256:4402d880dd4c34bfa7d8705e56a86cd6c88da80a1f6bbbe741f999e76264a090}"
PID_SQL="${MSSQL_PID:-Express}"
SUF="$$"
A="atlasbk-a-$SUF"; B="atlasbk-b-$SUF"
VOL_A="atlasbk-bak-a-$SUF"; VOL_B="atlasbk-bak-b-$SUF"
SA="Wr$(openssl rand -hex 12)A1"
APP="App-$(openssl rand -hex 8)"; MIG="Mig-$(openssl rand -hex 8)"; REL="Rel-$(openssl rand -hex 8)"; BKP="Bkp-$(openssl rand -hex 8)"
TMP="$(mktemp -d)"
falhas=0

limpar() {
  docker rm -f "$A" "$B" >/dev/null 2>&1 || true
  docker volume rm -f "$VOL_A" "$VOL_B" >/dev/null 2>&1 || true
  rm -rf "$TMP"
}
trap limpar EXIT

ok() { echo "  ok    $1"; }
falha() { echo "  FALHA $1" >&2; [[ -n "${2:-}" ]] && echo "$2" | sed 's/^/        /' >&2; falhas=$((falhas + 1)); }
confere() { # confere "<descrição>" <comando...>
  local d="$1"; shift
  if "$@" >/dev/null 2>&1; then ok "$d"; else falha "$d"; fi
}

SQLCMD=/opt/mssql-tools18/bin/sqlcmd
# sa no contêiner $1; as senhas dos logins entram por AMBIENTE (o sqlcmd resolve $(VAR) do ambiente), como no db-init.
sa_sql() {
  local c="$1"; shift
  docker exec -i -e SQLCMDPASSWORD="$SA" -e DB_APP_PASSWORD="$APP" -e DB_MIGRATOR_PASSWORD="$MIG" -e DB_RELATORIO_PASSWORD="$REL" \
    -e DB_BACKUP_PASSWORD="$BKP" "$c" "$SQLCMD" -C -N -S localhost -U sa -b -I "$@"
}
# login $2 (senha $3) no contêiner $1
como() {
  local c="$1" u="$2" p="$3"; shift 3
  docker exec -i -e SQLCMDPASSWORD="$p" "$c" "$SQLCMD" -C -N -S localhost -U "$u" -b -I "$@"
}
subir() { # subir <contêiner> <volume do backup>
  docker volume create "$2" >/dev/null
  # O mesmo comando do serviço sql-backup-init do docker-compose.yml: root, cap_drop ALL + CHOWN, sem rede, só o volume.
  docker run --rm --user 0:0 --cap-drop ALL --cap-add CHOWN --security-opt no-new-privileges --network none --read-only \
    -v "$2:/var/opt/mssql/backup" --entrypoint chown "$IMAGEM" 10001:0 /var/opt/mssql/backup
  docker run -d --name "$1" -e ACCEPT_EULA=Y -e MSSQL_PID="$PID_SQL" -e MSSQL_SA_PASSWORD="$SA" \
    -v "$2:/var/opt/mssql/backup" "$IMAGEM" >/dev/null
}
esperar() {
  for _ in $(seq 1 60); do
    if sa_sql "$1" -Q "SELECT 1" >/dev/null 2>&1; then return 0; fi
    sleep 2
  done
  echo "FALHA: o SQL Server de $1 não subiu." >&2; docker logs --tail 20 "$1" >&2; exit 1
}
preparar() { # init-db, logins e logins-backup num contêiner novo (o que o db-init faz)
  sa_sql "$1" -v Ambiente=Production -i /dev/stdin < infra/sql/init-db.sql > "$TMP/prep-$1.log" 2>&1 || { cat "$TMP/prep-$1.log"; exit 1; }
  sa_sql "$1" -i /dev/stdin < infra/sql/logins.sql >> "$TMP/prep-$1.log" 2>&1 || { cat "$TMP/prep-$1.log"; exit 1; }
  sa_sql "$1" -i /dev/stdin < infra/sql/logins-backup.sql >> "$TMP/prep-$1.log" 2>&1 || { cat "$TMP/prep-$1.log"; exit 1; }
}
# Contagem EXATA de linhas de todas as tabelas do WebRadio, uma por linha: "schema.tabela|n".
contagens() {
  sa_sql "$1" -d WebRadio -h -1 -W -s '|' -Q "SET NOCOUNT ON;
    DECLARE @q nvarchar(max) = (SELECT STRING_AGG(CAST(N'SELECT N''' + s.name + N'.' + t.name + N''' AS t, COUNT_BIG(*) AS n FROM ' + QUOTENAME(s.name) + N'.' + QUOTENAME(t.name) AS nvarchar(max)), N' UNION ALL ')
                                FROM sys.tables AS t JOIN sys.schemas AS s ON s.schema_id = t.schema_id);
    EXEC (N'SELECT t, n FROM (' + @q + N') AS x ORDER BY t');" | grep -v '^$' | sort
}

echo "== 1. instância A (origem): $PID_SQL, imagem $IMAGEM"
subir "$A" "$VOL_A"
esperar "$A"
confere "volume do backup com dono 10001 depois do chown (mesmo comando do sql-backup-init)" \
  bash -c "[[ \"\$(docker run --rm --user 10001:0 -v '$VOL_A:/v' --entrypoint stat '$IMAGEM' -c %u /v)\" == 10001 ]]"
preparar "$A"
recovery="$(sa_sql "$A" -h -1 -W -Q "SET NOCOUNT ON; SELECT recovery_model_desc FROM sys.databases WHERE name = N'WebRadio'" | tr -d '[:space:]')"
[[ "$recovery" == SIMPLE ]] && ok "WebRadio em RECOVERY SIMPLE com Ambiente=Production (o init-db.sql não põe mais FULL)" || falha "recovery model = '$recovery' (esperado SIMPLE)"
sa_sql "$A" -d WebRadio -i /dev/stdin < docs/specs/03-schema-sqlserver.sql > "$TMP/03.log" 2>&1 || { cat "$TMP/03.log"; exit 1; }
sa_sql "$A" -d WebRadio -Q "
SET NOCOUNT ON;
DECLARE @u uniqueidentifier = NEWID();
INSERT seg.Usuario (Id, Nome, Email, EmailNormalizado, SenhaHash, Role) VALUES (@u, N'Dono', N'dono@teste.local', N'dono@teste.local', N'hash', 3);
INSERT seg.Usuario (Id, Nome, Email, EmailNormalizado, SenhaHash, Role)
  SELECT NEWID(), N'U' + CONVERT(nvarchar(10), n), N'u' + CONVERT(nvarchar(10), n) + N'@teste.local', N'u' + CONVERT(nvarchar(10), n) + N'@teste.local', N'hash', 1
  FROM (SELECT TOP (150) ROW_NUMBER() OVER (ORDER BY (SELECT 1)) AS n FROM sys.all_objects) AS x;
INSERT midia.ArquivoMidia (NomeOriginal, TipoMidia, Bucket, ChaveStorage, MimeTypeDeclarado, TamanhoBytes, EnviadoPorUsuarioId, UploadExpiraEmUtc)
  SELECT N'faixa' + CONVERT(nvarchar(10), n) + N'.mp3', 1, 'quarentena', 'quarentena/' + CONVERT(varchar(40), NEWID()), 'audio/mpeg', 1000 + n, @u, SYSUTCDATETIME()
  FROM (SELECT TOP (400) ROW_NUMBER() OVER (ORDER BY (SELECT 1)) AS n FROM sys.all_objects) AS x;
INSERT infra.EventoOutbox (Tipo, Payload) SELECT TOP (60) 'TesteBackup', N'{}' FROM sys.all_objects;" > "$TMP/seed.log" 2>&1 || { cat "$TMP/seed.log"; falha "não consegui semear dados de teste"; exit 1; }
contagens "$A" > "$TMP/contagem-a.txt"
grep -q '^midia.ArquivoMidia|400$' "$TMP/contagem-a.txt" && ok "dados de teste na origem ($(grep -c . "$TMP/contagem-a.txt") tabelas contadas)" || falha "semente não gravou 400 mídias" "$(cat "$TMP/contagem-a.txt")"

echo "== 2. backup.sql como svc_backup (db_backupoperator)"
saida="$(como "$A" svc_backup "$BKP" -i /dev/stdin < infra/sql/backup.sql 2>&1)" && echo "$saida" | grep -q BACKUP_SQL_OK && ok "BACKUP WITH CHECKSUM, INIT pelo svc_backup, num volume novo (sem COMPRESSION)" || falha "backup.sql falhou" "$saida"
confere "webradio.bak existe no volume sql-backup" docker exec "$A" test -s /var/opt/mssql/backup/webradio.bak
echo "-- o que o svc_backup NÃO pode"
confere "svc_backup não lê tabela (SELECT negado)" bash -c "! docker exec -e SQLCMDPASSWORD='$BKP' '$A' $SQLCMD -C -N -S localhost -U svc_backup -b -I -d WebRadio -Q 'SELECT COUNT(*) FROM seg.Usuario'"
confere "svc_backup não faz RESTORE VERIFYONLY (exige CREATE DATABASE)" bash -c "! docker exec -e SQLCMDPASSWORD='$BKP' '$A' $SQLCMD -C -N -S localhost -U svc_backup -b -I -Q \"RESTORE VERIFYONLY FROM DISK = N'/var/opt/mssql/backup/webradio.bak' WITH CHECKSUM\""
confere "svc_backup não cria banco" bash -c "! docker exec -e SQLCMDPASSWORD='$BKP' '$A' $SQLCMD -C -N -S localhost -U svc_backup -b -I -Q 'CREATE DATABASE X_nao_pode'"
confere "svc_backup não sobrescreve o banco (RESTORE ... REPLACE negado)" bash -c "! docker exec -e SQLCMDPASSWORD='$BKP' '$A' $SQLCMD -C -N -S localhost -U svc_backup -b -I -Q \"RESTORE DATABASE WebRadio FROM DISK = N'/var/opt/mssql/backup/webradio.bak' WITH REPLACE\""
confere "svc_backup não é membro de nenhum papel de servidor" bash -c "[[ \"\$(docker exec -e SQLCMDPASSWORD='$SA' '$A' $SQLCMD -C -N -S localhost -U sa -h -1 -W -Q \"SET NOCOUNT ON; SELECT COUNT(*) FROM sys.server_role_members m JOIN sys.server_principals p ON p.principal_id = m.member_principal_id WHERE p.name = N'svc_backup'\" | tr -d '[:space:]')\" == 0 ]]"
if [[ "$PID_SQL" == Express ]]; then
  msg="$(como "$A" svc_backup "$BKP" -Q "BACKUP DATABASE WebRadio TO DISK = N'/var/opt/mssql/backup/c.bak' WITH COMPRESSION, CHECKSUM, INIT" 2>&1 || true)"
  echo "$msg" | grep -q "not supported on Express" && ok "COMPRESSION é recusada na Express (por isso o backup.sql não usa)" || falha "esperava a recusa de COMPRESSION na Express" "$msg"
fi
echo "-- verificar-restauracao.sql na origem (banco temporário, CHECKDB, sem tocar no WebRadio)"
saida="$(sa_sql "$A" -i /dev/stdin < infra/sql/verificar-restauracao.sql 2>&1)" && echo "$saida" | grep -q VERIFICACAO_OK && ok "verificar-restauracao.sql: restore com MOVE, DBCC CHECKDB limpo e banco temporário apagado" || falha "verificar-restauracao.sql falhou" "$saida"
confere "WebRadio_verif não sobrou" bash -c "[[ \"\$(docker exec -e SQLCMDPASSWORD='$SA' '$A' $SQLCMD -C -N -S localhost -U sa -h -1 -W -Q \"SET NOCOUNT ON; SELECT COUNT(*) FROM sys.databases WHERE name = N'WebRadio_verif'\" | tr -d '[:space:]')\" == 0 ]]"

echo "== 3. instância B (nova): volumes novos e logins recriados com SID novo"
subir "$B" "$VOL_B"
esperar "$B"
preparar "$B"
# O .bak chega ao volume da B como chegaria do restic (baixado para o volume), com o dono do mssql.
docker run --rm --user 10001:0 -v "$VOL_A:/de:ro" -v "$VOL_B:/para" --entrypoint cp "$IMAGEM" /de/webradio.bak /para/webradio.bak
confere ".bak no volume da B" docker exec "$B" test -s /var/opt/mssql/backup/webradio.bak
sa_sql "$B" -h -1 -W -Q "SET NOCOUNT ON; SELECT COUNT(*) FROM WebRadio.sys.tables" | tr -d '[:space:]' | grep -qx 0 && ok "B antes do restore: WebRadio existe e está vazio (o que o db-init deixa)" || falha "o WebRadio da B não estava vazio antes do restore"

echo "== 4. restore.sql (sa) e os usuários órfãos"
saida="$(sa_sql "$B" -i /dev/stdin < infra/sql/restore.sql 2>&1)" && echo "$saida" | grep -q RESTORE_SQL_OK && ok "restore.sql: VERIFYONLY, SINGLE_USER, RESTORE WITH CHECKSUM, REPLACE, MULTI_USER" || falha "restore.sql falhou" "$saida"
confere "login da API NÃO abre o banco antes do pos-restore (usuário órfão: SID do servidor antigo)" \
  bash -c "! docker exec -e SQLCMDPASSWORD='$APP' '$B' $SQLCMD -C -N -S localhost -U webradio_app -b -I -d WebRadio -Q 'SELECT 1'"
saida="$(sa_sql "$B" -i /dev/stdin < infra/sql/pos-restore.sql 2>&1)" && echo "$saida" | grep -q POS_RESTORE_OK && ok "pos-restore.sql religou os usuários ($(echo "$saida" | grep -c 'religado') usuários)" || falha "pos-restore.sql falhou" "$saida"
n_religados="$(echo "$saida" | grep -c 'religado' || true)"
[[ "$n_religados" -eq 4 ]] && ok "os quatro usuários (migrator, app, relatorio, svc_backup) estavam órfãos e foram religados" || falha "esperava 4 usuários religados, vieram $n_religados" "$saida"
for u in "webradio_app:$APP" "webradio_migrator:$MIG" "webradio_relatorio:$REL" "svc_backup:$BKP"; do
  confere "login ${u%%:*} abre o WebRadio depois do pos-restore" como "$B" "${u%%:*}" "${u#*:}" -d WebRadio -Q "SELECT 1"
done
confere "API (webradio_app) lê a tabela restaurada" bash -c "[[ \"\$(docker exec -e SQLCMDPASSWORD='$APP' '$B' $SQLCMD -C -N -S localhost -U webradio_app -b -I -d WebRadio -h -1 -W -Q 'SET NOCOUNT ON; SELECT COUNT(*) FROM seg.Usuario' | tr -d '[:space:]')\" == 151 ]]"
confere "pos-restore.sql é idempotente (segunda execução sem órfãos)" bash -c "docker exec -i -e SQLCMDPASSWORD='$SA' '$B' $SQLCMD -C -N -S localhost -U sa -b -I -i /dev/stdin < infra/sql/pos-restore.sql | grep -q POS_RESTORE_OK"

echo "== 5. o que voltou"
contagens "$B" > "$TMP/contagem-b.txt"
if diff -u "$TMP/contagem-a.txt" "$TMP/contagem-b.txt" > "$TMP/contagem.diff"; then ok "contagem de linhas igual em todas as tabelas ($(grep -c . "$TMP/contagem-b.txt"))"; else falha "contagem diferente entre origem e restauração" "$(cat "$TMP/contagem.diff")"; fi
saida="$(sa_sql "$B" -Q "DBCC CHECKDB (WebRadio) WITH NO_INFOMSGS, ALL_ERRORMSGS" 2>&1)" && [[ -z "$(echo "$saida" | tr -d '[:space:]')" ]] && ok "DBCC CHECKDB limpo no banco restaurado" || falha "DBCC CHECKDB com problema" "$saida"
estado="$(sa_sql "$B" -h -1 -W -s '|' -Q "SET NOCOUNT ON; SELECT recovery_model_desc, user_access_desc, state_desc FROM sys.databases WHERE name = N'WebRadio'" | tr -d '[:space:]')"
[[ "$estado" == "SIMPLE|MULTI_USER|ONLINE" ]] && ok "banco restaurado: SIMPLE, MULTI_USER, ONLINE" || falha "estado do banco = $estado"
saida="$(como "$B" svc_backup "$BKP" -i /dev/stdin < infra/sql/backup.sql 2>&1)" && echo "$saida" | grep -q BACKUP_SQL_OK && ok "o svc_backup da B (login novo) já faz o próximo backup" || falha "backup na B falhou" "$saida"

echo "== 6. restore com conexão aberta e idempotência"
docker exec -d -e SQLCMDPASSWORD="$APP" "$B" "$SQLCMD" -C -N -S localhost -U webradio_app -d WebRadio -Q "WAITFOR DELAY '00:01:00'" >/dev/null 2>&1 || true
sleep 3
saida="$(sa_sql "$B" -i /dev/stdin < infra/sql/restore.sql 2>&1)" && echo "$saida" | grep -q RESTORE_SQL_OK && ok "restore.sql com uma conexão aberta no banco (SINGLE_USER WITH ROLLBACK IMMEDIATE)" || falha "restore.sql com conexão aberta falhou" "$saida"
sa_sql "$B" -i /dev/stdin < infra/sql/pos-restore.sql >/dev/null 2>&1 && ok "pos-restore.sql de novo depois do segundo restore" || falha "pos-restore.sql do segundo restore falhou"

echo "== 7. .bak corrompido: recusado antes de tocar no banco"
contagens "$B" > "$TMP/antes.txt"
docker run --rm --user 10001:0 -v "$VOL_B:/v" --entrypoint bash "$IMAGEM" -c 'head -c 262144 /dev/urandom | dd of=/v/webradio.bak bs=1 seek=262144 conv=notrunc status=none'
if saida="$(sa_sql "$B" -i /dev/stdin < infra/sql/restore.sql 2>&1)"; then
  falha "restore.sql aceitou um .bak corrompido" "$saida"
elif echo "$saida" | grep -q "VERIFY DATABASE is terminating abnormally" && ! echo "$saida" | grep -qi "permission\|operating system error"; then
  ok "restore.sql recusa o .bak corrompido no RESTORE VERIFYONLY ... WITH CHECKSUM ($(echo "$saida" | grep -m1 '^Msg [0-9]*,' | cut -c1-60))"
else
  falha "o restore falhou, mas não foi o VERIFYONLY que recusou o .bak" "$saida"
fi
contagens "$B" > "$TMP/depois.txt"
diff -q "$TMP/antes.txt" "$TMP/depois.txt" >/dev/null && ok "o WebRadio ficou intacto depois da recusa (mesma contagem)" || falha "o banco mudou depois de um restore recusado"
estado="$(sa_sql "$B" -h -1 -W -s '|' -Q "SET NOCOUNT ON; SELECT user_access_desc, state_desc FROM sys.databases WHERE name = N'WebRadio'" | tr -d '[:space:]')"
[[ "$estado" == "MULTI_USER|ONLINE" ]] && ok "e continua MULTI_USER e ONLINE" || falha "estado depois da recusa = $estado"

echo
if [[ "$falhas" -eq 0 ]]; then
  echo "backup-check: backup como svc_backup, restauração numa instância nova, usuários religados, contagens iguais e CHECKDB limpo."
  exit 0
fi
echo "backup-check: $falhas verificação(ões) falharam." >&2
exit 1
