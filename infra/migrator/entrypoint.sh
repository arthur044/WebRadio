#!/usr/bin/env bash
# =============================================================================
# WebRadio — infra/migrator/entrypoint.sh · tarefa E1-F05 (D20)
# Serviço one-shot "migrator": aplica as migrations do EF Core via efbundle
# autenticado como webradio_migrator (nunca a SA). Roda depois do db-init
# (service_completed_successfully); api/worker só sobem depois dele.
# Idempotente: o efbundle só aplica o que falta.
#
# Variáveis: DB_SERVER (padrão sqlserver), DB_NAME (padrão WebRadio),
#   DB_MIGRATOR_PASSWORD_FILE (padrão /run/secrets/db_migrator_password),
#   ASPNETCORE_ENVIRONMENT (Development|Production; Encrypt/Trust por ambiente),
#   EFBUNDLE (padrão ./efbundle).
# =============================================================================
set -euo pipefail
export LC_ALL=C

DB_SERVER="${DB_SERVER:-sqlserver}"
DB_NAME="${DB_NAME:-WebRadio}"
ARQ="${DB_MIGRATOR_PASSWORD_FILE:-/run/secrets/db_migrator_password}"
AMBIENTE="${ASPNETCORE_ENVIRONMENT:-}"
EFBUNDLE="${EFBUNDLE:-./efbundle}"

# DB_SERVER/DB_NAME entram na connection string: restringe o alfabeto para que
# um valor com ';' não injete parâmetros (ex.: outro User Id/Password).
[[ "$DB_SERVER" =~ ^[A-Za-z0-9._-]+(,[0-9]+)?$ ]] || { echo "migrator: DB_SERVER inválido." >&2; exit 1; }
[[ "$DB_NAME" =~ ^[A-Za-z0-9_]+$ ]] || { echo "migrator: DB_NAME inválido." >&2; exit 1; }

case "$AMBIENTE" in
  Development) TRUST="True" ;;
  Production)  TRUST="False" ;;
  *) echo "migrator: ASPNETCORE_ENVIRONMENT precisa ser Development ou Production (valor: '${AMBIENTE}')." >&2; exit 1 ;;
esac

[[ -r "$ARQ" ]] || { echo "migrator: não consegui ler $ARQ." >&2; exit 1; }
SENHA="$(cat "$ARQ")"
if [[ -z "$SENHA" ]]; then echo "migrator: senha vazia." >&2; exit 1; fi
case "$SENHA" in
  *[!A-Za-z0-9_-]*) echo "migrator: senha fora do alfabeto [A-Za-z0-9_-]." >&2; exit 1 ;;
esac

# RISCO ACEITO (Sentinel, BAIXO): o efbundle só recebe a conexão por --connection
# (não lê env nem arquivo), então a senha do webradio_migrator fica em argv deste
# processo. Mitigação: contêiner one-shot e efêmero, sem outro processo, sem
# `ps` exposto a terceiros; o webradio_migrator não é sysadmin.
echo "migrator: aplicando migrations (Ambiente=$AMBIENTE)..."
"$EFBUNDLE" --connection "Server=${DB_SERVER};Database=${DB_NAME};User Id=webradio_migrator;Password=${SENHA};Encrypt=True;TrustServerCertificate=${TRUST};Application Name=webradio-migrator;Connect Timeout=30"
echo "migrator: concluído."
