#!/usr/bin/env bash
# =============================================================================
# WebRadio — infra/liquidsoap/teste.sh (E1-F15)
# Sobe o radio.liq de verdade (imagem fixada, mesmo hardening do compose) contra o Icecast da F14 e uma
# API falsa (teste/api-falsa.liq), numa rede Docker própria, e confere o critério de aceite e o S-A08/S-A09:
#   1. sem a API no ar o stream toca a playlist de teste;
#   0. segredo vazio, de exemplo ou curto aborta o boot (S-C02), sem imprimir o valor;
#   2. URL fora de http://minio:9000/midia/ é recusada (file:, annotate:, process:, outro bucket, outro host, \n no fim);
#   3. faixa válida da API toca, com título/artista por metadata.map e midiaId/pedidoId no evento;
#   4. ao vivo (E2-F08): o harbor autentica cada locutor na API (credencial de transmissão, janela do programa), recusa formato
#      inválido sem chamar a API, derruba em até 30 s quando o programa acaba e segue a falha aberta com teto; metadado inválido
#      do source client não vai ao ar; o stream de saída não cai em nenhum desses casos;
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
  # LOG_RADIO=/caminho: guarda o log do radio.liq e as estatísticas do Icecast antes de apagar tudo (para depurar uma falha).
  if [[ -n "${LOG_RADIO:-}" ]]; then
    docker logs "$P-radio" > "$LOG_RADIO" 2>&1 || true
    estatisticas > "$LOG_RADIO.stats" 2>&1 || true
  fi
  [[ -n "${CONTINUO:-}" ]] && kill "$CONTINUO" 2>/dev/null || true
  docker rm -f "$P-radio" "$P-icecast" "$P-api" "$P-fonte" "$P-fonte2" >/dev/null 2>&1 || true
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
    -v "$PWD/infra/liquidsoap/playlist-teste:/musica-teste:ro" -v "$TMP/segredos:/run/secrets:ro" -v "$TMP/controle:/controle:ro" \
    "$LIQUIDSOAP" /etc/liquidsoap/api-falsa.liq >/dev/null
}

fonte_ao_vivo() {
  # $1 = nome do contêiner, $2 = usuário, $3 = arquivo com a senha, $4 = título (opcional). Um segundo Liquidsoap faz o papel do
  # programa do locutor (BUTT): reconecta sozinho a cada poucos segundos, como o BUTT.
  docker rm -f "$P-$1" >/dev/null 2>&1 || true
  docker run -d --name "$P-$1" --network "$P" "${ENDURECIDO[@]}" -e FONTE_USER="$2" -e FONTE_TITULO="${4:-Programa ao vivo}" \
    -v "$TMP/fonte.liq:/etc/liquidsoap/fonte.liq:ro" -v "$3:/run/secrets/senha:ro" \
    "$LIQUIDSOAP" /etc/liquidsoap/fonte.liq >/dev/null
}

mkdir -p "$TMP/segredos" "$TMP/controle" # o Docker criaria a pasta do volume como root
chmod 0755 "$TMP/controle"
for s in icecast_source_password icecast_admin_password icecast_relay_password playout_token; do
  printf '%s' "$s-$(openssl rand -hex 16)" > "$TMP/segredos/$s"
