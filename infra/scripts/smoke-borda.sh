#!/usr/bin/env bash
# =============================================================================
# WebRadio — infra/scripts/smoke-borda.sh (E5-F05, Definição de Pronto do Épico 5: itens 1 e 3)
#
# Roda contra a base de PRODUÇÃO já no ar, atrás do Caddy em `tls internal` (job `compose-producao` do CI):
#   docker compose -f docker-compose.yml -f docker-compose.smoke.yml -f docker-compose.ci-prod.yml up -d --wait
#   DOMINIO=webradio.test CURL_CA_BUNDLE=<root.crt do Caddy> infra/scripts/smoke-borda.sh
#
#   1. HTTPS com a CA do Caddy, http:// redireciona para https://, HSTS uma vez só (sem includeSubDomains nem preload) e a CSP uma vez só (nginx; o Caddy não a duplica).
#   2. IP real (DoD 3): dois contêineres curl com IPs fixos diferentes na rede `edge` (a rede do Caddy) falam com o Caddy como dois clientes de verdade. O limite de login é por IP
#      (30 por minuto) e a resposta de quem excedeu é um 401 IGUAL ao de uma credencial errada (S-A12), então a prova é com a senha CERTA do Admin semeado:
#      o cliente A gasta o limite (31 tentativas, cada uma com um X-Forwarded-For forjado diferente) e a senha certa dele passa a dar 401; o cliente B, com a mesma senha certa, entra (200).
#      Se o Caddy, o nginx ou a Api vissem o mesmo IP para todos, B também levaria 401; se o X-Forwarded-For forjado valesse, o limite de A nunca fecharia.
#   3. Login, refresh e logout por https (a checagem de Origin compara com https://<domínio>): sem o X-Forwarded-Proto fixo em https, todo refresh e logout dava 403.
#   4. O log do Caddy não traz a query string (a assinatura da URL pré-assinada e o listenerId vão nela) nem o IP inteiro do cliente (E5-D9).
#
# O `curl` dos clientes vem da imagem do `web` (webradio-web:local, já construída pelo compose), com --pull never. A senha do Admin vai por stdin, nunca em argv.
# Acumula as falhas e sai com 1 se houve alguma. Nunca imprime segredo.
# =============================================================================
set -euo pipefail
export LC_ALL=C

RAIZ="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$RAIZ"
: "${DOMINIO:?Defina DOMINIO (o do job: webradio.test)}"
: "${CURL_CA_BUNDLE:?Defina CURL_CA_BUNDLE (o root.crt da CA interna do Caddy)}"
COMPOSE=(docker compose -f docker-compose.yml -f docker-compose.smoke.yml -f docker-compose.ci-prod.yml)
BASE="https://$DOMINIO"
IMAGEM_CURL="${SMOKE_IMAGEM_CURL:-webradio-web:local}"
EDGE_SUBNET="${EDGE_SUBNET:-172.30.250.0/29}"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

falhas=0
ok()    { printf '  ok    %s\n' "$1"; }
falha() { printf '  FALHA %s\n' "$1"; falhas=$((falhas + 1)); }
esperar() { if [[ "$2" == "$3" ]]; then ok "$1 ($3)"; else falha "$1: esperado $2, veio $3"; fi; }
status() { curl -s -o /dev/null -w '%{http_code}' --max-time 15 "$@"; }

EDGE_CID="$("${COMPOSE[@]}" ps -q edge)"
[[ -n "$EDGE_CID" ]] || { echo "smoke-borda: o contêiner edge não está no ar."; exit 1; }
# A rede `edge` e o IP do Caddy nela (o nome da rede leva o prefixo do projeto do Compose).
read -r REDE EDGE_IP < <(docker inspect -f '{{range $k, $v := .NetworkSettings.Networks}}{{$k}} {{$v.IPAddress}}{{"\n"}}{{end}}' "$EDGE_CID" | grep -E '^[^ ]*edge ' | head -1)
[[ -n "${REDE:-}" && -n "${EDGE_IP:-}" ]] || { echo "smoke-borda: não achei a rede edge do Caddy."; exit 1; }
PREFIXO="${EDGE_SUBNET%.*/*}"                # 172.30.250.0/29 -> 172.30.250 (só funciona com subnet /24 a /29 no último octeto, o caso do CI)
IP_A="$PREFIXO.10"
IP_B="$PREFIXO.11"
[[ "$(( ${EDGE_SUBNET##*/} ))" -le 28 ]] || { echo "smoke-borda: a EDGE_SUBNET do job precisa ser /28 ou maior (os clientes usam .10 e .11)."; exit 1; }

