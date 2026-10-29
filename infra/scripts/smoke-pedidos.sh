#!/usr/bin/env bash
# =============================================================================
# WebRadio — infra/scripts/smoke-pedidos.sh (E3-F08, Definição de Pronto do Épico 3: itens 2 e 3)
#
# Roda contra o stack de DEV já no ar (o mesmo do smoke-compose.sh), ATRÁS do nginx:
#   docker compose -f docker-compose.yml -f docker-compose.dev.yml up -d --wait
#   infra/scripts/smoke-pedidos.sh
#
#   2. Throttle do pedido: o segundo pedido do mesmo listenerId em 5 min → 429 com Retry-After e retryAfterSeconds iguais; o quarto
#      pedido de listenerIds diferentes no mesmo IP → 429; um pedido de OUTRA origem logo depois → 201; a origem já no limite, agora com
#      um X-Forwarded-For forjado (outro IP), continua 429. O nginx SOBRESCREVE o X-Forwarded-For com o IP do socket (S-A11), então as
#      duas origens são sockets de verdade: dois contêineres `curl` com IPs fixos diferentes numa rede que o script cria com subnet
#      própria e conecta ao `web` (a `borda` do compose não tem subnet, e o Docker só aceita --ip com subnet). O `curl` do host não
#      serve: chega sempre com o IP do gateway da bridge. Com o KnownIPNetworks errado as duas origens viram o IP do nginx e o
#      passo da outra origem falha.
#   3. Redis parado: o throttle cai para o SQL, o /health/ready fica Degraded e o segundo pedido do mesmo dispositivo continua 429.
#   7. Imagem de divulgação (precisa de E2E_ADMIN_SENHA_NOVA, a senha que o smoke do ao vivo deixou no Admin): um JPEG com EXIF de GPS volta WebP sem
#      metadado; WebP, PNG animado, SVG, GIF, HTML renomeado, lado acima de 4096 px e arquivo acima de 2 MB são recusados; a imagem é servida por
#      /storage/publico/ com image/webp, nosniff e CSP com sandbox; a listagem do bucket é negada; apagar a divulgação apaga a imagem; e as
#      políticas do MinIO valem (a API só escreve em publico/divulgacoes/, o Worker só lista e apaga lá).
#
# O `curl` vem da imagem do próprio `web` (webradio-web:local, já construída pelo compose; a base é fixada por digest e o bloco apk
# do Dockerfile mantém curl/libcurl corrigidos). O `docker run` usa --pull never: sem a imagem o smoke falha na hora e nunca baixa nada. O orçamento de throttle é dividido com o E2E: o
# `trap` de saída apaga wr:throttle:pedido:* e deixa o Redis de pé (o E2E do Playwright faz o mesmo antes de cada teste de pedidos).
# A senha do Redis é lida DENTRO do contêiner, nunca em argv nem em log do host. Acumula as falhas e sai com 1 se houve alguma.
# =============================================================================
set -euo pipefail
export LC_ALL=C

RAIZ="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$RAIZ"
COMPOSE=(docker compose -f docker-compose.yml -f docker-compose.dev.yml)
IMAGEM_CURL="${SMOKE_IMAGEM_CURL:-webradio-web:local}"
REDE="smoke-pedidos-$$"
SUBREDE="${SMOKE_SUBREDE:-172.31.77}" # só os três primeiros octetos; troque se colidir com outra rede do runner
ORIGEM_A=11
ORIGEM_B=12
ORIGEM_C=13
# Host que o nginx aceita (server_name = PUBLIC_HOST); o proxy repassa ao upstream o PUBLIC_AUTHORITY fixo.
AUTORIDADE="${SMOKE_AUTORIDADE:-$(grep -m1 '^PUBLIC_AUTHORITY=' .env 2>/dev/null | cut -d= -f2- || true)}"
AUTORIDADE="${AUTORIDADE:-localhost:8080}"
CORPO='{"nomeOuvinte":"Smoke","tituloMusica":"Garota de Ipanema","artista":"Tom Jobim","mensagem":"smoke do Epico 3"}'

