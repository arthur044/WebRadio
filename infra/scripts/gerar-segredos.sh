#!/usr/bin/env bash
# =============================================================================
# WebRadio — infra/scripts/gerar-segredos.sh
# Autor: Forge · tarefa E1-F18 · docs/specs/05-seguranca-auditoria.md §5
#
# Gera os segredos do stack em secrets/<nome> (um arquivo por segredo), montados
# pelo Compose em /run/secrets (S-C02 §5.3). Vale para dev
# E para prod: segredo em variável de ambiente aparece em `docker inspect`, em
# /proc/<pid>/environ e no `docker compose config` (DoD: o config não pode mostrar
# segredo literal). No modo dev o script também cria o .env com a configuração
# NÃO secreta de desenvolvimento (copiada do .env.example).
#
# Permissões: a PASTA secrets/ é 700 (nenhum outro usuário do host entra nela) e
# cada arquivo é 644. Fora do Swarm, o Compose monta o segredo como bind mount com
# o dono e o modo do HOST (uid/gid/mode do compose são ignorados); com 600, os
# serviços que rodam sem root (icecast uid 100, redis 999, mssql 10001, minio
# 65532, .NET 1654) não conseguiriam ler o próprio segredo. Quem protege no host
# é a pasta; dentro do contêiner, cada serviço só recebe os segredos dele.
#
# NUNCA sobrescreve o que já existe: rodar duas vezes é seguro (idempotente por
# omissão, não por regeneração). Para rotacionar um segredo, apague o arquivo
# dele e rode de novo (veja a tabela 5.1 do 05 para o impacto de cada um).
#
# Alfabeto (contrato com infra/sql/logins.sql, revisão do Sentinel):
#   base64url SEM padding — só [A-Za-z0-9_-]. Nunca ' " \ $ ` ; = , ! +.
#   As senhas de SQL Server entram no CREATE LOGIN por SUBSTITUIÇÃO DE TEXTO
#   do sqlcmd (não é parâmetro real) — uma aspa quebra a string N'...' e vira
#   SQL executado como SA. O mesmo alfabeto também evita quebrar connection
#   strings, URLs de source client e o XML do Icecast.
#
# Todo segredo gerado tem ≥ 24 caracteres; os base64url são gerados DE NOVO em
# laço até ter maiúscula, minúscula e dígito (as 3 classes da política do SQL
# Server; em cerca de 0,07% das gerações faltaria dígito e o CREATE LOGIN
# falharia). Os hex (playout_token, minio_*_user/access_key) não têm maiúscula
# por natureza e ficam fora do laço: não são senha de SQL.
#
# E-mail do Admin semeado (secrets/seed_admin_email): em dev vale admin@webradio.local; em PROD é obrigatório informar o
# seu (2º argumento ou ADMIN_EMAIL), porque um e-mail fixo e conhecido é metade da credencial de um Admin (B3-10).
#
# Uso:
#   infra/scripts/gerar-segredos.sh dev                         # secrets/ + .env de desenvolvimento
#   infra/scripts/gerar-segredos.sh prod admin@radio.exemplo.br # só secrets/ (o .env de prod sai do .env.example)
# =============================================================================
set -euo pipefail
export LC_ALL=C
umask 077

MODO="${1:-}"
ADMIN_EMAIL="${2:-${ADMIN_EMAIL:-}}"
DIR_RAIZ="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"

if [[ "$MODO" != "dev" && "$MODO" != "prod" ]]; then
  echo "Uso: $0 dev | $0 prod <email-do-admin>" >&2
  exit 1
fi

# O e-mail não é segredo, mas entra no seed como credencial: alfabeto estrito (sem espaço, aspa, ';' nem barra).
if [[ -n "$ADMIN_EMAIL" && ! "$ADMIN_EMAIL" =~ ^[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}$ ]]; then
  echo "E-mail do Admin inválido." >&2
  exit 1
fi
if [[ "$MODO" == "prod" && -z "$ADMIN_EMAIL" && ! -e "$DIR_RAIZ/secrets/seed_admin_email" ]]; then
  echo "Em prod informe o e-mail do Admin: $0 prod <email-do-admin> (ou ADMIN_EMAIL=...)." >&2
  exit 1
fi

if ! command -v openssl >/dev/null 2>&1; then
  echo "openssl não encontrado no PATH." >&2
  exit 1
