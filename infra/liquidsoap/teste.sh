#!/usr/bin/env bash
# =============================================================================
# WebRadio — infra/liquidsoap/teste.sh (E1-F15)
# Sobe o radio.liq de verdade (imagem fixada, mesmo hardening do compose) contra o Icecast da F14 e uma
# API falsa (teste/api-falsa.liq), numa rede Docker própria, e confere o critério de aceite e o S-A08/S-A09:
#   1. sem a API no ar o stream toca a playlist de teste;
#   0. segredo vazio, de exemplo ou curto aborta o boot (S-C02), sem imprimir o valor;
#   2. URL fora de http://minio:9000/midia/ é recusada (file:, annotate:, process:, outro bucket, outro host, \n no fim);
#   3. faixa válida da API toca, com título/artista por metadata.map e midiaId/pedidoId no evento;
#   4. o harbor recusa senha errada e a senha certa sobrepõe a programação;
#   5. derrubar a API não derruba o stream; nenhum segredo aparece no log.
# Uso (na raiz do repositório):  infra/liquidsoap/teste.sh
# =============================================================================
set -euo pipefail
export LC_ALL=C

LIQUIDSOAP="savonet/liquidsoap@sha256:8c1918482b76ca11191162a07ab47a7f0e0697e7b1a23e81b55cba0552f61e8e" # v2.4.0, uid 100
ICECAST="webradio-icecast:teste"
P="wr-liq-teste-$$"
TMP="$(mktemp -d)"
FALHAS=0
ENDURECIDO=(--read-only --cap-drop ALL --security-opt no-new-privileges:true --tmpfs /tmp:uid=100,gid=101 -e TZ=UTC)

limpar() {
  docker rm -f "$P-radio" "$P-icecast" "$P-api" "$P-fonte" >/dev/null 2>&1 || true
  docker network rm "$P" >/dev/null 2>&1 || true
  rm -rf "$TMP"
}
trap limpar EXIT

ok()    { echo "  ok    $*"; }
falha() { echo "  FALHA $*" >&2; FALHAS=$((FALHAS + 1)); }
confere() { if "${@:2}"; then ok "$1"; else falha "$1"; fi; }

espera_log() {
  # $1 = contêiner, $2 = texto fixo, $3 = segundos
  for _ in $(seq 1 "$3"); do
    docker logs "$1" 2>&1 | grep -q -F -e "$2" && return 0
    sleep 1
  done
  return 1
}

toca() {
  # O Icecast entrega áudio: content-type audio/mpeg e bytes chegando em 4 s.
  rm -f "$TMP/amostra.mp3" "$TMP/cabecalhos"
  curl -s -m 4 -D "$TMP/cabecalhos" -o "$TMP/amostra.mp3" "$BASE/radio.mp3" || true
  grep -q -i '^content-type: audio/mpeg' "$TMP/cabecalhos" 2>/dev/null && [[ "$(wc -c < "$TMP/amostra.mp3")" -gt 20000 ]]
}

estatisticas() { curl -s -m 5 -u "operador-radio:$(cat "$TMP/segredos/icecast_admin_password")" "$BASE/admin/stats"; }

titulo_no_ar() { estatisticas | grep -q -F -e "$1"; }

api() {
  # $1 = modo da API falsa; atende como api:8081 e minio:9000.
  docker rm -f "$P-api" >/dev/null 2>&1 || true
  docker run -d --name "$P-api" --network "$P" --network-alias api --network-alias minio "${ENDURECIDO[@]}" \
    -e MOCK_MODO="$1" -v "$PWD/infra/liquidsoap/teste/api-falsa.liq:/etc/liquidsoap/api-falsa.liq:ro" \
    -v "$PWD/infra/liquidsoap/playlist-teste:/musica-teste:ro" -v "$TMP/segredos:/run/secrets:ro" \
    "$LIQUIDSOAP" /etc/liquidsoap/api-falsa.liq >/dev/null
}

fonte_ao_vivo() {
  # $1 = arquivo com a senha do harbor. Um segundo Liquidsoap faz o papel do programa do locutor (BUTT).
  docker rm -f "$P-fonte" >/dev/null 2>&1 || true
  docker run -d --name "$P-fonte" --network "$P" "${ENDURECIDO[@]}" \
    -v "$TMP/fonte.liq:/etc/liquidsoap/fonte.liq:ro" -v "$1:/run/secrets/senha:ro" \
    "$LIQUIDSOAP" /etc/liquidsoap/fonte.liq >/dev/null
}