BASE="${SMOKE_BASE:-http://localhost:8080}"
TMP="$(mktemp -d)"
: "${E2E_ADMIN_SENHA_NOVA:?Defina E2E_ADMIN_SENHA_NOVA (a senha do Admin depois do smoke do ao vivo; o DoD 7 loga com ela)}"
[[ "${GITHUB_ACTIONS:-}" == "true" ]] && echo "::add-mask::$E2E_ADMIN_SENHA_NOVA"

WEB_CID=""
REDE_CRIADA=0
REDIS_PARADO=0

# A senha do Redis é lida DENTRO do contêiner. O padrão é o do RedisRequestThrottle (prefixo wr: + throttle:pedido:).
limpar_chaves() {
  "${COMPOSE[@]}" exec -T redis sh -c 'export REDISCLI_AUTH="$(cat /run/secrets/redis_password)"; redis-cli --scan --pattern "wr:throttle:pedido:*" | xargs -r redis-cli DEL; redis-cli --scan --pattern "wr:throttle:divimg:*" | xargs -r redis-cli DEL' >/dev/null 2>&1
}
chaves_redis() {
  "${COMPOSE[@]}" exec -T redis sh -c 'export REDISCLI_AUTH="$(cat /run/secrets/redis_password)"; redis-cli --scan --pattern "wr:throttle:pedido:*"' 2>/dev/null | tr -d '\r'
}
chaves_do_upload() {
  "${COMPOSE[@]}" exec -T redis sh -c 'export REDISCLI_AUTH="$(cat /run/secrets/redis_password)"; redis-cli --scan --pattern "wr:throttle:divimg:*"' 2>/dev/null | tr -d '\r'
}

limpar() {
  rm -rf "$TMP"
  # O DoD 3 para o Redis: se o script morrer no meio, o stack não pode ficar sem ele para quem vem depois (o E2E do CI).
  if [[ "$REDIS_PARADO" == 1 ]]; then "${COMPOSE[@]}" start redis >/dev/null 2>&1 || true; fi
  limpar_chaves || true # no trap é tolerante: o Redis pode nem estar de pé
  if [[ -n "$WEB_CID" && "$REDE_CRIADA" == 1 ]]; then docker network disconnect -f "$REDE" "$WEB_CID" >/dev/null 2>&1 || true; fi
  if [[ "$REDE_CRIADA" == 1 ]]; then docker network rm "$REDE" >/dev/null 2>&1 || true; fi
}
trap limpar EXIT

falhas=0
ok()    { printf '  ok    %s\n' "$1"; }
falha() { printf '  FALHA %s\n' "$1"; falhas=$((falhas + 1)); }
confere() { if "${@:2}"; then ok "$1"; else falha "$1"; fi; }
espera_ate() { # $1 segundos, resto = comando
  local t="$1"; shift
  for _ in $(seq 1 "$t"); do "$@" && return 0; sleep 1; done
  return 1
}
uuid() { cat /proc/sys/kernel/random/uuid; }