fi

# base64url sem padding: só [A-Za-z0-9_-]. $1 = bytes aleatórios (o texto sai com ~4/3 disso).
# O tr -d '\n' importa: o openssl quebra a linha a cada 64 caracteres.
gerar_base64url() {
  openssl rand -base64 "$1" | tr '+/' '-_' | tr -d '=\n'
}

# Regenera até ter maiúscula, minúscula e dígito.
gerar_forte() {
  local valor
  while true; do
    valor="$(gerar_base64url "$1")"
    if [[ "$valor" =~ [A-Z] && "$valor" =~ [a-z] && "$valor" =~ [0-9] ]]; then
      printf '%s' "$valor"
      return 0
    fi
  done
}

# arquivo em secrets/ = receita (docs/specs/05-seguranca-auditoria.md §5.1).
# As credenciais de serviço do MinIO (svc-api, svc-worker) também nascem aqui: o
# minio-init cria os usuários com ESTES valores e a api/o worker os leem do
# próprio arquivo, sem nenhum contêiner de aplicação ver a root (S-A04).
listar_segredos() {
  cat <<'LISTA'
jwt_signing_key=B64:48
seguranca_pepper=B64:32
playout_token=HEX:32
icecast_source_password=B64:24
icecast_admin_user=HEXUSER:3
icecast_admin_password=B64:24
icecast_relay_password=B64:24
mssql_sa_password=B64:32
db_migrator_password=B64:24
db_app_password=B64:24
db_relatorio_password=B64:24
minio_root_user=HEX:16
minio_root_password=B64:32
minio_api_access_key=HEX:20
minio_api_secret_key=B64:30
minio_worker_access_key=HEX:20
minio_worker_secret_key=B64:30
redis_password=B64:32
seed_admin_senha=B64:18
seed_dev_senha=B64:18
LISTA
}

gerar_valor() {
  # $1 = receita no formato TIPO:N (ver listar_segredos)
  local tipo="${1%%:*}"
  local n="${1##*:}"
  case "$tipo" in
    B64)     gerar_forte "$n" ;;
    HEX)     openssl rand -hex "$n" ;;
    HEXUSER) printf 'locutor-%s' "$(openssl rand -hex "$n")" ;;
    *)       echo "receita desconhecida: $1" >&2; exit 1 ;;
  esac
}

DESTINO_DIR="$DIR_RAIZ/secrets"
mkdir -p "$DESTINO_DIR"
chmod 700 "$DESTINO_DIR"

gravar() {
  # $1 = arquivo, $2 = valor. Sem newline no fim (o .NET e o sqlcmd leriam o \n como parte da senha).
  # Grava num temporário da MESMA pasta e renomeia: nunca fica um arquivo pela metade.
  local tmp
  tmp="$(mktemp "$DESTINO_DIR/.tmp.XXXXXX")"
  printf '%s' "$2" > "$tmp"
  chmod 644 "$tmp"   # ver "Permissões" no cabeçalho
  mv "$tmp" "$1"
}

criados=0
while IFS='=' read -r nome receita; do
  arquivo="$DESTINO_DIR/$nome"
  if [[ -e "$arquivo" ]]; then
    echo "secrets/$nome já existe — preservado." >&2
    chmod 644 "$arquivo"   # gerações antigas usavam 600 (ilegível para os serviços sem root)
    continue
  fi
  gravar "$arquivo" "$(gerar_valor "$receita")"
  criados=$((criados + 1))
done < <(listar_segredos)

if [[ ! -e "$DESTINO_DIR/seed_admin_email" ]]; then
  gravar "$DESTINO_DIR/seed_admin_email" "${ADMIN_EMAIL:-admin@webradio.local}"
else
  chmod 644 "$DESTINO_DIR/seed_admin_email"
fi

echo "secrets/: $criados segredo(s) novo(s); os que já existiam foram preservados."

if [[ "$MODO" == "dev" ]]; then
  if [[ -e "$DIR_RAIZ/.env" ]]; then
    echo ".env já existe — não sobrescrevendo." >&2
  else
    # O .env só tem configuração NÃO secreta; o exemplo já vem com os valores de dev.
    install -m 600 "$DIR_RAIZ/.env.example" "$DIR_RAIZ/.env"
    echo "Gerado .env (configuração de desenvolvimento, copiada do .env.example)."
  fi
fi
