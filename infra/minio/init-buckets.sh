#!/bin/sh
# =============================================================================
# WebRadio — infra/minio/init-buckets.sh (minio-init, E1-F16, S-A04/S-A05)
# One-shot e idempotente: buckets quarentena/midia/publico, quota da quarentena,
# GetObject anônimo SÓ em publico/* (sem listagem), políticas mínimas e os
# usuários de serviço svc-api/svc-worker com as chaves de /run/secrets.
# É o ÚNICO contêiner (além do próprio minio) que vê a credencial root.
# =============================================================================
set -eu
export LC_ALL=C
export HOME=/tmp MC_CONFIG_DIR=/tmp/.mc

S="${SEGREDOS_DIR:-/run/secrets}"
P=/etc/minio-init/policies
ENDPOINT="${MINIO_ENDPOINT:-minio:9000}"
QUOTA="${QUOTA_QUARENTENA:-10GiB}"

falhar() { echo "minio-init: $*" >&2; exit 1; }
ler() {
  [ -r "$S/$1" ] || falhar "não consegui ler $1."
  v="$(cat "$S/$1")"
  [ -n "$v" ] || falhar "$1 está vazio."
  case "$v" in *[!A-Za-z0-9_-]*) falhar "$1 tem caractere fora de [A-Za-z0-9_-]." ;; esac
  printf '%s' "$v"
}

ROOT_USER="$(ler minio_root_user)"
ROOT_PASS="$(ler minio_root_password)"
API_KEY="$(ler minio_api_access_key)"
API_SECRET="$(ler minio_api_secret_key)"
WORKER_KEY="$(ler minio_worker_access_key)"
WORKER_SECRET="$(ler minio_worker_secret_key)"

# Alias por variável de ambiente (só neste processo; nada gravado em disco).
export MC_HOST_wr="http://${ROOT_USER}:${ROOT_PASS}@${ENDPOINT}"

i=0
until mc ready wr >/dev/null 2>&1; do
  i=$((i + 1)); [ "$i" -lt 30 ] || falhar "MinIO não ficou pronto."
  sleep 2
done

for b in quarentena midia publico; do
  mc mb --ignore-existing "wr/$b"
done

mc quota set wr/quarentena --size "$QUOTA"
mc anonymous set-json "$P/publico-anonimo.json" wr/publico

# ListBucket nas políticas de serviço: o SDK .NET do MinIO faz HEAD no BUCKET antes de StatObject/CopyObject, e o
# MinIO exige s3:ListBucket para isso. A listagem ANÔNIMA continua proibida (S-A05): o publico só tem GetObject.
mc admin policy create wr webradio-svc-api "$P/svc-api.json"
mc admin policy create wr webradio-svc-worker "$P/svc-worker.json"

# RISCO ACEITO (igual ao migrator): o mc só recebe a secret key por argumento; o contêiner é one-shot,
# efêmero e sem outro processo.
mc admin user add wr "$API_KEY" "$API_SECRET"
mc admin user add wr "$WORKER_KEY" "$WORKER_SECRET"

anexar() {
  saida="$(mc admin policy attach wr "$1" --user "$2" 2>&1)" && return 0
  case "$saida" in *"already"*) return 0 ;; esac
  echo "$saida" >&2
  falhar "não consegui anexar a política $1."
}
anexar webradio-svc-api "$API_KEY"
anexar webradio-svc-worker "$WORKER_KEY"

echo "minio-init: buckets, políticas e usuários de serviço prontos."
