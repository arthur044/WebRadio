#!/usr/bin/env bash
# =============================================================================
# WebRadio — infra/minio/teste.sh (Sentinel no #128/#112, E1-F16)
# Roda o infra/minio/init-buckets.sh com um `mc` de mentira (sem Docker, sem MinIO) e confere o que o minio-init promete sobre SEGREDO:
#   1. um segredo que começa com '-' é recusado com mensagem fixa, e o valor NÃO aparece na saída;
#   2. se o `mc admin user add` falha (e ecoa o segredo, como o mc real fez no CI), a saída do mc é descartada: mensagem fixa com o rc, sem o valor;
#   3. no caminho feliz o `mc admin user add` recebe `--` antes dos argumentos posicionais (chave que comece com '-' não vira flag).
# Uso (na raiz do repositório):  infra/minio/teste.sh
# =============================================================================
set -euo pipefail
export LC_ALL=C

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT
FALHAS=0
ok()    { echo "  ok    $*"; }
falha() { echo "  FALHA $*" >&2; FALHAS=$((FALHAS + 1)); }
confere() { if "${@:2}"; then ok "$1"; else falha "$1"; fi; }

# mc de mentira: registra os argumentos de `admin user add`; MC_FALHA_USER=1 faz esse comando falhar ECOANDO os argumentos (o vazamento real).
mkdir -p "$TMP/bin" "$TMP/pol"
cat > "$TMP/bin/mc" <<'MC'
#!/bin/sh
if [ "$1" = "admin" ] && [ "$2" = "user" ] && [ "$3" = "add" ]; then
  echo "user add $*" >> "$MC_LOG"
  if [ "${MC_FALHA_USER:-0}" = "1" ]; then echo "mc: <ERROR> Invalid command usage, flag provided but not defined: $*" >&2; exit 1; fi
fi
exit 0
MC
chmod 0755 "$TMP/bin/mc"

segredos() { # $1 = diretório; $2 = secret key do svc-api
  mkdir -p "$1"
  printf '%s' "root" > "$1/minio_root_user"
  printf '%s' "RootPass123" > "$1/minio_root_password"
  printf '%s' "ApiAcesso123" > "$1/minio_api_access_key"
  printf '%s' "$2" > "$1/minio_api_secret_key"
  printf '%s' "WorkerAcesso123" > "$1/minio_worker_access_key"
  printf '%s' "WorkerSecreto123" > "$1/minio_worker_secret_key"
}
rodar() { # $1 = diretório de segredos; saída (stdout+stderr) em $TMP/saida; devolve o código de saída
  PATH="$TMP/bin:$PATH" SEGREDOS_DIR="$1" MC_LOG="$TMP/mc.log" MC_FALHA_USER="${MC_FALHA_USER:-0}" sh -c "P=$TMP/pol; sed 's#^P=.*#P=$TMP/pol#' infra/minio/init-buckets.sh | sh" > "$TMP/saida" 2>&1
}

echo "== segredo que começa com '-'"
SEGREDO_RUIM="-SyNrySegredoQueNaoPodeVazar"
segredos "$TMP/s1" "$SEGREDO_RUIM"
: > "$TMP/mc.log"
if rodar "$TMP/s1"; then falha "init-buckets.sh devia falhar com segredo iniciado em '-'"; else ok "init-buckets.sh falha com segredo iniciado em '-'"; fi
confere "mensagem fixa" grep -q "minio_api_secret_key começa com '-'" "$TMP/saida"
confere "o valor NÃO aparece na saída" bash -c "! grep -q -- '$SEGREDO_RUIM' '$TMP/saida'"
confere "o mc nunca recebeu o segredo" bash -c "! grep -q -- '$SEGREDO_RUIM' '$TMP/mc.log'"

echo "== mc admin user add falha e ecoa o segredo"
SEGREDO_OK="SegredoApiValido123"
segredos "$TMP/s2" "$SEGREDO_OK"
: > "$TMP/mc.log"
if MC_FALHA_USER=1 rodar "$TMP/s2"; then falha "init-buckets.sh devia falhar quando o mc falha"; else ok "init-buckets.sh falha quando o mc falha"; fi
confere "mensagem fixa com o rc" grep -q "não consegui criar o usuário de serviço svc-api (rc=1; saída do mc omitida" "$TMP/saida"
confere "nem a chave nem o segredo aparecem na saída" bash -c "! grep -q -e '$SEGREDO_OK' -e 'ApiAcesso123' '$TMP/saida'"

echo "== caminho feliz"
segredos "$TMP/s3" "$SEGREDO_OK"
: > "$TMP/mc.log"
confere "init-buckets.sh termina bem" rodar "$TMP/s3"
confere "mc admin user add recebe -- antes dos argumentos" grep -q -- "^user add admin user add -- wr ApiAcesso123 $SEGREDO_OK\$" "$TMP/mc.log"
confere "os dois usuários de serviço foram criados" test "$(grep -c '^user add' "$TMP/mc.log")" = "2"

echo
if [[ "$FALHAS" -eq 0 ]]; then echo "minio-init: tudo certo."; else echo "minio-init: $FALHAS falha(s)." >&2; cat "$TMP/saida" >&2 || true; exit 1; fi
