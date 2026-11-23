#!/usr/bin/env bash
# =============================================================================
# WebRadio — infra/backup/backup.sh (E5-F03, E5-D3). Serviço `backup` (profile `backup`), disparado pelo systemd.timer do host às 03:30.
#   docker compose --profile backup run --rm backup
#
# Etapas, na ordem; cada uma só roda se a anterior saiu com 0:
#   sql       BACKUP DATABASE ... WITH CHECKSUM como svc_backup (infra/sql/backup.sql) -> /var/opt/mssql/backup/webradio.bak
#   midia     mc mirror dos buckets midia e publico (NUNCA a quarentena: é transitória e pode ter arquivo malicioso) -> /backup/espelho
#   envio     restic backup do .bak e do espelho para o repositório LOCAL (/backup/repo, criptografado) e, se configurado, restic copy para o EXTERNO (S3)
#   retencao  restic forget --keep-daily 7 --keep-weekly 4 --keep-monthly 6 --prune (local e, se configurado, externo)
#   check     restic check (aos domingos; BACKUP_FORCAR_CHECK=1 força)
# Log: `BACKUP_OK` (tamanho e duração) ou `BACKUP_FALHOU etapa=<...> motivo=<token>` e saída com o código da etapa (sql 10, midia 20, envio 30, retencao 40, check 50;
# 2 = segredo ou configuração ausente; 3 = outro backup rodando). O `motivo` é um token FIXO (credencial_recusada, sem_conexao, ...): a saída crua dos programas pode
# trazer host, caminho e pedaço de segredo, então NÃO vai para o log. BACKUP_DEBUG=1 (só numa execução manual) a mostra no stderr.
#
# Cópia EXTERNA (S3) é OPCIONAL: sem BACKUP_S3_ENDPOINT, BACKUP_S3_BUCKET e os dois segredos backup_s3_* PREENCHIDOS, o script registra
# BACKUP_EXTERNO_NAO_CONFIGURADO (aviso, não erro) e segue só com o repositório local. Liga depois só configurando, sem mudar código.
#
# Segredos SEMPRE de arquivo em /run/secrets, nunca em argv: a senha do sqlcmd por SQLCMDPASSWORD, a do restic por --password-file, as chaves do MinIO e do S3 por variável de
# ambiente montada aqui, só para o processo filho.
# Variáveis: DB_SERVER (sqlserver), MINIO_ENDPOINT (minio:9000), BACKUP_S3_ENDPOINT (com o esquema: https://s3.exemplo.com), BACKUP_S3_BUCKET,
#   BACKUP_TIMEOUT_EXTERNO_S (padrão 7200: cada comando restic contra o storage externo; o restic repete uma conexão que falha por ~15 min antes de desistir),
#   BACKUP_ETAPAS (padrão: sql,midia,envio,retencao,check), BACKUP_FORCAR_CHECK, BACKUP_DEBUG; para o teste: SEGREDOS_DIR, BAK_DIR, BACKUP_LOCAL, SQL_DIR.
# =============================================================================
set -uo pipefail
export LC_ALL=C

S="${SEGREDOS_DIR:-/run/secrets}"
BAK_DIR="${BAK_DIR:-/var/opt/mssql/backup}"
LOCAL="${BACKUP_LOCAL:-/backup}"
SQL_DIR="${SQL_DIR:-/sql}"
DB_SERVER="${DB_SERVER:-sqlserver}"
MINIO_ENDPOINT="${MINIO_ENDPOINT:-minio:9000}"
ETAPAS="${BACKUP_ETAPAS:-sql,midia,envio,retencao,check}"
REPO_LOCAL="$LOCAL/repo"
ESPELHO="$LOCAL/espelho"
export RESTIC_CACHE_DIR="$LOCAL/cache-restic" HOME="${HOME:-/tmp}" MC_CONFIG_DIR="${MC_CONFIG_DIR:-/tmp/.mc}"
SAIDA="$(mktemp)"
trap 'rm -f "$SAIDA"' EXIT
INICIO="$(date +%s)"

