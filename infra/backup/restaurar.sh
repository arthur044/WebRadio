#!/usr/bin/env bash
# =============================================================================
# WebRadio — infra/backup/restaurar.sh (E5-F03, E5-D3, E5-D13). One-shot manual, `docker compose --profile restore run --rm restaurar`, feito pelo OPERADOR.
# É o ÚNICO lugar (com o db-init e o minio-init) que recebe o `sa` e a credencial root do MinIO: o RESTORE exige dbcreator e a volta da mídia escreve nos buckets.
#
# Ordem (E5-D13). O Compose já rodou o db-init (logins) e o minio-init (buckets) antes deste contêiner (depends_on):
#   0. PRÉ-VOO: todos os segredos (sa, root do MinIO, restic) conferidos ANTES de tocar em qualquer coisa. TRAVA: aborta se a api ou o worker estiverem com sessão aberta no SQL Server (21) ou se um backup estiver rodando (23, o mesmo flock do backup.sh); pede o nome do banco antes do REPLACE
#   1. restic restore do .bak e do espelho da mídia (repositório local; se não existir, o externo) para /backup/restauracao
#   2. restore.sql (sa): VERIFYONLY, SINGLE_USER, RESTORE ... WITH CHECKSUM, REPLACE, MULTI_USER
#   3. pos-restore.sql (sa): religa cada usuário do banco ao login (num servidor novo os logins nascem com outro SID)
#   4. mc mirror de volta para os buckets midia e publico (banco ANTES da mídia: um objeto que entrou entre o BACKUP e o espelho vira órfão e o Worker o limpa)
#   Depois, FORA daqui: docker compose up -d --wait  (migrator, api e worker, nessa ordem).
# Variáveis: RESTAURAR_FONTE (local | externo; padrão: local se o repositório local existe, senão externo), RESTAURAR_SNAPSHOT (padrão: latest),
#   RESTAURAR_CONFIRMA (o nome do banco, para rodar sem terminal; no terminal o script PERGUNTA), DB_SERVER, MINIO_ENDPOINT, BACKUP_S3_ENDPOINT, BACKUP_S3_BUCKET.
# Segredos de arquivo, nunca em argv: mssql_sa_password (SQLCMDPASSWORD), restic_senha (--password-file), minio_root_* (variável do filho), backup_s3_*.
# Nunca imprime segredo; a saída dos programas vai para um arquivo e só um token de motivo aparece (BACKUP_DEBUG=1 mostra a saída crua no stderr).
# =============================================================================
set -uo pipefail
export LC_ALL=C

S="${SEGREDOS_DIR:-/run/secrets}"
BAK_DIR="${BAK_DIR:-/var/opt/mssql/backup}"
LOCAL="${BACKUP_LOCAL:-/backup}"
SQL_DIR="${SQL_DIR:-/sql}"
DB_SERVER="${DB_SERVER:-sqlserver}"
MINIO_ENDPOINT="${MINIO_ENDPOINT:-minio:9000}"
REPO_LOCAL="$LOCAL/repo"
TRABALHO="$LOCAL/restauracao"
SNAPSHOT="${RESTAURAR_SNAPSHOT:-latest}"
export RESTIC_CACHE_DIR="$LOCAL/cache-restic" HOME="${HOME:-/tmp}" MC_CONFIG_DIR="${MC_CONFIG_DIR:-/tmp/.mc}"
SAIDA="$(mktemp)"
trap 'rm -f "$SAIDA"; rm -rf "$TRABALHO"' EXIT