# --- a rede das duas origens ------------------------------------------------------------------------------------------
WEB_CID="$("${COMPOSE[@]}" ps -q web)"
[[ -n "$WEB_CID" ]] || { echo "smoke-pedidos: o contêiner web não está no ar."; exit 1; }
docker network create --subnet "$SUBREDE.0/24" "$REDE" >/dev/null
REDE_CRIADA=1
docker network connect "$REDE" "$WEB_CID"
WEB_IP="$(docker inspect -f "{{(index .NetworkSettings.Networks \"$REDE\").IPAddress}}" "$WEB_CID")"
[[ -n "$WEB_IP" ]] || { echo "smoke-pedidos: o web não ganhou IP na rede $REDE."; exit 1; }
# Padrão errado, senha errada ou Redis fora apagariam zero chaves em silêncio e o E2E herdaria o limite por IP: aqui a limpeza inicial
# tem que funcionar e deixar 0 chaves, senão o script falha.
redis_responde() { "${COMPOSE[@]}" exec -T redis sh -c 'export REDISCLI_AUTH="$(cat /run/secrets/redis_password)"; redis-cli ping' 2>/dev/null | grep -q PONG; }
# Redis recusando (senha errada) não pode passar por "zero chaves": o ping vem antes de contar.
if ! redis_responde || ! limpar_chaves || [[ -n "$(chaves_redis)" ]]; then
  echo "smoke-pedidos: não consegui limpar as chaves wr:throttle:pedido:* do Redis (Redis fora, senha ou padrão errados)."
  exit 1
fi

# STATUS, RETRY (cabeçalho Retry-After) e RESPOSTA (corpo) da última chamada.
STATUS=""; RETRY=""; RESPOSTA=""
pedir() { # $1 final do IP da origem, $2 X-Listener-Id, demais = argumentos extras do curl (ex.: -H 'X-Forwarded-For: ...')
  local origem="$1" ouvinte="$2"; shift 2
  local saida
  saida="$(docker run --rm --pull never --network "$REDE" --ip "$SUBREDE.$origem" --read-only --cap-drop ALL \
    --security-opt no-new-privileges:true --user 101:101 --entrypoint curl "$IMAGEM_CURL" \
    -s -i --max-time 20 -X POST -H 'Content-Type: application/json' -H "Host: $AUTORIDADE" -H "X-Listener-Id: $ouvinte" "$@" \
    --data "$CORPO" "http://$WEB_IP:8080/api/v1/pedidos" | tr -d '\r' || true)"
  STATUS="$(printf '%s\n' "$saida" | head -n1 | cut -d' ' -f2)"
  RETRY="$(printf '%s\n' "$saida" | grep -i -m1 '^retry-after:' | cut -d: -f2 | tr -d ' ' || true)"
  RESPOSTA="$(printf '%s\n' "$saida" | sed '1,/^$/d')"
}
json() { python3 -I -c 'import json,sys; d=json.loads(sys.stdin.read()); print(eval(sys.argv[1], {"d": d}))' "$1"; }
campo() { json "$1" <<<"$RESPOSTA" 2>/dev/null || true; }
status_e() { [[ "$STATUS" == "$1" ]]; }
limite_bate() { # Retry-After e retryAfterSeconds iguais e entre 1 e 300
  [[ -n "$RETRY" && "$RETRY" == "$(campo 'd["retryAfterSeconds"]')" && "$RETRY" -ge 1 && "$RETRY" -le 300 ]]
}
tipo_e() { [[ "$(campo 'd["type"]')" == "$1" ]]; }

LA="$(uuid)"; LB="$(uuid)"; LC="$(uuid)"; LD="$(uuid)"; LE="$(uuid)"; LF="$(uuid)"; LG="$(uuid)"

echo "== DoD 2: throttle atrás do nginx, com duas origens de verdade"
pedir "$ORIGEM_A" "$LA"
confere "1º pedido do listenerId A (origem A) → 201" status_e 201
confere "o PedidoDto não tem hash nem usuário" test -z "$(printf '%s' "$RESPOSTA" | grep -i -e hash -e usuarioId || true)"
pedir "$ORIGEM_A" "$LA"
confere "2º pedido do mesmo listenerId em 5 min → 429" status_e 429
confere "429 com Retry-After igual ao retryAfterSeconds" limite_bate
confere "429 com o tipo /erros/muitos-pedidos" tipo_e /erros/muitos-pedidos
pedir "$ORIGEM_A" "$LB"
confere "listenerId B no mesmo IP → 201" status_e 201
pedir "$ORIGEM_A" "$LC"
confere "listenerId C no mesmo IP → 201" status_e 201
pedir "$ORIGEM_A" "$LD"
confere "4º listenerId diferente no mesmo IP → 429" status_e 429
confere "429 do IP com Retry-After igual ao retryAfterSeconds" limite_bate
pedir "$ORIGEM_B" "$LE"
confere "pedido de OUTRA origem logo depois → 201" status_e 201
pedir "$ORIGEM_A" "$LF" -H 'X-Forwarded-For: 192.0.2.99'
confere "origem A no limite com X-Forwarded-For forjado continua 429" status_e 429

echo "== DoD 3: com o Redis parado o throttle cai para o SQL (Degraded) e o 2º pedido do mesmo dispositivo segue 429"
saude() { "${COMPOSE[@]}" exec -T api wget -q -O - http://127.0.0.1:8081/health/ready 2>/dev/null | python3 -I -c 'import json,sys; print(json.loads(sys.stdin.read())["status"])' 2>/dev/null || true; }
saude_e() { [[ "$(saude)" == "$1" ]]; }
confere "antes: /health/ready está Healthy" saude_e Healthy
REDIS_PARADO=1 # antes do stop: se o script morrer no meio, o trap religa o Redis
"${COMPOSE[@]}" stop redis >/dev/null 2>&1 || falha "docker compose stop redis"
confere "o Redis está parado" test -z "$("${COMPOSE[@]}" ps -q --status running redis)"
confere "/health/ready fica Degraded (não Unhealthy: a API segue servindo)" espera_ate 40 saude_e Degraded
pedir "$ORIGEM_C" "$LG"
confere "1º pedido do dispositivo G com o Redis fora (fallback SQL) → 201" status_e 201
pedir "$ORIGEM_C" "$LG"
confere "2º pedido do mesmo dispositivo com o Redis fora → 429" status_e 429
confere "429 do fallback com Retry-After igual ao retryAfterSeconds" limite_bate
confere "segue Degraded" saude_e Degraded
"${COMPOSE[@]}" start redis >/dev/null 2>&1 && REDIS_PARADO=0
redis_saudavel() { [[ "$(docker inspect -f '{{.State.Health.Status}}' "$("${COMPOSE[@]}" ps -q redis)" 2>/dev/null || true)" == healthy ]]; }
confere "o Redis volta a ficar healthy" espera_ate 60 redis_saudavel

# O disjuntor da API fica aberto até 30 s depois da última falha do Redis, e nesse tempo o throttle conta pedidos no SQL: o E2E que roda
# logo depois estouraria o limite por IP do banco. Fica aqui até a API voltar a usar o Redis (uma sonda depois dos 30 s cria chave nele).
limpar_chaves || falha "limpar as chaves do throttle depois de religar o Redis"
LH=""
api_voltou_ao_redis() { LH="$(uuid)"; pedir "$ORIGEM_B" "$LH"; [[ -n "$(chaves_redis)" ]]; }
confere "a API volta a usar o Redis (o disjuntor fecha em até ~30 s)" espera_ate 75 api_voltou_ao_redis

echo "== LGPD: nenhum listenerId nem IP de origem nas chaves do Redis"
chaves="$(chaves_redis)"
confere "há chaves reais do throttle para conferir" test -n "$chaves"
for valor in "$LA" "$LB" "$LC" "$LE" "$LG" "$LH" "$SUBREDE.$ORIGEM_A" "$SUBREDE.$ORIGEM_B" "$SUBREDE.$ORIGEM_C"; do
  confere "ausente nas chaves: ${valor:0:8}…" test -z "$(grep -F -m1 -e "$valor" <<<"$chaves" || true)"
done

echo "== LGPD: nenhum listenerId nem IP de origem nos logs do nginx e da API"
logs="$("${COMPOSE[@]}" logs --no-color web api 2>&1 || true)"
for valor in "$LA" "$LB" "$LC" "$LE" "$LG" "$LH" "$SUBREDE.$ORIGEM_A" "$SUBREDE.$ORIGEM_B" "$SUBREDE.$ORIGEM_C"; do
  confere "ausente nos logs: ${valor:0:8}…" test -z "$(grep -F -m1 -e "$valor" <<<"$logs" || true)"
done


echo "== DoD 7: imagem de divulgação (recodificação, recusas, headers, ciclo de vida, políticas do MinIO)"
EMAIL_ADMIN="$(cat secrets/seed_admin_email)"
python3 -I -c 'import json,sys; print(json.dumps({"email": sys.argv[1], "senha": sys.argv[2]}))' "$EMAIL_ADMIN" "$E2E_ADMIN_SENHA_NOVA" > "$TMP/login.json"
ADMIN="$(curl -s --max-time 15 -X POST -H 'Content-Type: application/json' --data-binary "@$TMP/login.json" "$BASE/api/v1/auth/login" | python3 -I -c 'import json,sys; print(json.loads(sys.stdin.read()).get("accessToken",""))' 2>/dev/null || true)"
if [[ -z "$ADMIN" ]]; then
  falha "login do Admin com a senha nova (o smoke do ao vivo rodou antes?)"
else
  [[ "${GITHUB_ACTIONS:-}" == "true" ]] && echo "::add-mask::$ADMIN"
  ok "Admin autenticado"

  # Arquivos de fixture: o JPEG com GPS vem do repositório (bytes de um JPEG de verdade); o resto é montado aqui, em bytes.
  cp infra/scripts/fixtures/jpeg-com-gps.jpg "$TMP/gps.jpg"
  python3 -I - "$TMP" <<'PY'
import struct, sys, zlib
d = sys.argv[1]
def chunk(t, b): return struct.pack(">I", len(b)) + t + b + struct.pack(">I", zlib.crc32(t + b) & 0xFFFFFFFF)
def ihdr(w, h): return chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 2, 0, 0, 0))
sig = b"\x89PNG\r\n\x1a\n"
raw = b"".join(b"\x00" + b"\xff\x00\x00" * 4 for _ in range(4))
idat = zlib.compress(raw)
fctl = lambda n: chunk(b"fcTL", struct.pack(">IIIIIHHBB", n, 4, 4, 0, 0, 1, 10, 0, 0))
open(d + "/ok.png", "wb").write(sig + ihdr(4, 4) + chunk(b"IDAT", idat) + chunk(b"IEND", b""))
open(d + "/animado.png", "wb").write(sig + ihdr(4, 4) + chunk(b"acTL", struct.pack(">II", 2, 0)) + fctl(0) + chunk(b"IDAT", idat)
    + fctl(1) + chunk(b"fdAT", struct.pack(">I", 2) + idat) + chunk(b"IEND", b""))
open(d + "/bomba.png", "wb").write(sig + ihdr(10000, 10000) + chunk(b"IDAT", idat) + chunk(b"IEND", b""))
open(d + "/lado-4097.png", "wb").write(sig + ihdr(4097, 8) + chunk(b"IDAT", idat) + chunk(b"IEND", b""))
open(d + "/webp.bin", "wb").write(b"RIFF\x1a\x00\x00\x00WEBPVP8 \x0e\x00\x00\x00" + b"\x00" * 14)
open(d + "/gif.bin", "wb").write(b"GIF89a\x01\x00\x01\x00\x80\x00\x00\xff\xff\xff\x00\x00\x00;")
open(d + "/svg.bin", "wb").write(b'<svg xmlns="http://www.w3.org/2000/svg"><script>alert(1)</script></svg>')
open(d + "/html.jpg", "wb").write(b"<html><script>alert(1)</script></html>")
open(d + "/grande.jpg", "wb").write(b"\xff\xd8\xff" + b"\x00" * (2 * 1024 * 1024 + 200 * 1024))
PY

  imagem() { # $1 arquivo; deixa o corpo em $TMP/resp.json e imprime o status ("000" se a conexão caiu)
    curl -s --max-time 30 -o "$TMP/resp.json" -w '%{http_code}' -X POST -H "Authorization: Bearer $ADMIN" \
      -F "file=@$1;type=image/jpeg;filename=foto.jpg" "$BASE/api/v1/divulgacoes/imagens" || true
  }
  status_da_imagem() { [[ "$(imagem "$1")" == "$2" ]]; }

  # --- o envio que dá certo: JPEG com GPS (orientação 6 no EXIF) volta WebP sem metadado
  [[ "$(imagem "$TMP/gps.jpg")" == 201 ]] && ok "JPEG com EXIF de GPS → 201" || falha "JPEG com EXIF de GPS → 201"
  CHAVE="$(json 'd["chave"]' < "$TMP/resp.json" 2>/dev/null || true)"
  URL_IMG="$(json 'd["imagemUrl"]' < "$TMP/resp.json" 2>/dev/null || true)"
  confere "a chave tem o formato divulgacoes/{guid}.webp" test -n "$(grep -E -m1 '^divulgacoes/[0-9a-f]{32}\.webp$' <<<"$CHAVE" || true)"
  curl -s --max-time 15 -D "$TMP/cab.txt" -o "$TMP/saida.webp" "$BASE/storage/publico/$CHAVE" || true
  CAB="$(tr -d '\r' < "$TMP/cab.txt" 2>/dev/null || true)"
  confere "a imagem é servida por /storage/publico/ com 200" test "$(head -n1 <<<"$CAB" | cut -d' ' -f2)" = 200
  confere "Content-Type: image/webp" test -n "$(grep -i -m1 '^content-type: image/webp' <<<"$CAB" || true)"
  confere "X-Content-Type-Options: nosniff" test -n "$(grep -i -m1 '^x-content-type-options: nosniff' <<<"$CAB" || true)"
  confere "algum Content-Security-Policy contém sandbox" test -n "$(grep -i '^content-security-policy:' <<<"$CAB" | grep -i -m1 sandbox || true)"
  confere "a URL devolvida aponta para o mesmo objeto" test "${URL_IMG##*/storage/publico/}" = "$CHAVE"
  python3 -I - "$TMP/saida.webp" <<'PY' && ok "bytes de saída: RIFF/WEBP sem chunk EXIF, XMP ou ICCP e sem o EXIF de origem" || falha "bytes de saída: RIFF/WEBP sem chunk EXIF, XMP ou ICCP e sem o EXIF de origem"
import struct, sys
b = open(sys.argv[1], "rb").read()
assert b[:4] == b"RIFF" and b[8:12] == b"WEBP", "não é WebP"
p, tipos = 12, []
while p + 8 <= len(b):
    tipos.append(b[p:p + 4]); p += 8 + struct.unpack("<I", b[p + 4:p + 8])[0] + (struct.unpack("<I", b[p + 4:p + 8])[0] & 1)
assert not ({b"EXIF", b"XMP ", b"ICCP"} & set(tipos)), tipos
assert b"Exif" not in b
PY

  # --- as recusas (400 antes de qualquer decode; 413 acima de 2 MB)
  for caso in animado.png bomba.png lado-4097.png webp.bin gif.bin svg.bin html.jpg; do
    confere "recusado com 400: $caso" status_da_imagem "$TMP/$caso" 400
  done
  confere "arquivo acima de 2 MB → 413" status_da_imagem "$TMP/grande.jpg" 413
  confere "PNG simples é aceito (201)" status_da_imagem "$TMP/ok.png" 201
  confere "sem token → 401" test "$(curl -s -o /dev/null -w '%{http_code}' -X POST -F "file=@$TMP/gps.jpg" "$BASE/api/v1/divulgacoes/imagens")" = 401

  # --- o caminho em MAIÚSCULAS não chega à API: o location /api/ do nginx diferencia maiúsculas, então ele cai na SPA (location /). Seguro: um corpo
  # maior que o limite do nginx nunca alcança a API por ali. (O critério do 07, "cai no /api/ e leva 413", estava errado; o comportamento real é este.)
  leitura="$(curl -s -o /dev/null -w '%{http_code} %{content_type}' --max-time 15 -X POST -H "Authorization: Bearer $ADMIN" -F "file=@$TMP/gps.jpg" "$BASE/API/V1/DIVULGACOES/IMAGENS" || true)"
  fora_da_api() { [[ "${leitura%% *}" != 2* && "${leitura#* }" != *json* ]]; }
  confere "POST em /API/V1/DIVULGACOES/IMAGENS não é 2xx nem resposta JSON da API (veio $leitura)" fora_da_api

  # --- listagem do bucket público negada (nunca lista, S-A05)
  for caminho in /storage/publico/ /storage/publico/divulgacoes/ '/storage/publico/divulgacoes/?list-type=2' '/storage/publico/?list-type=2'; do
    codigo="$(curl -s -o /dev/null -w '%{http_code}' --max-time 10 "$BASE$caminho")"
    confere "GET $caminho → 403/404 (veio $codigo)" test "$codigo" = 403 -o "$codigo" = 404
  done

  # --- ciclo de vida: a divulgação usa a imagem, aparece nas ativas, e apagá-la apaga a imagem do bucket
  python3 -I -c 'import json,sys; print(json.dumps({"titulo": "[smoke] imagem", "mensagem": "texto puro", "imagemChave": sys.argv[1], "prioridade": 1, "ativo": True}))' "$CHAVE" > "$TMP/div.json"
  codigo="$(curl -s -o "$TMP/div-resp.json" -w '%{http_code}' --max-time 15 -X POST -H "Authorization: Bearer $ADMIN" -H 'Content-Type: application/json' --data-binary "@$TMP/div.json" "$BASE/api/v1/divulgacoes")"
  confere "divulgação com a imagem → 201" test "$codigo" = 201
  DIV_ID="$(json 'd["id"]' < "$TMP/div-resp.json" 2>/dev/null || true)"
  confere "a mesma imagem em outra divulgação → 409" test "$(curl -s -o /dev/null -w '%{http_code}' --max-time 15 -X POST -H "Authorization: Bearer $ADMIN" -H 'Content-Type: application/json' --data-binary "@$TMP/div.json" "$BASE/api/v1/divulgacoes")" = 409
  confere "/divulgacoes/ativas lista a imagem com imagemUrl" test -n "$(curl -s --max-time 10 "$BASE/api/v1/divulgacoes/ativas" | grep -F -m1 "$CHAVE" || true)"
  [[ -n "$DIV_ID" ]] && confere "DELETE da divulgação → 204" test "$(curl -s -o /dev/null -w '%{http_code}' --max-time 15 -X DELETE -H "Authorization: Bearer $ADMIN" "$BASE/api/v1/divulgacoes/$DIV_ID")" = 204
  confere "a imagem some do bucket depois do DELETE" test "$(curl -s -o /dev/null -w '%{http_code}' --max-time 10 "$BASE/storage/publico/$CHAVE")" = 404

  # --- políticas do MinIO (svc-api e svc-worker), com a credencial de cada um, pelo namespace de rede do próprio MinIO
  MINIO_CID="$("${COMPOSE[@]}" ps -q minio)"
  if [[ -z "$MINIO_CID" ]] || ! docker image inspect webradio-minio-init:local >/dev/null 2>&1; then
    falha "contêiner do MinIO ou imagem webradio-minio-init:local ausentes para conferir as políticas"
  else
    for papel in api worker; do
      printf 'MC_HOST_svc=http://%s:%s@127.0.0.1:9000\n' "$(cat "secrets/minio_${papel}_access_key")" "$(cat "secrets/minio_${papel}_secret_key")" > "$TMP/mc-$papel.env"
      chmod 0600 "$TMP/mc-$papel.env"
    done
    mc_como() { # $1 papel, demais = argumentos do mc (stdin para `mc pipe`); a credencial vai por --env-file, nunca em argv
      local papel="$1"; shift
      docker run --rm -i --pull never --network "container:$MINIO_CID" --user 65532:65532 --read-only --tmpfs /tmp --cap-drop ALL \
        --security-opt no-new-privileges:true --env-file "$TMP/mc-$papel.env" -e HOME=/tmp -e MC_CONFIG_DIR=/tmp/.mc \
        --entrypoint mc webradio-minio-init:local "$@" >/dev/null 2>&1
    }
    gravar_como() { echo x | mc_como "$1" pipe "svc/publico/$2"; }
    nega() { ! "$@"; }
    confere "svc-api grava em publico/divulgacoes/" gravar_como api divulgacoes/politica.bin
    confere "svc-api NÃO grava em publico/ fora de divulgacoes/" nega gravar_como api fora-politica.bin
    confere "svc-worker lista publico/divulgacoes/" mc_como worker ls svc/publico/divulgacoes/
    confere "svc-worker NÃO lista publico/ fora do prefixo divulgacoes/" nega mc_como worker ls svc/publico/
    confere "svc-worker NÃO apaga em publico/ fora de divulgacoes/" nega mc_como worker rm --force svc/publico/fora-politica.bin
    confere "svc-worker apaga em publico/divulgacoes/" mc_como worker rm svc/publico/divulgacoes/politica.bin

    # O código do Worker (SDK .NET: ListObjectsV2 com o prefixo, sob a política real) também funciona: a manutenção roda ao subir o Worker, e se o SDK
    # fizesse qualquer chamada além do que a política deixa (ex.: HEAD no bucket) a limpeza falharia todo dia só com um aviso, e as órfãs nunca sairiam.
    logs_worker() { "${COMPOSE[@]}" logs --no-color worker 2>&1 || true; }
    manutencao_rodou() { grep -F -q 'WORKER_MANUTENCAO:' <<<"$(logs_worker)"; }
    confere "o Worker rodou a manutenção (que inclui a limpeza das imagens órfãs)" espera_ate 90 manutencao_rodou
    confere "e a limpeza listou o bucket com a credencial do svc-worker (sem aviso WORKER_MANUTENCAO_IMAGENS)" test -z "$(grep -F -m1 'WORKER_MANUTENCAO_IMAGENS' <<<"$(logs_worker)" || true)"
  fi
fi

echo
if ((falhas > 0)); then
  echo "smoke-pedidos: $falhas verificação(ões) falharam."
  exit 1
fi
echo "smoke-pedidos: tudo certo."