log() { printf '%s %s\n' "$(date -u +%FT%TZ)" "$*"; }
etapa_ativa() { [[ ",$ETAPAS," == *",$1,"* ]]; }

# Classifica a saída guardada em um token fixo. NUNCA devolve o texto cru.
RC_ULTIMO=0
motivo_de() {
  if [[ "$RC_ULTIMO" == "124" || "$RC_ULTIMO" == "143" ]]; then echo tempo_esgotado; return; fi
  if grep -qiE 'login failed|access ?denied|signaturedoesnotmatch|signature we calculated|does not match the signature|invalidaccesskeyid|forbidden|unauthorized|wrong password|no key found|permission denied' "$SAIDA"; then echo credencial_ou_permissao_recusada
  elif grep -qiE 'no space left|disk full|quota|there is not enough space' "$SAIDA"; then echo sem_espaco
  elif grep -qiE 'connection refused|no such host|dial tcp|timeout|timed out|could not open a connection|network is unreachable|tls:' "$SAIDA"; then echo sem_conexao
  elif grep -qiE 'nosuchbucket|bucket does not exist|repository does not exist|unable to open config|is there a repository' "$SAIDA"; then echo destino_inexistente
  elif grep -qiE 'locked|lock' "$SAIDA"; then echo repositorio_travado
  elif grep -qiE 'pack|index|integrity|checksum|corrupt|missing|damaged|3043|3201' "$SAIDA"; then echo dado_inconsistente
  else echo desconhecido; fi
}
falhar() { # falhar <etapa> <código> [destino]
  local m; m="$(motivo_de)"
  log "BACKUP_FALHOU etapa=$1${3:+ destino=$3} motivo=$m"
  if [[ "${BACKUP_DEBUG:-}" == "1" ]]; then { echo "--- saída crua (BACKUP_DEBUG=1; pode ter host, caminho e trecho de segredo: não cole em lugar nenhum) ---"; cat "$SAIDA"; } >&2; fi
  exit "$2"
}
config_falhou() { log "BACKUP_FALHOU etapa=config motivo=$1"; exit 2; }
rodar() { "$@" >"$SAIDA" 2>&1; RC_ULTIMO=$?; return "$RC_ULTIMO"; }
ler() { # segredo de arquivo; imprime o valor
  [[ -r "$S/$1" ]] || config_falhou "segredo_ilegivel:$1"
  local v; v="$(cat "$S/$1")"
  [[ -n "$v" ]] || config_falhou "segredo_vazio:$1"
  printf '%s' "$v"
}

# Um backup por vez (o timer e uma execução manual não se pisam).
mkdir -p "$LOCAL" 2>/dev/null
exec 9>"$LOCAL/.backup.lock"
flock -n 9 || { log "BACKUP_FALHOU etapa=config motivo=outro_backup_em_andamento"; exit 3; }

[[ -r "$S/restic_senha" && -s "$S/restic_senha" ]] || config_falhou "segredo_ilegivel:restic_senha"
export RESTIC_PASSWORD_FILE="$S/restic_senha"

externo_configurado() {
  [[ -n "${BACKUP_S3_ENDPOINT:-}" && -n "${BACKUP_S3_BUCKET:-}" && -s "$S/backup_s3_access_key" && -s "$S/backup_s3_secret_key" ]]
}
# Roda o restic contra o repositório EXTERNO: as chaves do S3 entram só no ambiente deste processo, lidas de arquivo.
restic_externo() {
  AWS_ACCESS_KEY_ID="$(cat "$S/backup_s3_access_key")" AWS_SECRET_ACCESS_KEY="$(cat "$S/backup_s3_secret_key")" \
    timeout "${BACKUP_TIMEOUT_EXTERNO_S:-7200}" restic -r "s3:${BACKUP_S3_ENDPOINT%/}/${BACKUP_S3_BUCKET}" "$@"
}
restic_local() { restic -r "$REPO_LOCAL" "$@"; }

log "BACKUP_INICIO etapas=$ETAPAS"

