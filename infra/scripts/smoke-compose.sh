#!/usr/bin/env bash
# =============================================================================
# WebRadio — infra/scripts/smoke-compose.sh (E1-F16/F17, Definição de Pronto 1–4 e 8)
#
# Roda contra o stack JÁ no ar (docker compose -f docker-compose.yml -f docker-compose.dev.yml up -d --wait).
# Usado pelo job compose-smoke do CI e localmente. Sai com 1 no primeiro item que falhar; nunca imprime segredo.
#
#   infra/scripts/smoke-compose.sh
# =============================================================================
set -euo pipefail
export LC_ALL=C

RAIZ="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$RAIZ"
COMPOSE=(docker compose -f docker-compose.yml -f docker-compose.dev.yml)
BASE="${SMOKE_BASE:-http://localhost:8080}"
ORIGEM="${SMOKE_ORIGEM:-$BASE}"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

falhas=0
ok()    { printf '  ok    %s\n' "$1"; }
falha() { printf '  FALHA %s\n' "$1"; falhas=$((falhas + 1)); }
esperar() { # $1 descrição, $2 esperado, $3 obtido
  if [[ "$2" == "$3" ]]; then ok "$1 ($3)"; else falha "$1: esperado $2, veio $3"; fi
}
status() { curl -s -o /dev/null -w '%{http_code}' --max-time 10 "$@"; }

echo "== 1. todos os serviços healthy e os one-shots com exit 0"
while read -r servico estado; do
  case "$estado" in
    *healthy*|*"Exited (0)"*) ok "$servico" ;;
    *) falha "$servico: $estado" ;;
  esac
done < <("${COMPOSE[@]}" ps -a --format '{{.Service}} {{.Status}}' | grep -v '^sanitizer ')

echo "== 2. SPA e stream"
esperar "GET / (SPA)" 200 "$(status "$BASE/")"
cabecalhos="$(curl -s -I --max-time 10 "$BASE/stream/radio.mp3" | tr -d '\r')"
esperar "HEAD /stream/radio.mp3" "HTTP/1.1 200 OK" "$(head -1 <<<"$cabecalhos")"
grep -qi '^content-type: audio/mpeg' <<<"$cabecalhos" && ok "content-type audio/mpeg" || falha "content-type do stream"
curl -s --max-time 4 "$BASE/stream/radio.mp3" -o "$TMP/amostra.mp3" || true
[[ "$(stat -c %s "$TMP/amostra.mp3")" -gt 16000 ]] && ok "stream entrega áudio" || falha "stream sem dados"
esperar "S-A07 /stream/admin/" 404 "$(status "$BASE/stream/admin/")"
esperar "S-A07 PUT /stream/radio.mp3" 403 "$(status -X PUT "$BASE/stream/radio.mp3")"

echo "== 3. login do Admin semeado, troca de senha obrigatória, /auth/me"
corpo="$(python3 -c 'import json,sys; print(json.dumps({"email": open(sys.argv[1]).read(), "senha": open(sys.argv[2]).read()}))' secrets/seed_admin_email secrets/seed_admin_senha)"
# O corpo (com a senha do Admin) vai por stdin: no argv ele apareceria no `ps` numa máquina compartilhada.
resp="$(curl -s --max-time 10 -c "$TMP/cookies" -H 'Content-Type: application/json' --data-binary @- "$BASE/api/v1/auth/login" <<<"$corpo")"
token="$(python3 -c 'import json,sys; print(json.loads(sys.stdin.read()).get("accessToken",""))' <<<"$resp" 2>/dev/null || true)"
if [[ -n "$token" ]]; then
  [[ "${GITHUB_ACTIONS:-}" == "true" ]] && echo "::add-mask::$token"
  ok "login do Admin"
  deve="$(python3 -c 'import json,sys; print(json.loads(sys.stdin.read())["deveTrocarSenha"])' <<<"$resp")"
  esperar "deveTrocarSenha no primeiro login" True "$deve"
  esperar "/auth/me com token" 200 "$(status -H "Authorization: Bearer $token" "$BASE/api/v1/auth/me")"
  esperar "rota qualquer com token restrito" 403 "$(status -H "Authorization: Bearer $token" "$BASE/api/v1/stream/qualquer")"
  esperar "refresh sem Origin" 403 "$(status -b "$TMP/cookies" -X POST "$BASE/api/v1/auth/refresh")"
  esperar "refresh com Origin de outra origem" 403 "$(status -b "$TMP/cookies" -H 'Origin: https://evil.example' -X POST "$BASE/api/v1/auth/refresh")"
  esperar "refresh da mesma origem" 200 "$(status -b "$TMP/cookies" -H "Origin: $ORIGEM" -X POST "$BASE/api/v1/auth/refresh")"
