#!/usr/bin/env bash
# =============================================================================
# WebRadio — infra/db-init/entrypoint.sh
# Autor: Forge · tarefa E1-F05 (D20) · docs/specs/04-epico-1-tarefas.md
#
# Serviço one-shot "db-init": o ÚNICO ponto do stack autenticado como SA
# (S-A10). Roda infra/sql/init-db.sql, depois infra/sql/logins.sql e, só em
# dev com SEED_DEV=true, infra/sql/seed-dev.sql. Idempotente: rodar 2x
# seguidas não falha nem duplica nada (os próprios .sql garantem isso).
#
# Contrato de segurança (docs/specs/05-seguranca-auditoria.md §5):
#   - Toda senha entra por SQLCMDPASSWORD (conexão) ou -v (script vars do
#     sqlcmd para logins.sql), NUNCA por -P (fica visível em `ps`/`docker
#     inspect`).
#   - O alfabeto de TODA senha é validado AQUI, com LC_ALL=C, ANTES de
#     qualquer chamada ao sqlcmd — inclusive rejeitando string vazia. O
#     gerar-segredos.sh/.ps1 (F18) já não gera fora do alfabeto; isto é a
#     defesa de segundo nível para quem trocar uma senha à mão. Uma senha
#     com aspa quebraria o CREATE LOGIN por substituição de texto do sqlcmd
#     (logins.sql), e nenhuma checagem DENTRO do .sql pegaria isso a tempo.
#   - -v Ambiente é OBRIGATÓRIO e sem valor padrão (init-db.sql e
#     seed-dev.sql abortam sem ele — fail-closed).
#
# Variáveis de ambiente esperadas:
#   ASPNETCORE_ENVIRONMENT       'Development' ou 'Production' (obrigatória)
#   SEED_DEV                     'true' para rodar seed-dev.sql (só em dev)
#   MSSQL_SA_PASSWORD_FILE       caminho do segredo da SA (padrão: /run/secrets/mssql_sa_password)
#   DB_APP_PASSWORD_FILE         (padrão: /run/secrets/db_app_password)
#   DB_MIGRATOR_PASSWORD_FILE    (padrão: /run/secrets/db_migrator_password)
#   DB_RELATORIO_PASSWORD_FILE   (padrão: /run/secrets/db_relatorio_password)
#   DB_SERVER                    host do SQL Server (padrão: sqlserver)
# =============================================================================
set -euo pipefail
export LC_ALL=C

DIR_SCRIPT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DIR_SQL="$(cd "$DIR_SCRIPT/../sql" && pwd)"

DB_SERVER="${DB_SERVER:-sqlserver}"
AMBIENTE="${ASPNETCORE_ENVIRONMENT:-}"

MSSQL_SA_PASSWORD_FILE="${MSSQL_SA_PASSWORD_FILE:-/run/secrets/mssql_sa_password}"
DB_APP_PASSWORD_FILE="${DB_APP_PASSWORD_FILE:-/run/secrets/db_app_password}"
DB_MIGRATOR_PASSWORD_FILE="${DB_MIGRATOR_PASSWORD_FILE:-/run/secrets/db_migrator_password}"
DB_RELATORIO_PASSWORD_FILE="${DB_RELATORIO_PASSWORD_FILE:-/run/secrets/db_relatorio_password}"

# -v Ambiente é obrigatório aqui também (defesa em profundidade — init-db.sql e
# seed-dev.sql já abortam sozinhos, mas falhar antes de tocar o sqlcmd é mais barato
# e dá um erro mais claro do que "variável não definida" do sqlcmd).
if [[ "$AMBIENTE" != "Development" && "$AMBIENTE" != "Production" ]]; then
  echo "entrypoint: ASPNETCORE_ENVIRONMENT precisa ser Development ou Production (valor: '${AMBIENTE}'). Abortando." >&2
  exit 1
fi

# Rejeita string vazia e qualquer caractere fora de [A-Za-z0-9_-] (mesmo alfabeto do F18).
# `case *[!A-Za-z0-9_-]*` sozinho aceita a string vazia — por isso o teste de vazio vem antes.
validar_alfabeto_senha() {
  local nome="$1" valor="$2"
  if [[ -z "$valor" ]]; then
    echo "entrypoint: $nome está vazia. Abortando antes de chamar o sqlcmd." >&2
    exit 1
  fi
  case "$valor" in
    *[!A-Za-z0-9_-]*)
      echo "entrypoint: $nome tem caractere fora do alfabeto seguro [A-Za-z0-9_-]. Abortando antes de chamar o sqlcmd." >&2
      exit 1
      ;;
  esac
}

ler_segredo() {
  local arquivo="$1"
  if [[ ! -r "$arquivo" ]]; then
    echo "entrypoint: não consegui ler $arquivo." >&2
    exit 1
  fi
  cat "$arquivo"
}

SA_PASSWORD="$(ler_segredo "$MSSQL_SA_PASSWORD_FILE")"
APP_PASSWORD="$(ler_segredo "$DB_APP_PASSWORD_FILE")"
MIGRATOR_PASSWORD="$(ler_segredo "$DB_MIGRATOR_PASSWORD_FILE")"
RELATORIO_PASSWORD="$(ler_segredo "$DB_RELATORIO_PASSWORD_FILE")"

validar_alfabeto_senha "MSSQL_SA_PASSWORD" "$SA_PASSWORD"
validar_alfabeto_senha "DB_APP_PASSWORD" "$APP_PASSWORD"
validar_alfabeto_senha "DB_MIGRATOR_PASSWORD" "$MIGRATOR_PASSWORD"
validar_alfabeto_senha "DB_RELATORIO_PASSWORD" "$RELATORIO_PASSWORD"

# SQLCMDPASSWORD, nunca -P (S-A10): -P fica visível por inteiro em `ps aux` e em
# `docker inspect` (args do processo), SQLCMDPASSWORD só existe no ambiente do processo.
export SQLCMDPASSWORD="$SA_PASSWORD"

echo "entrypoint: rodando init-db.sql (Ambiente=$AMBIENTE)..."
sqlcmd -b -C -I -S "$DB_SERVER" -U sa -v Ambiente="$AMBIENTE" -i "$DIR_SQL/init-db.sql"

echo "entrypoint: rodando logins.sql..."
sqlcmd -b -C -I -S "$DB_SERVER" -U sa \
  -v DB_APP_PASSWORD="$APP_PASSWORD" \
     DB_MIGRATOR_PASSWORD="$MIGRATOR_PASSWORD" \
     DB_RELATORIO_PASSWORD="$RELATORIO_PASSWORD" \
  -i "$DIR_SQL/logins.sql"

if [[ "${SEED_DEV:-}" == "true" && "$AMBIENTE" == "Development" ]]; then
  echo "entrypoint: SEED_DEV=true em Development — rodando seed-dev.sql..."
  sqlcmd -b -C -I -S "$DB_SERVER" -U sa \
    -v Ambiente="$AMBIENTE" \
       Locutor1Email="locutor1@example.com" \
       Locutor2Email="locutor2@example.com" \
    -i "$DIR_SQL/seed-dev.sql"
else
  echo "entrypoint: seed-dev.sql pulado (SEED_DEV='${SEED_DEV:-}', Ambiente=$AMBIENTE)."
fi

echo "entrypoint: db-init concluído."