done
printf '%s' "operador-radio" > "$TMP/segredos/icecast_admin_user"
printf '%s' "senha-errada-$(openssl rand -hex 12)" > "$TMP/senha-errada"
chmod 0755 "$TMP" "$TMP/segredos"
chmod 0644 "$TMP/segredos"/* "$TMP/senha-errada"
cat > "$TMP/fonte.liq" <<'LIQ'
settings.log.stdout := true
settings.log.file := false
usuario = environment.get(default="x", "FONTE_USER")
titulo = environment.get(default="Programa ao vivo", "FONTE_TITULO")
senha = string.trim(file.contents("/run/secrets/senha"))
ao_vivo = metadata.map(insert_missing=true, fun (_) -> [("title", titulo), ("artist", "Locutor")], sine(880.))
output.icecast(%mp3(bitrate=128), host="liquidsoap", port=8005, user=usuario, password=senha, mount="live", fallible=true, ao_vivo)
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
segredo_ruim playout_token "" "está vazio"
segredo_ruim playout_token "__GERAR__-valor-de-exemplo-que-nao-pode-ir-ao-log" "valor de exemplo"
segredo_ruim icecast_source_password "curta" "menos de 24"

echo "== sobe Icecast e Liquidsoap, sem API"
docker build -q -f infra/icecast/Dockerfile -t "$ICECAST" . >/dev/null
docker network create "$P" >/dev/null
docker run -d --name "$P-icecast" --network "$P" --network-alias icecast --read-only --cap-drop ALL \
  --security-opt no-new-privileges:true --tmpfs /tmp:uid=100,gid=101,mode=0700 \
  -v "$TMP/segredos:/run/secrets:ro" -p 127.0.0.1::8000 "$ICECAST" >/dev/null
BASE="http://$(docker port "$P-icecast" 8000/tcp | head -n1)"
# HARBOR_TETO_SEM_RESPOSTA_S=45: o "último 200 + 15 min" (900 s) em escala de teste.
docker run -d --name "$P-radio" --network "$P" --network-alias liquidsoap "${ENDURECIDO[@]}" -e HARBOR_TETO_SEM_RESPOSTA_S=45 \
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

echo "== metadado_seguro: a tabela de casos (S-A09, igual ao MetadadoSeguro do servidor)"
sed -n '/^# >>> metadado_seguro/,/^# <<< metadado_seguro/p' infra/liquidsoap/radio.liq > "$TMP/metadado.liq"
cat >> "$TMP/metadado.liq" <<'LIQ'
dez = "aaaaaaaaaa"
def caso(nome, esperado, texto) =
  resultado = if metadado_seguro(texto) == esperado then "ok" else "FALHA" end
  print("CASO #{resultado} #{nome}")
end
caso("texto comum com acento", true, "Ol\xC3\xA1, mundo \xE6\x97\xA5\xE6\x9C\xAC")
caso("200 caracteres", true, string.concat(list.init(20, fun (_) -> dez)))
caso("201 caracteres", false, string.concat(list.init(20, fun (_) -> dez)) ^ "a")
caso("emoji composto com ZWJ (U+200D)", true, "\xF0\x9F\x91\xA8\xE2\x80\x8D\xF0\x9F\x91\xA9\xE2\x80\x8D\xF0\x9F\x91\xA7")
caso("bandeira com tags fechada por U+E007F", true, "\xF0\x9F\x8F\xB4\xF3\xA0\x81\xA7\xF3\xA0\x81\xA2\xF3\xA0\x81\xA5\xF3\xA0\x81\xAE\xF3\xA0\x81\xA7\xF3\xA0\x81\xBF")
caso("tag solta", false, "a\xF3\xA0\x81\xA7")
caso("bandeira sem o U+E007F", false, "\xF0\x9F\x8F\xB4\xF3\xA0\x81\xA7\xF3\xA0\x81\xA2")
caso("tag depois do fecho", false, "\xF0\x9F\x8F\xB4\xF3\xA0\x81\xA7\xF3\xA0\x81\xBF\xF3\xA0\x81\xA7")
caso("U+200B (invisível)", false, "a\xE2\x80\x8Bb")
caso("U+200C (ZWNJ)", false, "a\xE2\x80\x8Cb")
caso("U+200E (marca de direção)", false, "a\xE2\x80\x8Eb")
caso("U+202E (inverte a direção)", false, "Ao\xE2\x80\xAEvivo")
caso("U+2066 (isolamento de direção)", false, "a\xE2\x81\xA6b")
caso("U+FEFF", false, "a\xEF\xBB\xBFb")
caso("U+00AD (hífen suave)", false, "a\xC2\xADb")
caso("U+2028", false, "a\xE2\x80\xA8b")
caso("U+2029", false, "a\xE2\x80\xA9b")
caso("U+0085 (C1)", false, "a\xC2\x85b")
caso("quebra de linha", false, "a\nb")
caso("tabulação", false, "a\tb")
caso("DEL", false, "a\x7Fb")
caso("surrogate solto (ED A0 80)", false, "a\xED\xA0\x80b")
caso("UTF-8 inválido", false, "a\xFFb")
LIQ
SAIDA_META="$(docker run --rm --user 100 -v "$TMP/metadado.liq:/metadado.liq:ro" "$LIQUIDSOAP" /metadado.liq 2>&1 || true)"
[[ "$(grep -c '^CASO ' <<<"$SAIDA_META")" -ge 22 ]] && ok "tabela executada (22 casos)" || falha "a tabela de metadados não rodou: $SAIDA_META"
if grep '^CASO FALHA' <<<"$SAIDA_META"; then falha "metadado_seguro diverge do contrato"; else ok "metadado_seguro segue o contrato em todos os casos"; fi

echo "== ao vivo pelo harbor (E2-F08, S-A08, S-A09)"
controle() { printf '%s' "$2" > "$TMP/controle/$1"; chmod 0644 "$TMP/controle/$1"; }
conta_log() { docker logs "$1" 2>&1 | grep -c -F -e "$2" || true; }
conta_liq_api() { conta_log "$P-api" "$1"; }
espera_mais() {
  # $1 = contêiner, $2 = texto fixo, $3 = quantas ocorrências já havia, $4 = segundos
  for _ in $(seq 1 "$4"); do
    [[ "$(conta_log "$1" "$2")" -gt "$3" ]] && return 0
    sleep 1
  done
  return 1
}
espera_evento() {
  # $1 = tipo, $2 = segundos: linha HARBOR_EVENTO com o Bearer certo, o tipo e o identificador do locutor.
  for _ in $(seq 1 "$2"); do
    docker logs "$P-api" 2>&1 | grep -F 'HARBOR_EVENTO auth=ok resp=202' | grep -F "\"$1\"" | grep -q -F "$USR" && return 0
    sleep 1
  done
  return 1
}
espera_titulo() {
  # $1 = texto, $2 = segundos
  for _ in $(seq 1 "$2"); do titulo_no_ar "$1" && return 0; sleep 1; done
  return 1
}
espera_titulo_exato() {
  for _ in $(seq 1 "$2"); do estatisticas | grep -q -F "<title>$1</title>" && return 0; sleep 1; done
  return 1
}
iso_em() { date -u -d "now + $1 seconds" +%Y-%m-%dT%H:%M:%SZ; }
segredo_novo() { head -c 32 /dev/urandom | base64 | tr '+/' '-_' | tr -d '=\n'; }

USR="$(cat /proc/sys/kernel/random/uuid)"
SEC="$(segredo_novo)"
OUTRO="$(segredo_novo)"
printf '%s' "$SEC" > "$TMP/senha-ok"
printf '%s' "$OUTRO" > "$TMP/senha-outra"
printf 'curta' > "$TMP/senha-curta"
chmod 0644 "$TMP/senha-ok" "$TMP/senha-outra" "$TMP/senha-curta"
controle credencial "$USR $SEC"
controle janela aberta
controle fim "$(iso_em 3600)"
TITULO_BOM=$'A\xc3\xa7\xc3\xa3o ao vivo \xf0\x9f\x91\xa8\xe2\x80\x8d\xf0\x9f\x91\xa9\xe2\x80\x8d\xf0\x9f\x91\xa7'
TITULO_RLO=$'Ao\xe2\x80\xaevivo'
api vazia
# Um ouvinte que não sai durante o teste inteiro: o stream de saída não pode cair em nenhum dos casos (DoD 4 do 06).
curl -s -m 1500 -o "$TMP/continuo.mp3" "$BASE/radio.mp3" &
CONTINUO=$!
sleep 3

echo "-- recusas (nada disso entra no ar)"
fonte_ao_vivo fonte "$USR" "$TMP/senha-curta"
confere "senha de formato inválido é recusada" espera_log "$P-radio" "Recusado: senha_invalida identificador $USR" 30
fonte_ao_vivo fonte "$SEC" "$TMP/senha-ok"
confere "segredo no campo usuário é recusado (43 caracteres, sem o valor no log)" espera_log "$P-radio" "Recusado: usuario_invalido (43 caracteres)" 30
fonte_ao_vivo fonte "../../etc/passwd" "$TMP/senha-ok"
confere "usuário com ../ é recusado" espera_log "$P-radio" "Recusado: usuario_invalido (16 caracteres)" 30
confere "formato inválido nem chega à API" test "$(conta_log "$P-api" HARBOR_AUTH)" = "0"
fonte_ao_vivo fonte "$USR" "$TMP/senha-outra"
confere "segredo errado: a API recusa" espera_log "$P-api" "credencial=errada" 30
confere "segredo errado: o harbor recusa" espera_log "$P-radio" "Recusado: API respondeu 401 identificador $USR" 30
controle janela fechada
fonte_ao_vivo fonte "$USR" "$TMP/senha-ok"
confere "credencial certa fora da janela: a API recusa" espera_log "$P-api" "credencial=certa janela=fechada" 30
controle janela aberta
controle modo_auth lento
REC="$(conta_log "$P-radio" 'Recusado: API respondeu 52')"
# Timeout e conexão recusada não lançam no http.post: voltam como status 522/523. Qualquer um recusa.
confere "API lenta (> 5 s): o auth é recusado por timeout (522)" espera_mais "$P-radio" "Recusado: API respondeu 52" "$REC" 45
confere "nenhuma dessas conexões foi aceita" test "$(conta_log "$P-radio" 'Source client conectado')" = "0"
IP_VISTO="$(docker logs "$P-api" 2>&1 | grep -o ' ip=[^ ]*' | head -n1 | tr -d ' ')"
confere "o auth entrega à API o IP do source client, sem porta ($IP_VISTO)" grep -q -E '^ip=([0-9]{1,3}\.){3}[0-9]{1,3}$' <<<"$IP_VISTO"

echo "-- credencial certa dentro da janela"
fonte_ao_vivo fonte "$USR" "$TMP/senha-ok" "$TITULO_BOM"
controle modo_eventos 503 # a API perde o Conectado
controle modo_auth ""
CON=0
confere "conecta" espera_mais "$P-radio" "Source client conectado (identificador $USR)" "$CON" 60
confere "o ao vivo sobrepõe a programação" espera_log "$P-radio" "Switch to metadata_map" 30
# Guardian M1: o Conectado que a API não aceitou (503) é reenviado na reconferência de 10 s, senão o aoVivo ficaria noAr:false.
sleep 4
if espera_evento Conectado 1; then falha "o Conectado foi aceito com a API em 503"; else ok "o Conectado ficou sem aceite (a API respondeu 503)"; fi
controle modo_eventos ""
confere "o radio.liq reenvia o Conectado e a API o aceita, com o token e o identificador" espera_evento Conectado 30
# O Icecast mostra o título em entidades XML e o output.icecast do radio.liq manda o metadado em ISO-8859-1: o emoji vira "*",
# mas o título só chega se o ZWJ passou pela validação (senão o metadado inteiro some e fica só o artista).
confere "título com acento e emoji composto (ZWJ) passa" espera_titulo 'A&#xE7;&#xE3;o ao vivo' 40
confere "stream toca durante o ao vivo" toca

echo "-- metadado inválido do source client não vai ao ar"
CON="$(conta_log "$P-radio" 'Source client conectado')"
fonte_ao_vivo fonte "$USR" "$TMP/senha-ok" "$TITULO_RLO"
confere "reconecta com o título com U+202E" espera_mais "$P-radio" "Source client conectado" "$CON" 60
# O harbor enche 12 s de buffer antes de o ao vivo entrar: só depois disso o Icecast mostra o metadado novo (aqui, só o artista).
confere "o artista válido segue (o título inválido saiu)" espera_titulo_exato "Locutor" 40
if titulo_no_ar "$TITULO_RLO"; then falha "título com U+202E foi ao ar"; else ok "título com U+202E não foi ao ar"; fi

echo "-- segunda conexão com credencial válida não derruba a que está no ar"
DESC="$(conta_log "$P-radio" 'Source client desconectado')"
CON="$(conta_log "$P-radio" 'Source client conectado')"
fonte_ao_vivo fonte2 "$USR" "$TMP/senha-ok"
sleep 12
confere "a primeira segue conectada" test "$(conta_log "$P-radio" 'Source client desconectado')" = "$DESC"
confere "a segunda não entrou" test "$(conta_log "$P-radio" 'Source client conectado')" = "$CON"
confere "o stream segue" toca
docker rm -f "$P-fonte2" >/dev/null

echo "-- duas credenciais diferentes: o auth da segunda não troca quem está no ar (Sentinel 1)"
USR2="$(cat /proc/sys/kernel/random/uuid)"; SEC2="$(segredo_novo)"
printf '%s' "$SEC2" > "$TMP/senha-b"; chmod 0644 "$TMP/senha-b"
controle credencial "$USR $SEC"$'\n'"$USR2 $SEC2"
DESC="$(conta_log "$P-radio" 'Source client desconectado')"
CON="$(conta_log "$P-radio" 'Source client conectado')"
fonte_ao_vivo fonte2 "$USR2" "$TMP/senha-b"
confere "a API autorizou a segunda credencial (o auth dela é válido)" espera_log "$P-radio" "Autorizado identificador $USR2" 30
sleep 12 # pelo menos uma reconferência de 10 s depois do auth da segunda
confere "a primeira segue conectada" test "$(conta_log "$P-radio" 'Source client desconectado')" = "$DESC"
confere "a segunda não entrou" test "$(conta_log "$P-radio" 'Source client conectado')" = "$CON"
if docker logs "$P-api" 2>&1 | grep -F HARBOR_AUTORIZACAO | grep -q -F "$USR2"; then falha "a reconferência consultou a segunda credencial"; else ok "a reconferência segue consultando só a primeira"; fi
if docker logs "$P-api" 2>&1 | grep -F HARBOR_EVENTO | grep -q -F "$USR2"; then falha "um evento do harbor saiu com a segunda credencial"; else ok "nenhum evento saiu com a segunda credencial"; fi
docker rm -f "$P-fonte2" >/dev/null
controle credencial "$USR $SEC"

echo "-- falha de transporte da consulta mantém o locutor (falha aberta)"
controle modo_autorizacao 503
confere "log.severe a cada consulta que falha" espera_log "$P-radio" "Consulta de autorização falhou (status 503)" 30
sleep 12
confere "continua no ar com a API respondendo 503" test "$(conta_log "$P-radio" 'Source client desconectado')" = "$DESC"
confere "nenhuma derrubada" test "$(conta_log "$P-radio" 'Derrubando o ao vivo')" = "0"
confere "stream segue" toca
controle modo_autorizacao ""

echo "-- programa encerrado: derruba em até 30 s e volta para a rotação"
DESC="$(conta_log "$P-radio" 'Source client desconectado')"
CON="$(conta_log "$P-radio" 'Source client conectado')"
controle janela fechada
T0=$SECONDS
confere "403 derruba o ao vivo" espera_log "$P-radio" "Derrubando o ao vivo: autorização negada (HTTP 403)" 30
confere "derrubou em $((SECONDS - T0)) s (limite de 30 s)" test $((SECONDS - T0)) -le 30
confere "log da desconexão do harbor" espera_mais "$P-radio" "Source client desconectado" "$DESC" 15
confere "evento Desconectado chega à API" espera_evento Desconectado 15
confere "o stream volta para a rotação sem cair" toca
REC="$(conta_log "$P-radio" 'Recusado: API respondeu 401')"
fonte_ao_vivo fonte2 "$USR" "$TMP/senha-ok"
confere "um source client novo, com a credencial certa, é recusado fora da janela" espera_mais "$P-radio" "Recusado: API respondeu 401" "$REC" 30
docker rm -f "$P-fonte2" >/dev/null
confere "e não entrou no ar" test "$(conta_log "$P-radio" 'Source client conectado')" = "$CON"

echo "-- janela volta: o source client reconecta"
controle janela aberta
confere "reconecta sozinho" espera_mais "$P-radio" "Source client conectado" "$CON" 60

echo "-- qualquer 4xx derruba na hora"
DESC="$(conta_log "$P-radio" 'Source client desconectado')"
CON="$(conta_log "$P-radio" 'Source client conectado')"
controle modo_autorizacao 401
confere "401 derruba o ao vivo" espera_log "$P-radio" "Derrubando o ao vivo: autorização negada (HTTP 401)" 30
confere "e o stream segue" toca
controle modo_autorizacao ""
confere "reconecta depois" espera_mais "$P-radio" "Source client conectado" "$CON" 60

echo "-- API fora, teto 1: fim do programa + 1 min"
# O stub devolve um fim 10 s no passado (com a janela "aberta"): o radio.liq aprende o fim na consulta seguinte e o teto vira
# fim + 60 s = 50 s depois do `controle`, bem antes do "último 200 + HARBOR_TETO_SEM_RESPOSTA_S" (que é de pelo menos 45 s depois do último
# 200, e o último 200 vem de uma consulta posterior ao `controle`).
# ATENÇÃO (flake do run do #80): a PRIMEIRA consulta que o log da API mostra depois do `controle` pode ter começado ANTES dele (uma consulta
# em voo, que leu o fim antigo). Só a SEGUNDA começa depois do `controle` com certeza (as consultas são de 10 em 10 s). Sem esperar a segunda,
# a API caía com o radio.liq ainda no fim antigo, e o ao vivo caía pelo outro teto ("último 200 + 45 s"), com outro texto no log. Com o teto
# em 50 s (e não 30 s) a primeira consulta que falha ainda cai antes dele, e o "segue no ar" abaixo não depende de a derrubada atrasar.
controle fim "$(iso_em -10)"
AUT="$(conta_liq_api HARBOR_AUTORIZACAO)"
confere "o radio.liq aprende o novo fim: duas consultas depois do controle" espera_mais "$P-api" HARBOR_AUTORIZACAO "$((AUT + 1))" 30
DESC="$(conta_log "$P-radio" 'Source client desconectado')"
docker rm -f "$P-api" >/dev/null
confere "API parada: log.severe da consulta" espera_log "$P-radio" "Consulta de autorização falhou" 30
sleep 12
confere "o locutor já autorizado segue no ar" test "$(conta_log "$P-radio" 'Source client desconectado')" = "$DESC"
confere "o stream segue" toca
confere "o teto do fim do programa derruba mesmo sem resposta" espera_log "$P-radio" "Derrubando o ao vivo: teto da falha aberta (fim do programa + 1 min)" 90

echo "-- API fora, teto 2: último 200 + HARBOR_TETO_SEM_RESPOSTA_S (a janela é longa, só o 200 antigo vale)"
api vazia
controle fim "$(iso_em 3600)"
CON="$(conta_log "$P-radio" 'Source client conectado')"
fonte_ao_vivo fonte2 "$USR" "$TMP/senha-ok"
confere "um source client novo entra" espera_mais "$P-radio" "Source client conectado" "$CON" 90
# ATENÇÃO (flake do #83, mesma classe do teto 1): o radio.liq guarda o ÚLTIMO 200 e o fim do programa do teto anterior. Se a API cair antes de uma
# consulta que começou DEPOIS do `controle` e depois da reconexão, o teto sai de um valor velho: o ao vivo cai na hora ("20 s sem resposta: segue
# no ar" falha), ou cai pelo outro teto, com outro texto no log. Só a SEGUNDA linha HARBOR_AUTORIZACAO da API nova começa depois do `controle`
# com certeza (as consultas são de 10 em 10 s e só existem com um locutor no ar): com ela o último 200 é de menos de 10 s atrás e o fim é o de +1 h.
AUT="$(conta_liq_api HARBOR_AUTORIZACAO)"
confere "duas consultas de autorização depois do controle: o 200 recente e o fim novo" espera_mais "$P-api" HARBOR_AUTORIZACAO "$((AUT + 1))" 60
DESC="$(conta_log "$P-radio" 'Source client desconectado')"
docker rm -f "$P-api" >/dev/null
sleep 20
confere "20 s sem resposta: segue no ar (o teto é de 45 s)" test "$(conta_log "$P-radio" 'Source client desconectado')" = "$DESC"
confere "o teto do último 200 derruba, com o fim do programa a 1 h" espera_log "$P-radio" "Derrubando o ao vivo: teto da falha aberta (último 200 + 45 s)" 90
docker rm -f "$P-fonte2" >/dev/null

echo "-- API fora: conexão nova é recusada"
CON="$(conta_log "$P-radio" 'Source client conectado')"
REC="$(conta_log "$P-radio" 'Recusado: API respondeu 52')"
# O source client antigo demora a tentar de novo depois do stop(); um novo mostra a recusa na hora.
fonte_ao_vivo fonte2 "$USR" "$TMP/senha-ok"
confere "conexão nova com a API fora é recusada" espera_mais "$P-radio" "Recusado: API respondeu 52" "$REC" 30
confere "e não entra no ar" test "$(conta_log "$P-radio" 'Source client conectado')" = "$CON"
docker rm -f "$P-fonte2" >/dev/null
confere "o stream de saída nunca caiu (o ouvinte contínuo segue conectado)" kill -0 "$CONTINUO"
TAM1="$(wc -c < "$TMP/continuo.mp3")"; sleep 5; TAM2="$(wc -c < "$TMP/continuo.mp3")"
confere "e recebendo áudio" test "$TAM2" -gt "$TAM1"
kill "$CONTINUO" 2>/dev/null || true
docker rm -f "$P-fonte" >/dev/null 2>&1 || true

echo "== derruba a API"
docker rm -f "$P-api" >/dev/null
sleep 8
confere "stream continua sem a API" toca
confere "o Liquidsoap segue de pé" test "$(docker inspect -f '{{.State.Running}}' "$P-radio")" = "true"

docker logs "$P-radio" > "$TMP/radio.log" 2>&1
VAZOU=0
for s in playout_token icecast_source_password; do
  if grep -q -F -e "$(cat "$TMP/segredos/$s")" "$TMP/radio.log"; then VAZOU=1; fi
done
# O segredo de transmissão passou pelo auth (certo, errado e no campo usuário): nunca pode ter ido ao log.
for s in "$SEC" "$OUTRO"; do
  if grep -q -F -e "$s" "$TMP/radio.log"; then VAZOU=1; fi
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