# Um cliente de verdade na rede do Caddy: curl como uid 101, sem capability, com a CA do Caddy e o domínio resolvido para o IP dele.
cliente() { # $1 IP do cliente, demais = argumentos do curl (a URL por último). O corpo, se houver, vem por stdin (--data-binary @-).
  local ip="$1"; shift
  docker run --rm -i --pull never --network "$REDE" --ip "$ip" --read-only --cap-drop ALL --security-opt no-new-privileges:true --user 101:101 \
    -v "$CURL_CA_BUNDLE:/ca.crt:ro" --entrypoint curl "$IMAGEM_CURL" \
    -s --max-time 20 --cacert /ca.crt --resolve "$DOMINIO:443:$EDGE_IP" "$@"
}
login_de() { # $1 IP do cliente, $2 X-Forwarded-For forjado; corpo JSON em stdin. Imprime o status.
  cliente "$1" -o /dev/null -w '%{http_code}' -H 'Content-Type: application/json' -H "X-Forwarded-For: $2" --data-binary @- "$BASE/api/v1/auth/login"
}

echo "== 1. HTTPS com a CA do Caddy, redirecionamento, HSTS e CSP"
esperar "GET / por https" 200 "$(status "$BASE/")"
resp="$(curl -s -o /dev/null -w '%{http_code} %{redirect_url}' --max-time 10 --resolve "$DOMINIO:80:127.0.0.1" "http://$DOMINIO/")"
esperar "http:// redireciona para https://" "308 $BASE/" "$resp"
cab="$(curl -s -I --max-time 10 "$BASE/" | tr -d '\r')"
esperar "Strict-Transport-Security uma vez só" 1 "$(grep -ci '^strict-transport-security:' <<<"$cab" || true)"
hsts="$(grep -i '^strict-transport-security:' <<<"$cab" | head -1 | cut -d: -f2- | tr -d ' ')"
[[ "$hsts" =~ ^max-age=[0-9]+$ ]] && ok "HSTS só com max-age, sem includeSubDomains nem preload ($hsts)" || falha "HSTS inesperado: $hsts"
esperar "Content-Security-Policy uma vez só (nginx; o Caddy não duplica)" 1 "$(grep -ci '^content-security-policy:' <<<"$cab" || true)"
# O Host é do domínio: um Host de outro nome não chega ao nginx (o Caddy responde vazio), e o SNI de outro nome nem completa o handshake.
corpo_host="$(curl -s --max-time 10 -H 'Host: evil.example' "$BASE/" | wc -c)"
esperar "Host de outro nome com o SNI certo: corpo vazio (não chega ao nginx)" 0 "$corpo_host"
if curl -s -o /dev/null --max-time 10 --resolve "evil.example:443:127.0.0.1" "https://evil.example/" 2>/dev/null; then falha "SNI de outro nome completou o handshake"; else ok "SNI de outro nome não completa o handshake"; fi

echo "== 2. IP real do cliente atrás do Caddy (DoD 3): dois IPs, um X-Forwarded-For forjado"
SENHA_ADMIN_JSON="$(python3 -I -c 'import json,sys; print(json.dumps({"email": open(sys.argv[1]).read(), "senha": open(sys.argv[2]).read()}))' secrets/seed_admin_email secrets/seed_admin_senha)"
anomalas=""
for i in $(seq 1 31); do
  # 31 contas fantasma diferentes (nenhuma tranca por conta): só o limite por IP (30/min) conta. 0,12 s entre elas: abaixo dos 10 r/s do nginx (api_auth), que daria 429 e não 401.
  st="$(printf '{"email":"fantasma%s@webradio.test","senha":"senha-errada-%s"}' "$i" "$i" | login_de "$IP_A" "10.9.8.$i")"
  [[ "$st" == "401" ]] || anomalas="$anomalas $i:$st"
  sleep 0.12
done
[[ -z "$anomalas" ]] && ok "as 31 tentativas do cliente A (X-Forwarded-For diferente em cada uma) voltaram 401, sem 429 do nginx" || falha "tentativas do cliente A fora do 401:$anomalas"
esperar "cliente A, agora com a senha CERTA do Admin: 401 (o limite por IP fechou; o X-Forwarded-For forjado não o contornou)" 401 "$(login_de "$IP_A" "10.9.9.9" <<<"$SENHA_ADMIN_JSON")"
esperar "cliente B (outro IP), com a mesma senha certa: 200 (não divide o balde com A)" 200 "$(login_de "$IP_B" "10.9.8.1" <<<"$SENHA_ADMIN_JSON")"