log() { printf '%s %s\n' "$(date -u +%FT%TZ)" "$*"; }
RC_ULTIMO=0
motivo_de() {
  if [[ "$RC_ULTIMO" == "124" || "$RC_ULTIMO" == "143" ]]; then echo tempo_esgotado; return; fi
  if grep -qiE 'login failed|access ?denied|signaturedoesnotmatch|signature we calculated|does not match the signature|invalidaccesskeyid|forbidden|wrong password|no key found|permission denied' "$SAIDA"; then echo credencial_ou_permissao_recusada
  elif grep -qiE 'no space left|disk full|there is not enough space' "$SAIDA"; then echo sem_espaco
  elif grep -qiE 'connection refused|no such host|dial tcp|timeout|timed out|could not open a connection|network is unreachable' "$SAIDA"; then echo sem_conexao
  elif grep -qiE 'nosuchbucket|repository does not exist|unable to open config|is there a repository' "$SAIDA"; then echo destino_inexistente
  elif grep -qiE 'msg 3189|msg 3241|msg 3043|checksum|corrupt|damaged|3013|3203' "$SAIDA"; then echo arquivo_de_backup_invalido
  else echo desconhecido; fi
}
falhar() { # falhar <etapa> <código>
  log "RESTAURACAO_FALHOU etapa=$1 motivo=$(motivo_de)"
  if [[ "${BACKUP_DEBUG:-}" == "1" ]]; then { echo "--- saída crua (BACKUP_DEBUG=1; pode ter host, caminho e trecho de segredo) ---"; cat "$SAIDA"; } >&2; fi
  exit "$2"
}
abortar() { log "RESTAURACAO_ABORTADA motivo=$1"; exit "${2:-2}"; }
rodar() { "$@" >"$SAIDA" 2>&1; RC_ULTIMO=$?; return "$RC_ULTIMO"; }
# Todos os segredos que a restauração usa, conferidos AQUI, no shell principal e ANTES de tocar em qualquer coisa: dentro de um $(...) o exit sairia só do subshell e o log viraria o "valor".
exigir() { local n; for n in "$@"; do [[ -r "$S/$n" && -s "$S/$n" ]] || abortar "segredo_ausente_ou_vazio:$n"; done; }
ler() { cat "$S/$1"; } # só depois do exigir
exigir restic_senha mssql_sa_password minio_root_user minio_root_password

export RESTIC_PASSWORD_FILE="$S/restic_senha"
# SÓ https:// no storage externo (BACKUP_PERMITIR_HTTP=1 é só do teste).
if [[ -n "${BACKUP_S3_ENDPOINT:-}" && "$BACKUP_S3_ENDPOINT" != https://* && "${BACKUP_PERMITIR_HTTP:-}" != "1" ]]; then abortar endpoint_s3_precisa_de_https; fi
sql_sa() { SQLCMDPASSWORD="$(ler mssql_sa_password)" sqlcmd -b -C -N -I -S "$DB_SERVER" -U sa -l 30 -H WebRadio.Restaurar "$@"; }

# --------------------------------------------------------------------- 0. trava
log "RESTAURACAO_INICIO"
rodar sql_sa -h -1 -W -Q "SET NOCOUNT ON; SELECT COUNT(*) FROM sys.dm_exec_sessions WHERE program_name LIKE N'WebRadio.Api%' OR program_name LIKE N'WebRadio.Worker%' OR program_name LIKE N'WebRadio.Migrator%'" || falhar trava 20
SESSOES="$(tr -d '[:space:]' <"$SAIDA")"
[[ "$SESSOES" =~ ^[0-9]+$ ]] || { echo "resposta inesperada" >"$SAIDA"; falhar trava 20; }
if [[ "$SESSOES" != "0" ]]; then
  abortar "api_ou_worker_no_ar sessoes=$SESSOES (rode: docker compose stop api worker; e o migrator não pode estar rodando)" 21
fi
# O mesmo flock do backup.sh (o volume backup-local é compartilhado): um backup em andamento (o timer das 03:30) escreveria o MESMO webradio.bak e o espelho que esta restauração lê.
mkdir -p "$LOCAL" 2>/dev/null
exec 9>"$LOCAL/.backup.lock"
flock -n 9 || abortar "backup_em_andamento (espere o backup terminar: o timer roda às 03:30)" 23

# ------------------------------------------------------------- 1. baixar do restic
externo_configurado() { [[ -n "${BACKUP_S3_ENDPOINT:-}" && -n "${BACKUP_S3_BUCKET:-}" && -s "$S/backup_s3_access_key" && -s "$S/backup_s3_secret_key" ]]; }
restic_externo() {
  AWS_ACCESS_KEY_ID="$(cat "$S/backup_s3_access_key")" AWS_SECRET_ACCESS_KEY="$(cat "$S/backup_s3_secret_key")" \
    timeout "${BACKUP_TIMEOUT_EXTERNO_S:-7200}" restic -r "s3:${BACKUP_S3_ENDPOINT%/}/${BACKUP_S3_BUCKET}" "$@"
}
FONTE="${RESTAURAR_FONTE:-}"
if [[ -z "$FONTE" ]]; then
  if restic -r "$REPO_LOCAL" cat config >/dev/null 2>&1; then FONTE=local; else FONTE=externo; fi
fi
case "$FONTE" in
  local) restic_fonte() { restic -r "$REPO_LOCAL" "$@"; } ;;
  externo) externo_configurado || abortar "fonte_externa_nao_configurada"; restic_fonte() { restic_externo "$@"; } ;;
  *) abortar "RESTAURAR_FONTE_invalida:$FONTE" ;;