# ---------------------------------------------------------------------- sql
if etapa_ativa sql; then
  SQLCMDPASSWORD="$(ler sql_backup_senha)"; export SQLCMDPASSWORD
  rm -f "$BAK_DIR/webradio.bak.ok" 2>/dev/null
  rodar sqlcmd -b -C -N -I -S "$DB_SERVER" -U svc_backup -l 30 -H WebRadio.Backup -i "$SQL_DIR/backup.sql" || falhar sql 10
  unset SQLCMDPASSWORD
  [[ -s "$BAK_DIR/webradio.bak" ]] || { echo "webradio.bak ausente ou vazio" >"$SAIDA"; falhar sql 10; }
  log "BACKUP_SQL_OK tamanho_bytes=$(stat -c %s "$BAK_DIR/webradio.bak")"
fi

# -------------------------------------------------------------------- midia
if etapa_ativa midia; then
  MC_HOST_origem="http://$(ler minio_backup_access_key):$(ler minio_backup_secret_key)@${MINIO_ENDPOINT}"; export MC_HOST_origem
  mkdir -p "$ESPELHO" || config_falhou espelho_nao_gravavel
  # --remove: o espelho é igual ao bucket (objeto apagado lá some daqui; o histórico fica no restic). Só midia e publico: a identidade do backup nem enxerga a quarentena.
  for bucket in midia publico; do
    rodar mc mirror --overwrite --remove "origem/$bucket" "$ESPELHO/$bucket" || falhar midia 20
  done
  unset MC_HOST_origem
  log "BACKUP_MIDIA_OK espelho_kib=$(du -sk "$ESPELHO" | cut -f1)"
fi

# -------------------------------------------------------------------- envio
if etapa_ativa envio; then
  if ! restic_local cat config >/dev/null 2>&1; then
    rodar restic_local init || falhar envio 30 local
  fi
  # Caminhos absolutos: o snapshot guarda /var/opt/mssql/backup/webradio.bak e /backup/espelho (o restaurar.sh os pega por esses nomes).
  rodar restic_local backup --tag diario --host webradio "$BAK_DIR/webradio.bak" "$ESPELHO" || falhar envio 30 local
  log "BACKUP_ENVIO_OK destino=local"
  if externo_configurado; then
    if ! restic_externo cat config >/dev/null 2>&1; then
      # O repositório externo nasce com os MESMOS parâmetros de partição do local: o `restic copy` só deduplica assim.
      rodar restic_externo init --from-repo "$REPO_LOCAL" --from-password-file "$S/restic_senha" --copy-chunker-params || falhar envio 30 externo
    fi
    rodar restic_externo copy --from-repo "$REPO_LOCAL" --from-password-file "$S/restic_senha" || falhar envio 30 externo
    log "BACKUP_ENVIO_OK destino=externo"
  else
    log "BACKUP_EXTERNO_NAO_CONFIGURADO aviso=so_ha_copia_local (defina BACKUP_S3_ENDPOINT, BACKUP_S3_BUCKET e preencha os segredos backup_s3_access_key e backup_s3_secret_key)"
  fi
fi

# ----------------------------------------------------------------- retencao
if etapa_ativa retencao; then
  rodar restic_local forget --keep-daily 7 --keep-weekly 4 --keep-monthly 6 --prune || falhar retencao 40 local
  if externo_configurado; then rodar restic_externo forget --keep-daily 7 --keep-weekly 4 --keep-monthly 6 --prune || falhar retencao 40 externo; fi
  log "BACKUP_RETENCAO_OK"
fi

# -------------------------------------------------------------------- check
if etapa_ativa check && { [[ "$(date +%u)" == "7" ]] || [[ "${BACKUP_FORCAR_CHECK:-}" == "1" ]]; }; then
  rodar restic_local check || falhar check 50 local
  if externo_configurado; then rodar restic_externo check || falhar check 50 externo; fi
  log "BACKUP_CHECK_OK"
fi

FIM="$(date +%s)"
log "BACKUP_OK duracao_s=$((FIM - INICIO)) repo_local_kib=$(du -sk "$REPO_LOCAL" 2>/dev/null | cut -f1) externo=$(externo_configurado && echo sim || echo nao)"