mkdir -p "$TMP/segredos"
for s in icecast_source_password icecast_admin_password icecast_relay_password playout_token liquidsoap_harbor_password; do
  printf '%s' "$s-$(openssl rand -hex 16)" > "$TMP/segredos/$s"
done
printf '%s' "operador-radio" > "$TMP/segredos/icecast_admin_user"
printf '%s' "senha-errada-$(openssl rand -hex 12)" > "$TMP/senha-errada"
chmod 0755 "$TMP" "$TMP/segredos"
chmod 0644 "$TMP/segredos"/* "$TMP/senha-errada"
cat > "$TMP/fonte.liq" <<'LIQ'
settings.log.stdout := true
settings.log.file := false
senha = string.trim(file.contents("/run/secrets/senha"))
ao_vivo = metadata.map(fun (_) -> [("title", "Programa ao vivo"), ("artist", "Locutor")], sine(880.))
output.icecast(%mp3(bitrate=128), host="liquidsoap", port=8005, password=senha, mount="live", fallible=true, ao_vivo)
LIQ
chmod 0644 "$TMP/fonte.liq"

echo "== segredo ruim aborta o boot (S-C02)"
segredo_ruim() {
  # $1 = arquivo de segredo, $2 = conteúdo ruim, $3 = trecho esperado na mensagem
  rm -rf "$TMP/ruim"; cp -r "$TMP/segredos" "$TMP/ruim"
  printf '%s' "$2" > "$TMP/ruim/$1"; chmod 0755 "$TMP/ruim"; chmod 0644 "$TMP/ruim"/*
  local saida codigo=0
  saida="$(timeout 90 docker run --rm --network none "${ENDURECIDO[@]}" -v "$PWD/infra/liquidsoap/radio.liq:/etc/liquidsoap/radio.liq:ro" \
    -v "$PWD/infra/liquidsoap/playlist-teste:/musica-teste:ro" -v "$TMP/ruim:/run/secrets:ro" "$LIQUIDSOAP" /etc/liquidsoap/radio.liq 2>&1)" || codigo=$?
  if [[ "$codigo" -ne 0 ]] && grep -q -F -e "segredo $1" <<<"$saida" && grep -q -F -e "$3" <<<"$saida"; then ok "$1: $3"; else falha "$1 ($3): saiu $codigo sem a mensagem esperada"; fi
  if [[ -n "${2// /}" ]] && grep -q -F -e "$2" <<<"$saida"; then falha "$1: a mensagem imprimiu o valor do segredo"; fi
}
segredo_ruim liquidsoap_harbor_password "" "está vazio"
segredo_ruim playout_token "__GERAR__-valor-de-exemplo-que-nao-pode-ir-ao-log" "valor de exemplo"
segredo_ruim icecast_source_password "curta" "menos de 24"

echo "== sobe Icecast e Liquidsoap, sem API"
docker build -q -f infra/icecast/Dockerfile -t "$ICECAST" . >/dev/null
docker network create "$P" >/dev/null
docker run -d --name "$P-icecast" --network "$P" --network-alias icecast --read-only --cap-drop ALL \
  --security-opt no-new-privileges:true --tmpfs /tmp:uid=100,gid=101,mode=0700 \
  -v "$TMP/segredos:/run/secrets:ro" -p 127.0.0.1::8000 "$ICECAST" >/dev/null
BASE="http://$(docker port "$P-icecast" 8000/tcp | head -n1)"
docker run -d --name "$P-radio" --network "$P" --network-alias liquidsoap "${ENDURECIDO[@]}" \
  -v "$PWD/infra/liquidsoap/radio.liq:/etc/liquidsoap/radio.liq:ro" \
  -v "$PWD/infra/liquidsoap/playlist-teste:/musica-teste:ro" -v "$TMP/segredos:/run/secrets:ro" \
  "$LIQUIDSOAP" /etc/liquidsoap/radio.liq >/dev/null

for _ in $(seq 1 40); do toca && break; sleep 1; done
confere "stream toca a playlist de teste sem a API (audio/mpeg)" toca
confere "roda sem root" test "$(docker exec "$P-radio" id -u)" != "0"
confere "log: API indisponível, segue com o fallback" espera_log "$P-radio" "API indisponível" 20

echo "== API devolve URL fora do prefixo (S-A09)"
api maliciosa
confere "URL maliciosa recusada" espera_log "$P-radio" "URL da API recusada" 30
sleep 50 # ~9 consultas (retry de 5 s): file:, annotate:, process:, ../, %2e%2e, outro bucket, outro host, \n no fim
confere "todas as variações foram recusadas" test "$(docker logs "$P-radio" 2>&1 | grep -c -F 'URL da API recusada')" -ge 9
if docker logs "$P-api" 2>&1 | grep -q -F "MIDIA servida"; then falha "o Liquidsoap buscou mídia de URL recusada"; else ok "nenhuma URL recusada foi buscada"; fi
if titulo_no_ar "nao deve tocar"; then falha "faixa de URL recusada foi ao ar"; else ok "faixa de URL recusada não foi ao ar"; fi
confere "stream continua tocando" toca
confere "evento de faixa da rotação local chega à API com o token" espera_log "$P-api" "EVENTO auth=ok" 30
if docker logs "$P-api" 2>&1 | grep -q -F "auth=ERRADO"; then falha "chamada à API sem o token certo"; else ok "toda chamada à API leva o Bearer do playout"; fi

echo "== API devolve faixa válida"
api valida
confere "a mídia da API é buscada no MinIO interno" espera_log "$P-api" "MIDIA servida" 40
confere "evento leva o pedidoId da faixa" espera_log "$P-api" '"pedidoId":"33333333-3333-3333-3333-333333333333"' 40
EVENTO="$(docker logs "$P-api" 2>&1 | grep -F '33333333-3333-3333-3333-333333333333' | tail -n1)"
confere "evento leva o midiaId" grep -q -F '"midiaId":"22222222-2222-2222-2222-222222222222"' <<<"$EVENTO"
confere "título chega como dado, com aspas e dois-pontos preservados" grep -q -F 'annotate:title=x' <<<"$EVENTO"
if grep -q -F '\n' <<<"$EVENTO"; then falha "caractere de controle no título do evento"; else ok "caractere de controle removido do título"; fi
confere "artista da API no evento" grep -q -F '"artista":"Banda da API"' <<<"$EVENTO"
confere "ocorridoEmUtc em ISO-8601 com Z" grep -q -E '"ocorridoEmUtc":"[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z"' <<<"$EVENTO"

echo "== ao vivo pelo harbor (S-A08)"
fonte_ao_vivo "$TMP/senha-errada"
sleep 8
if docker logs "$P-radio" 2>&1 | grep -q -F "Source client conectado"; then falha "harbor aceitou senha errada"; else ok "harbor recusa senha errada"; fi
fonte_ao_vivo "$TMP/segredos/liquidsoap_harbor_password"
confere "harbor aceita a senha certa" espera_log "$P-radio" "Source client conectado" 30
confere "o ao vivo sobrepõe a programação" espera_log "$P-radio" "Switch to input.harbor" 40
confere "stream continua tocando durante o ao vivo" toca
docker rm -f "$P-fonte" >/dev/null
confere "log da desconexão do harbor" espera_log "$P-radio" "Source client desconectado" 30

echo "== derruba a API"
docker rm -f "$P-api" >/dev/null
sleep 8
confere "stream continua sem a API" toca
confere "o Liquidsoap segue de pé" test "$(docker inspect -f '{{.State.Running}}' "$P-radio")" = "true"

docker logs "$P-radio" > "$TMP/radio.log" 2>&1
VAZOU=0
for s in playout_token icecast_source_password liquidsoap_harbor_password; do
  if grep -q -F -e "$(cat "$TMP/segredos/$s")" "$TMP/radio.log"; then VAZOU=1; fi
done
confere "nenhum segredo no log do Liquidsoap" test "$VAZOU" = "0"

echo
if [[ "$FALHAS" -eq 0 ]]; then
  echo "liquidsoap: tudo certo."
else
  echo "liquidsoap: $FALHAS falha(s)." >&2
  docker logs --tail 60 "$P-radio" >&2 || true
  exit 1
fi