esac
log "RESTAURACAO_FONTE fonte=$FONTE snapshot=$SNAPSHOT"
# Mostra QUAL snapshot vai voltar (id, data, host) ANTES de pedir o nome do banco: num servidor novo, um backup rodando antes da restauração pode ter criado um "latest" de banco VAZIO.
if [[ "$SNAPSHOT" == "latest" ]]; then rodar restic_fonte snapshots --compact --latest 1; else rodar restic_fonte snapshots --compact "$SNAPSHOT"; fi || falhar baixar 30
echo "Snapshot que será restaurado (id, data, host, caminhos):" >&2
grep -E '^[0-9a-f]{8} ' "$SAIDA" >&2 || true
if [[ -n "${RESTAURAR_CONFIRMA:-}" ]]; then CONFIRMA="$RESTAURAR_CONFIRMA"
elif [[ -t 0 ]]; then
  echo "ATENÇÃO: o banco WebRadio atual será SUBSTITUÍDO pelo backup. Digite o nome do banco para continuar:" >&2
  read -r CONFIRMA
else abortar "sem_terminal_e_sem_RESTAURAR_CONFIRMA"; fi
[[ "$CONFIRMA" == "WebRadio" ]] || abortar "confirmacao_diferente_do_nome_do_banco" 22
rm -rf "$TRABALHO"; mkdir -p "$TRABALHO" || abortar "pasta_de_trabalho_nao_gravavel"
# O snapshot guarda os caminhos absolutos do backup.sh: o .bak e o espelho.
rodar restic_fonte restore "$SNAPSHOT" --target "$TRABALHO" --include "$BAK_DIR/webradio.bak" --include "$LOCAL/espelho" || falhar baixar 30
[[ -s "$TRABALHO$BAK_DIR/webradio.bak" ]] || { echo "o snapshot não tem o webradio.bak" >"$SAIDA"; falhar baixar 30; }
cp -f "$TRABALHO$BAK_DIR/webradio.bak" "$BAK_DIR/webradio.bak" || { echo "no space left or permission denied" >"$SAIDA"; falhar baixar 30; }
log "RESTAURACAO_BAIXADO bak_bytes=$(stat -c %s "$BAK_DIR/webradio.bak")"

# ----------------------------------------------------------- 2 e 3. banco (sa)
rodar sql_sa -i "$SQL_DIR/restore.sql" || falhar restore 40
log "RESTAURACAO_SQL_OK"
rodar sql_sa -i "$SQL_DIR/pos-restore.sql" || falhar pos_restore 41
log "RESTAURACAO_POS_RESTORE_OK"

# ------------------------------------------------------------ 4. mídia de volta
MC_HOST_destino="http://$(ler minio_root_user):$(ler minio_root_password)@${MINIO_ENDPOINT}"; export MC_HOST_destino
for bucket in midia publico; do
  if [[ -d "$TRABALHO$LOCAL/espelho/$bucket" ]]; then
    rodar mc mirror --overwrite "$TRABALHO$LOCAL/espelho/$bucket" "destino/$bucket" || falhar midia 50
  fi
done
unset MC_HOST_destino
rm -rf "$TRABALHO"
log "RESTAURACAO_OK fonte=$FONTE (agora: docker compose up -d --wait, para o migrator, a api e o worker)"