else
  falha "login do Admin (sem accessToken; o Admin já trocou a senha? use volumes limpos)"
fi
esperar "/auth/me sem token" 401 "$(status "$BASE/api/v1/auth/me")"

echo "== 4. health só no :8081 e bypass do S-A06 pela :8080"
ready="$("${COMPOSE[@]}" exec -T api wget -qO- http://127.0.0.1:8081/health/ready || true)"
[[ "$ready" == *'"status":"Healthy"'* ]] && ok "/health/ready interno: Healthy" || falha "/health/ready interno: $ready"
for u in /api/v1/internal/playout/proxima /api/v1/INTERNAL/playout/proxima /api/v1//internal/playout/proxima \
         /api/v1/%69nternal/playout/proxima /api/v1/internal /health/ready /health/live /HEALTH/live; do
  esperar "pela 8080 $u" 404 "$(status -H "Authorization: Bearer x" "$BASE$u")"
done
esperar "Host: localhost:8081 na 8080" 404 "$(status -H 'Host: localhost:8081' "$BASE/api/v1/internal/playout/proxima")"

echo "== F16: isolamento de rede"
alcanca() { "${COMPOSE[@]}" exec -T "$1" bash -c "timeout 3 bash -c 'exec 3<>/dev/tcp/$2/$3'" >/dev/null 2>&1 && echo sim || echo nao; }
esperar "liquidsoap → sqlserver:1433" nao "$(alcanca liquidsoap sqlserver 1433)"
esperar "liquidsoap → redis:6379" nao "$(alcanca liquidsoap redis 6379)"
esperar "liquidsoap → api:8080" nao "$(alcanca liquidsoap api 8080)"
esperar "liquidsoap → api:8081" sim "$(alcanca liquidsoap api 8081)"
web_8081="$("${COMPOSE[@]}" exec -T web sh -c 'nc -z -w 3 api 8081' >/dev/null 2>&1 && echo sim || echo nao)"
esperar "web → api:8081 (S-M15)" nao "$web_8081"

echo "== 8. portas publicadas"
publicadas="$(docker ps --filter label=com.docker.compose.project=webradio --format '{{.Ports}}' | tr ',' '\n' | grep -oE '(0\.0\.0\.0|\[::\]|127\.0\.0\.1):[0-9]+' | sort -u | tr '\n' ' ')"
esperar "só 8080 pública (+ 127.0.0.1 no dev)" "0.0.0.0:8080 127.0.0.1:1433 127.0.0.1:8005 127.0.0.1:9001 [::]:8080 " "$publicadas"

echo "== 7. docker compose config sem segredo literal"
config="$("${COMPOSE[@]}" config)"
vazou=0
for f in secrets/*; do
  v="$(cat "$f")"
  if [[ ${#v} -ge 16 ]] && grep -qF -- "$v" <<<"$config"; then vazou=1; falha "segredo de $(basename "$f") aparece no compose config"; fi
done
[[ $vazou -eq 0 ]] && ok "nenhum segredo no compose config"

echo
if [[ $falhas -gt 0 ]]; then echo "smoke: $falhas falha(s)."; exit 1; fi
echo "smoke: tudo ok."