echo "== 3. login, refresh e logout por https (checagem de Origin com https://$DOMINIO)"
resp="$(curl -s --max-time 15 -c "$TMP/cookies" -H 'Content-Type: application/json' --data-binary @- "$BASE/api/v1/auth/login" <<<"$SENHA_ADMIN_JSON")"
token="$(python3 -I -c 'import json,sys; print(json.loads(sys.stdin.read()).get("accessToken",""))' <<<"$resp" 2>/dev/null || true)"
if [[ -n "$token" ]]; then
  [[ "${GITHUB_ACTIONS:-}" == "true" ]] && echo "::add-mask::$token"
  ok "login por https"
  esperar "refresh sem Origin" 403 "$(status -b "$TMP/cookies" -X POST "$BASE/api/v1/auth/refresh")"
  esperar "refresh com Origin http:// do mesmo host (esquema diferente)" 403 "$(status -b "$TMP/cookies" -H "Origin: http://$DOMINIO" -X POST "$BASE/api/v1/auth/refresh")"
  esperar "refresh com Origin de outra origem" 403 "$(status -b "$TMP/cookies" -H 'Origin: https://evil.example' -X POST "$BASE/api/v1/auth/refresh")"
  esperar "refresh da mesma origem com X-Forwarded-Host e X-Forwarded-Proto forjados (o nginx os reescreve)" 200 "$(status -b "$TMP/cookies" -c "$TMP/cookies" -H "Origin: $BASE" -H 'X-Forwarded-Host: evil.example' -H 'X-Forwarded-Proto: http' -X POST "$BASE/api/v1/auth/refresh")"
  esperar "refresh da mesma origem (https://$DOMINIO)" 200 "$(status -b "$TMP/cookies" -c "$TMP/cookies" -H "Origin: $BASE" -X POST "$BASE/api/v1/auth/refresh")"
  cp "$TMP/cookies" "$TMP/cookies-antes-do-logout"
  esperar "logout da mesma origem" 204 "$(status -b "$TMP/cookies" -c "$TMP/cookies" -H "Origin: $BASE" -X POST "$BASE/api/v1/auth/logout")"
  esperar "refresh com o cookie de antes do logout (família revogada)" 401 "$(status -b "$TMP/cookies-antes-do-logout" -H "Origin: $BASE" -X POST "$BASE/api/v1/auth/refresh")"
else
  falha "login por https (sem accessToken)"
fi

echo "== 4. o log do Caddy não traz query string nem o IP inteiro do cliente"
SEGREDO="SEGREDO-DE-TESTE-$$"
cliente "$IP_A" -o /dev/null "$BASE/storage/midia/x?X-Amz-Signature=$SEGREDO&listenerId=$SEGREDO" || true
sleep 1
log_edge="$("${COMPOSE[@]}" logs --no-color edge 2>&1 || true)"
grep -q 'http.log.access' <<<"$log_edge" && ok "o Caddy registra o acesso" || falha "nenhuma linha de acesso no log do Caddy"
grep -qF "$SEGREDO" <<<"$log_edge" && falha "a query string aparece no log do Caddy" || ok "sem query string no log do Caddy"
if grep -qE "\"(remote_ip|client_ip)\":\"($IP_A|$IP_B)\"" <<<"$log_edge" || grep -qF "$IP_A" <<<"$log_edge"; then falha "o IP inteiro do cliente aparece no log do Caddy"; else ok "IP mascarado (/24) no log do Caddy"; fi
grep -q '"request":{[^}]*"headers"' <<<"$log_edge" && falha "cabeçalhos da requisição no log do Caddy" || ok "sem cabeçalhos da requisição no log do Caddy"
# O log de ERRO não se provoca de fora (nenhum erro de upstream sem derrubar o web): prova-se pela configuração que o Caddy realmente carregou. Um único logger, o `default`, atende o acesso E o erro
# (o servidor aponta para ele), e é ele que leva o filtro: query fora da URI, IP mascarado, sem cabeçalhos, e o http.stdlib (IP inteiro no texto) excluído.
if "${COMPOSE[@]}" exec -T edge caddy adapt --config /etc/caddy/Caddyfile --adapter caddyfile 2>/dev/null | python3 -I -c '
import json, sys
d = json.load(sys.stdin)
logs = d["logging"]["logs"]
default = logs["default"]
campos = default["encoder"]["fields"]
servidor = next(iter(d["apps"]["http"]["servers"].values()))
assert list(logs) == ["default"], "há outro logger além do default"
assert default["encoder"]["format"] == "filter"
assert campos["request>uri"]["filter"] == "regexp" and campos["request>uri"]["regexp"] == "\\?.*"
assert campos["request>remote_ip"]["filter"] == "ip_mask" and campos["request>client_ip"]["filter"] == "ip_mask"
assert campos["request>headers"]["filter"] == "delete"
assert default["exclude"] == ["http.stdlib"]
assert all(v == [""] for v in servidor["logs"]["logger_names"].values()), "o servidor não usa o logger default"
'; then ok "o logger default (acesso e erro) carrega o filtro: sem query, IP mascarado, sem cabeçalhos, sem http.stdlib"; else falha "a configuração de log carregada pelo Caddy não tem o filtro esperado"; fi

echo
if [[ $falhas -gt 0 ]]; then echo "smoke-borda: $falhas falha(s)."; exit 1; fi
echo "smoke-borda: tudo ok."
