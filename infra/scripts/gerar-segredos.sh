#!/usr/bin/env bash
# =============================================================================
# WebRadio — infra/scripts/gerar-segredos.sh
# Autor: Forge · tarefa E1-F18 · docs/specs/05-seguranca-auditoria.md §5
#
# Gera segredos aleatórios para dev (.env, um arquivo só) ou prod (secrets/,
# um arquivo por segredo — Docker/Compose secrets, S-C02 §5.3). NUNCA
# sobrescreve o que já existe: rodar duas vezes é seguro (idempotente por
# omissão, não por regeneração).
#
# Alfabeto (contrato com infra/sql/logins.sql, revisão do Sentinel):
#   base64url SEM padding — só [A-Za-z0-9_-]. Nunca ' " \ $ ` ; = , ! +.
#   As senhas de SQL Server entram no CREATE LOGIN por SUBSTITUIÇÃO DE TEXTO
#   do sqlcmd (não é parâmetro real) — uma aspa quebra a string N'...' e vira
#   SQL executado como SA. O mesmo alfabeto também evita quebrar connection
#   strings, URLs de source client e o XML do Icecast.
#
# As 4 senhas de SQL Server (MSSQL_SA_PASSWORD, DB_MIGRATOR_PASSWORD,
# DB_APP_PASSWORD, DB_RELATORIO_PASSWORD) são geradas DE NOVO em laço até ter
# ao menos uma maiúscula, uma minúscula e um dígito — a política de senha do
# SQL Server (CHECK_POLICY = ON) exige 3 das 4 classes, e nosso alfabeto não
# tem símbolo. Sem o laço, cerca de 0,07% das gerações não teriam dígito e o
# CREATE LOGIN falharia.
#
# Uso:
#   infra/scripts/gerar-segredos.sh dev    # cria .env na raiz do repo
#   infra/scripts/gerar-segredos.sh prod   # cria secrets/<nome> (um por segredo)
# =============================================================================
set -euo pipefail
export LC_ALL=C

MODO="${1:-}"
DIR_RAIZ="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"

if [[ "$MODO" != "dev" && "$MODO" != "prod" ]]; then
  echo "Uso: $0 [dev|prod]" >&2
  exit 1
fi

if ! command -v openssl >/dev/null 2>&1; then
  echo "openssl não encontrado no PATH." >&2
  exit 1
fi

# base64url sem padding: só [A-Za-z0-9_-]. $1 = bytes aleatórios de entrada
# (o comprimento final em caracteres é maior, ~4/3 dos bytes).
gerar_base64url() {
  openssl rand -base64 "$1" | tr '+/' '-_' | tr -d '=\n'
}

# Regenera até ter maiúscula, minúscula e dígito (política de senha do SQL Server).
gerar_senha_sql() {
  local valor
  while true; do
    valor="$(gerar_base64url "$1")"
    if [[ "$valor" =~ [A-Z] && "$valor" =~ [a-z] && "$valor" =~ [0-9] ]]; then
      printf '%s' "$valor"
      return 0
    fi
  done
}

# Nome dos segredos gerados aqui × geração (docs/specs/05-seguranca-auditoria.md §5.1).
# MINIO_API_ACCESS_KEY/SECRET e MINIO_WORKER_ACCESS_KEY/SECRET ficam de fora: são
# criados pelo minio-init em runtime, não por este script.
listar_segredos() {
  echo "Jwt__SigningKey=B64:48"
  echo "Seguranca__Pepper=B64:32"
  echo "Playout__Token=HEX:32"
  echo "ICECAST_SOURCE_PASSWORD=B64:24"
  echo "ICECAST_ADMIN_USER=HEXUSER:3"
  echo "ICECAST_ADMIN_PASSWORD=B64:24"
  echo "ICECAST_RELAY_PASSWORD=B64:24"
  echo "LIQUIDSOAP_HARBOR_PASSWORD=B64:24"
  echo "MSSQL_SA_PASSWORD=SQL:32"
  echo "DB_MIGRATOR_PASSWORD=SQL:24"
  echo "DB_APP_PASSWORD=SQL:24"
  echo "DB_RELATORIO_PASSWORD=SQL:24"
  echo "MINIO_ROOT_USER=HEX:16"
  echo "MINIO_ROOT_PASSWORD=B64:32"
  echo "REDIS_PASSWORD=B64:32"
  echo "Seed__AdminSenha=B64:18"
}

gerar_valor() {
  # $1 = receita no formato TIPO:N (ver listar_segredos)
  local tipo="${1%%:*}"
  local n="${1##*:}"
  case "$tipo" in
    B64)     gerar_base64url "$n" ;;
    HEX)     openssl rand -hex "$n" ;;
    HEXUSER) printf 'locutor-%s' "$(openssl rand -hex "$n")" ;;
    SQL)     gerar_senha_sql "$n" ;;
    *)       echo "receita desconhecida: $1" >&2; exit 1 ;;
  esac
}

if [[ "$MODO" == "dev" ]]; then
  DESTINO="$DIR_RAIZ/.env"
  if [[ -e "$DESTINO" ]]; then
    echo "$DESTINO já existe — não sobrescrevendo. Apague-o manualmente para gerar de novo." >&2
    exit 0
  fi

  TMP="$(mktemp)"
  trap 'rm -f "$TMP"' EXIT

  {
    # Comentários em ASCII puro de propósito: o arquivo não deve depender de detecção de encoding.
    echo "# Gerado por infra/scripts/gerar-segredos.sh dev -- $(date -u +%Y-%m-%dT%H:%M:%SZ)"
    echo "# NUNCA versionar este arquivo (.gitignore ja cobre .env)."
    echo
    echo "ASPNETCORE_ENVIRONMENT=Development"
    echo "SEED_DEV=true"
    echo
    while IFS='=' read -r nome receita; do
      printf '%s=%s\n' "$nome" "$(gerar_valor "$receita")"
    done < <(listar_segredos)
    echo
    echo "Seed__AdminEmail=admin@webradio.local"
  } > "$TMP"

  install -m 600 "$TMP" "$DESTINO"
  echo "Gerado $DESTINO."
else
  DESTINO_DIR="$DIR_RAIZ/secrets"
  mkdir -p "$DESTINO_DIR"
  chmod 700 "$DESTINO_DIR"

  while IFS='=' read -r nome receita; do
    # nomes de arquivo em minúsculo/snake_case (Docker secrets, D19)
    arquivo="$DESTINO_DIR/$(echo "$nome" | sed -E 's/([a-z0-9])([A-Z])/\1_\2/g; s/__/_/g' | tr 'A-Z' 'a-z')"
    if [[ -e "$arquivo" ]]; then
      echo "$arquivo já existe — pulando." >&2
      continue
    fi
    TMP="$(mktemp)"
    printf '%s' "$(gerar_valor "$receita")" > "$TMP"
    install -m 600 "$TMP" "$arquivo"
    rm -f "$TMP"
  done < <(listar_segredos)

  if [[ ! -e "$DESTINO_DIR/seed_admin_email" ]]; then
    TMP="$(mktemp)"
    printf 'admin@webradio.local' > "$TMP"
    install -m 600 "$TMP" "$DESTINO_DIR/seed_admin_email"
    rm -f "$TMP"
  fi

  echo "Segredos gerados em $DESTINO_DIR (os que já existiam foram preservados)."
fi
