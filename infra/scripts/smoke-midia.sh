#!/usr/bin/env bash
# =============================================================================
# WebRadio — infra/scripts/smoke-midia.sh (E4-F10, Definição de Pronto do Épico 4: itens 3, 4 e 7, e o upload do item 1 pela API)
#
# Roda contra o stack de DEV já no ar, com o seed ligado, depois do smoke do ao vivo (que deixa o Admin com a senha nova) e do smoke dos pedidos:
#   E2E_ADMIN_SENHA_NOVA=<senha> infra/scripts/smoke-midia.sh
# Primeiro sobe o clamd REAL (com a base só de assinatura de teste, docker-compose.smoke.yml) e recria o Worker com ele; o freshclam não roda.
#
#   1. upload como o browser faz: POST /midias/uploads, multipart para a url da POST policy PELO NGINX (campos + file por último), concluir, e a mídia vai de Pendente a
#      Aprovado; a pré-escuta (?formato=url) devolve uma URL que serve audio/mpeg; a saída tem 1 stream de áudio e nenhum metadado.
#   3. S-C01: um .mp3 que é playlist HLS, um que é ffconcat e um com ID3 na frente e #EXTM3U no corpo são recusados (os dois primeiros SEM chamar o ffmpeg);
#      WAV com ADPCM, OGG com Theora e um WAV de 90 s numa vinheta (limite de 60 s) são recusados; um MP3 com capa e tags é aprovado.
#   4. antivírus com a assinatura de teste: o WAV marcado vai para Quarentena; com o clamd parado por 4 ciclos do Worker o arquivo continua Pendente, o marcador
#      WORKER_DEGRADADO motivo=scanner sai a cada ciclo, e quando o clamd volta o MESMO arquivo é aprovado (nunca aprovado sem scan).
#   7. limites do S-M06: o 21º POST na mesma hora é 429 (o cancelamento do envio não devolve o contador); 20 POST paralelos do mesmo usuário = exatamente 5 × 201 e
#      15 × 429; a quota do bucket quarentena recusa o upload acima dela; GET, PUT e DELETE em /storage/quarentena/... são recusados.
#
# Os arquivos de teste são gerados aqui (python3 e um ffmpeg COMPLETO num contêiner descartável, só para gerar; o do sanitizer só tem o encoder de MP3). Nunca imprime
# segredo. Acumula as falhas e sai com 1 se houve alguma.
# =============================================================================
set -euo pipefail
export LC_ALL=C

RAIZ="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$RAIZ"
COMPOSE=(docker compose -f docker-compose.yml -f docker-compose.dev.yml -f docker-compose.smoke.yml)
BASE="${SMOKE_BASE:-http://localhost:8080}"
ALPINE="alpine:3.24.4@sha256:f59abe656b692d901e184be3445e084786602108599d2a51d229c0f5a48c71bf" # o mesmo do sanitizer (o ffmpeg completo vem do apk, só para gerar arquivos)
TMP="$(mktemp -d)"
FIX="$TMP/fix"
# uid:gid do sanitizer e do mc de teste (sem privilégio). Em variável: dois-pontos colado a --user, perto de um curl, dispara o curl-auth-user do gitleaks.
UID_SEM_PRIVILEGIO="65532:65532"
QUOTA_ORIGINAL="10GiB"
QUOTA_MUDADA=0
MINIO_CID=""

: "${E2E_ADMIN_SENHA_NOVA:?Defina E2E_ADMIN_SENHA_NOVA (a senha que o smoke do ao vivo deixou no Admin)}"
[[ "${GITHUB_ACTIONS:-}" == "true" ]] && echo "::add-mask::$E2E_ADMIN_SENHA_NOVA"

falhas=0
ok()    { printf '  ok    %s\n' "$1"; }
falha() { printf '  FALHA %s\n' "$1"; falhas=$((falhas + 1)); }
confere() { if "${@:2}"; then ok "$1"; else falha "$1"; fi; }

mc_root() { # argumentos do mc; a credencial root vai por --env-file (nunca em argv), no namespace de rede do MinIO
  docker run --rm -i --pull never --network "container:$MINIO_CID" --user "$UID_SEM_PRIVILEGIO" --read-only --tmpfs /tmp --cap-drop ALL \
    --security-opt no-new-privileges:true --env-file "$TMP/mc-root.env" -e HOME=/tmp -e MC_CONFIG_DIR=/tmp/.mc \
    --entrypoint mc webradio-minio-init:local "$@" 2>&1
}

limpar() {
  # A quota baixada e o clamd parado não podem sobrar para quem vem depois (o E2E).
  if [[ "$QUOTA_MUDADA" == 1 && -n "$MINIO_CID" ]]; then mc_root quota set svc/quarentena --size "$QUOTA_ORIGINAL" >/dev/null || true; fi
  "${COMPOSE[@]}" up -d --wait --wait-timeout 300 clamd >/dev/null 2>&1 || true
  # Os arquivos gerados pelo contêiner pertencem ao root dele.
  docker run --rm -v "$TMP:/x" --entrypoint sh "$ALPINE" -c 'rm -rf /x/*' >/dev/null 2>&1 || true
  rm -rf "$TMP"
}
trap limpar EXIT

# --- utilidades -------------------------------------------------------------------------------------------------------
json() { python3 -I -c 'import json,sys; d=json.loads(sys.stdin.read()); print(eval(sys.argv[1], {"d": d}))' "$1"; }
espera_ate() { # $1 segundos, resto = comando
  local t="$1"; shift
  for _ in $(seq 1 "$t"); do "$@" && return 0; sleep 1; done
  return 1
}
api() { # $1 método, $2 caminho, $3 token, $4 corpo JSON em arquivo (opcional); imprime "STATUS corpo"
  local args=(-s --max-time 20 -X "$1" -w '\n%{http_code}' -H 'Content-Type: application/json' -H "Authorization: Bearer $3")
  [[ -n "${4:-}" ]] && args+=(--data-binary "@$4")
  local saida; saida="$(curl "${args[@]}" "$BASE$2")"
  printf '%s %s' "${saida##*$'\n'}" "${saida%$'\n'*}"
}
status_de() { printf '%s' "${1%% *}"; }
corpo_de() { printf '%s' "${1#* }"; }
login() { # $1 e-mail, $2 senha; o /auth/login tem 5 por minuto por IP: espera e tenta de novo no 429
  python3 -I -c 'import json,sys; print(json.dumps({"email": sys.argv[1], "senha": sys.argv[2]}))' "$1" "$2" > "$TMP/login.json"
  local saida codigo
  for _ in 1 2 3 4 5 6; do
    saida="$(curl -s --max-time 20 -X POST -w '\n%{http_code}' -H 'Content-Type: application/json' --data-binary "@$TMP/login.json" "$BASE/api/v1/auth/login")"
    codigo="${saida##*$'\n'}"
    if [[ "$codigo" == 200 ]]; then printf '%s' "${saida%$'\n'*}"; return 0; fi
    [[ "$codigo" == 429 ]] || return 1
    sleep 15
  done
  return 1
}

# Um upload como o browser: policy pela API, multipart PELO NGINX com os campos e o `file` por último, concluir. Define $UP_ID (o midiaId), $UP_POLICY, $UP_POST e $UP_CONCLUIR
# (variáveis, não saída: um $(subir ...) rodaria num subshell e as perderia).
UP_POST=""; UP_CONCLUIR=""; UP_POLICY=""; UP_ID=""; LAST=""
subir() { # $1 token, $2 arquivo, $3 tipo (Vinheta|Comercial|Musica), $4 mime, $5 título (opcional)
  UP_ID=""; UP_POST=""; UP_CONCLUIR=""
  local tam; tam="$(stat -c %s "$2")"
  python3 -I -c 'import json,sys; print(json.dumps({"nomeOriginal": sys.argv[1], "tipoMidia": sys.argv[2], "tamanhoBytes": int(sys.argv[3]), "mimeTypeDeclarado": sys.argv[4], "titulo": sys.argv[5] or None}))' \
    "$(basename "$2")" "$3" "$tam" "$4" "${5:-}" > "$TMP/up.json"
  local r; r="$(api POST /api/v1/midias/uploads "$1" "$TMP/up.json")"
  UP_POLICY="$(status_de "$r")"
  [[ "$UP_POLICY" == 201 ]] || return 0
  local corpo; corpo="$(corpo_de "$r")"
  local id url; id="$(json 'd["midiaId"]' <<<"$corpo")"; url="$(json 'd["upload"]["url"]' <<<"$corpo")"
  local campos=()
  while IFS= read -r linha; do campos+=(--form-string "$linha"); done < <(python3 -I -c 'import json,sys; [print(k + "=" + v) for k, v in json.loads(sys.stdin.read())["upload"]["campos"].items()]' <<<"$corpo")
  UP_POST="$(curl -s -o /dev/null --max-time 120 -w '%{http_code}' -X POST "$url" "${campos[@]}" -F "file=@$2;type=$4" || true)"
  UP_CONCLUIR=""
  if [[ "$UP_POST" == 201 ]]; then
    r="$(api POST "/api/v1/midias/$id/concluir" "$1")"; UP_CONCLUIR="$(status_de "$r")"
  fi
  UP_ID="$id"
}
status_da() { # $1 token, $2 id; imprime statusSanitizacao
  local r; r="$(api GET "/api/v1/midias/$2" "$1")"
  [[ "$(status_de "$r")" == 200 ]] && json 'd["statusSanitizacao"]' <<<"$(corpo_de "$r")" || true
}
motivo_da() { local r; r="$(api GET "/api/v1/midias/$2" "$1")"; json 'd.get("motivoRejeicao") or ""' <<<"$(corpo_de "$r")" || true; }
terminal() { # $1 token, $2 id
  case "$(status_da "$1" "$2")" in Aprovado|Rejeitado|Quarentena) return 0 ;; *) return 1 ;; esac
}
espera_status() { # $1 token, $2 id, $3 status, $4 segundos
  _s() { [[ "$(status_da "$1" "$2")" == "$3" ]]; }
  espera_ate "$4" _s "$1" "$2" "$3"
}
logs_worker() { "${COMPOSE[@]}" logs --no-color worker 2>&1 || true; }
logs_sanitizer() { "${COMPOSE[@]}" logs --no-color sanitizer 2>&1 || true; }

# --- o stack: clamd real + Worker com ele ------------------------------------------------------------------------------
echo "== sobe o clamd de verdade (base só com a assinatura de teste) e recria o Worker"
"${COMPOSE[@]}" up -d --wait --wait-timeout 300 clamd worker >/dev/null 2>&1 && ok "clamd e worker saudáveis" || { falha "clamd/worker não ficaram saudáveis"; exit 1; }
MINIO_CID="$("${COMPOSE[@]}" ps -q minio)"
printf 'MC_HOST_svc=http://%s:%s@127.0.0.1:9000\n' "$(cat secrets/minio_root_user)" "$(cat secrets/minio_root_password)" > "$TMP/mc-root.env"
chmod 0600 "$TMP/mc-root.env"

# --- quem é quem -------------------------------------------------------------------------------------------------------
echo "== Admin (senha nova), locutor1 e locutor2 do seed"
EMAIL_ADMIN="$(cat secrets/seed_admin_email)"
SENHA_DEV="$(cat secrets/seed_dev_senha)"
resp="$(login "$EMAIL_ADMIN" "$E2E_ADMIN_SENHA_NOVA")" || { falha "login do Admin com a senha nova (o smoke do ao vivo rodou antes?)"; exit 1; }
ADMIN="$(json 'd["accessToken"]' <<<"$resp")"
resp="$(login locutor1@example.com "$SENHA_DEV")" || { falha "login do locutor1 (o stack subiu com SEED_DEV_DATA=true?)"; exit 1; }
LOC1="$(json 'd["accessToken"]' <<<"$resp")"
resp="$(login locutor2@example.com "$SENHA_DEV")" || { falha "login do locutor2"; exit 1; }
LOC2="$(json 'd["accessToken"]' <<<"$resp")"
if [[ "${GITHUB_ACTIONS:-}" == "true" ]]; then echo "::add-mask::$ADMIN"; echo "::add-mask::$LOC1"; echo "::add-mask::$LOC2"; fi
ok "três usuários autenticados"

# --- arquivos de teste ------------------------------------------------------------------------------------------------
echo "== gera os arquivos de teste"
mkdir -p "$FIX"
python3 -I - "$FIX" <<'PY'
import math, struct, sys
d = sys.argv[1]

def wav(path, segundos, taxa=22050, marca=None, aberto=False, freq=440):
    pcm = b"".join(struct.pack("<h", int(8000 * math.sin(2 * math.pi * freq * i / taxa))) for i in range(taxa * segundos))
    fmt = struct.pack("<HHIIHH", 1, 1, taxa, taxa * 2, 2, 16)
    tamanho_dados = 0xFFFFFFFF if aberto else len(pcm)
    corpo = b"fmt " + struct.pack("<I", len(fmt)) + fmt + b"data" + struct.pack("<I", tamanho_dados) + pcm
    if marca:
        info = b"INFO" + b"ICMT" + struct.pack("<I", len(marca) + len(marca) % 2) + marca + (b"\0" if len(marca) % 2 else b"")
        corpo += b"LIST" + struct.pack("<I", len(info)) + info
    open(path, "wb").write(b"RIFF" + struct.pack("<I", 0xFFFFFFFF if aberto else 4 + len(corpo)) + b"WAVE" + corpo)

wav(d + "/marcado.wav", 3, marca=b"WEBRADIO-E4-ASSINATURA-DE-TESTE")   # a assinatura de teste (infra/clamav/smoke/smoke.ndb)
wav(d + "/limpo.wav", 3, freq=523)
wav(d + "/longo.wav", 90, aberto=True)                                 # 90 s numa vinheta (limite 60 s), cabeçalho com tamanho aberto
wav(d + "/grande.wav", 47, freq=330)                                    # ~2 MB: acima da quota de 1 MiB do teste
open(d + "/playlist.mp3", "wb").write(b"#EXTM3U\nhttp://api:8080/health/live\n")
open(d + "/concat.mp3", "wb").write(b"ffconcat version 1.0\nfile /etc/passwd\n")
open(d + "/id3-playlist.mp3", "wb").write(b"ID3\x04\x00\x00\x00\x00\x00\x00" + b"#EXTM3U\nhttp://api:8080/health/live\n")
PY
cat > "$TMP/gera.sh" <<'GERA'
set -e
apk add --no-cache ffmpeg >/dev/null
cd /out
S="-f lavfi -i sine=frequency=440:sample_rate=44100:duration=3"
ffmpeg -loglevel error -y -f lavfi -i color=c=red:s=64x64:d=1 -frames:v 1 capa.png
ffmpeg -loglevel error -y $S -i capa.png -map 0:a -map 1:v -c:a libmp3lame -c:v mjpeg -id3v2_version 3 -metadata title="Titulo Secreto" -metadata artist="Artista Secreto" -disposition:v attached_pic capa.mp3
ffmpeg -loglevel error -y $S -f lavfi -i testsrc=s=64x64:d=1:r=5 -map 0:a -map 1:v -c:a libvorbis -c:v libtheora teora.ogg
ffmpeg -loglevel error -y $S -c:a adpcm_ms adpcm.wav
chmod -R a+rX /out
GERA
docker run --rm -v "$FIX:/out" -v "$TMP/gera.sh:/gera.sh:ro" --entrypoint sh "$ALPINE" /gera.sh && ok "arquivos gerados" || { falha "não consegui gerar os arquivos de teste"; exit 1; }
chmod 755 "$TMP" "$FIX"; chmod 644 "$FIX"/* 2>/dev/null || true

# --- DoD 3: S-C01 e o upload pelo nginx -------------------------------------------------------------------------------------------
echo "== DoD 1 e 3: upload pelo nginx, e o que o sanitizer recusa"
recusa() { # $1 arquivo, $2 tipo, $3 mime, $4 descrição, $5 "sem-ffmpeg" (opcional)
  local id; subir "$LOC1" "$FIX/$1" "$2" "$3" ""; id="$UP_ID"
  if [[ "$UP_POLICY" != 201 || "$UP_POST" != 201 || "$UP_CONCLUIR" != 202 ]]; then falha "$4: o envio falhou (policy $UP_POLICY, multipart $UP_POST, concluir $UP_CONCLUIR)"; return; fi
  espera_ate 120 terminal "$LOC1" "$id" || true
  confere "$4 → Rejeitado" test "$(status_da "$LOC1" "$id")" = Rejeitado
  [[ "${5:-}" == sem-ffmpeg ]] && confere "$4: o sanitizer nunca viu o job (magic bytes, sem ffmpeg)" test -z "$(grep -F -m1 "$id" <<<"$(logs_sanitizer)" || true)"
  LAST="$id"
}
recusa playlist.mp3 Vinheta audio/mpeg "playlist HLS com extensão .mp3" sem-ffmpeg
recusa concat.mp3 Vinheta audio/mpeg "ffconcat com extensão .mp3" sem-ffmpeg
recusa id3-playlist.mp3 Vinheta audio/mpeg "ID3 na frente e #EXTM3U no corpo"
recusa adpcm.wav Vinheta audio/wav "WAV com codec fora da lista (ADPCM)"
recusa teora.ogg Vinheta audio/ogg "OGG com Theora"
recusa longo.wav Vinheta audio/wav "WAV de 90 s numa vinheta de 60 s"
confere "o motivo da duração fala do limite" bash -c "[[ \"\$(curl -s -H 'Authorization: Bearer $LOC1' '$BASE/api/v1/midias/$LAST' | python3 -I -c 'import json,sys; print(json.loads(sys.stdin.read()).get(\"motivoRejeicao\",\"\"))')\" == *dura* ]]"

subir "$LOC1" "$FIX/capa.mp3" Vinheta audio/mpeg ""; ID_CAPA="$UP_ID"
if [[ "$UP_POLICY" == 201 && "$UP_POST" == 201 && "$UP_CONCLUIR" == 202 ]]; then
  ok "MP3 com capa e tags: policy 201, multipart 201 pelo nginx, concluir 202"
  confere "e vai de Pendente a Aprovado" espera_status "$LOC1" "$ID_CAPA" Aprovado 120
  r="$(api GET "/api/v1/midias/$ID_CAPA/preview?formato=url" "$LOC1")"
  URL="$(json 'd["url"]' <<<"$(corpo_de "$r")" 2>/dev/null || true)"
  confere "pré-escuta: ?formato=url devolve a URL (200)" test "$(status_de "$r")" = 200
  if [[ -n "$URL" ]]; then
    curl --silent --max-time 30 --output "$TMP/saida.mp3" --dump-header "$TMP/saida.h" "$URL" || true
    confere "a URL serve audio/mpeg (200)" bash -c "head -n1 '$TMP/saida.h' | grep -q ' 200' && grep -qi '^content-type: audio/mpeg' '$TMP/saida.h'"
    chmod 644 "$TMP/saida.mp3"
    SAIDA="$(docker run --rm --pull never --network none --user "$UID_SEM_PRIVILEGIO" -v "$TMP:/in:ro" --entrypoint /usr/local/bin/ffprobe webradio-sanitizer:local -v error -show_entries stream=codec_name,codec_type -of csv=p=0 /in/saida.mp3 2>&1 | tr '\n' '|')"
    confere "a saída tem 1 stream de áudio mp3 e nenhuma capa" test "$SAIDA" = "mp3,audio|"
    TAGS="$(docker run --rm --pull never --network none --user "$UID_SEM_PRIVILEGIO" -v "$TMP:/in:ro" --entrypoint /usr/local/bin/ffprobe webradio-sanitizer:local -v error -show_entries format_tags -of default=nw=1 /in/saida.mp3 2>&1)"
    confere "e nenhum metadado (a tag secreta sumiu)" test -z "$TAGS"
  fi
else
  falha "MP3 com capa: o envio falhou (policy $UP_POLICY, multipart $UP_POST, concluir $UP_CONCLUIR)"
fi

# --- DoD 4: antivírus -----------------------------------------------------------------------------------------------------
echo "== DoD 4: antivírus com a assinatura de teste"
subir "$LOC1" "$FIX/marcado.wav" Vinheta audio/wav ""; ID_MARCADO="$UP_ID"
if [[ "$UP_CONCLUIR" == 202 ]]; then
  espera_ate 120 terminal "$LOC1" "$ID_MARCADO" || true
  confere "o WAV marcado vai para Quarentena" test "$(status_da "$LOC1" "$ID_MARCADO")" = Quarentena
  confere "e nunca chegou ao sanitizer" test -z "$(grep -F -m1 "$ID_MARCADO" <<<"$(logs_sanitizer)" || true)"
  confere "o Worker registrou ARQUIVO_INFECTADO" grep -q 'ARQUIVO_INFECTADO' <<<"$(logs_worker)"
else
  falha "WAV marcado: o envio falhou (policy $UP_POLICY, multipart $UP_POST, concluir $UP_CONCLUIR)"
fi

"${COMPOSE[@]}" stop clamd >/dev/null 2>&1
ANTES="$(grep -c 'WORKER_DEGRADADO motivo=scanner' <<<"$(logs_worker)" || true)"
subir "$LOC1" "$FIX/limpo.wav" Vinheta audio/wav ""; ID_LIMPO="$UP_ID"
if [[ "$UP_CONCLUIR" == 202 ]]; then
  sleep 55 # mais de 4 ciclos de 10 s do Worker
  confere "com o clamd parado por 4 ciclos o arquivo continua Pendente (nunca Aprovado sem scan)" test "$(status_da "$LOC1" "$ID_LIMPO")" = Pendente
  DEPOIS="$(grep -c 'WORKER_DEGRADADO motivo=scanner' <<<"$(logs_worker)" || true)"
  confere "WORKER_DEGRADADO motivo=scanner sai a cada ciclo (${DEPOIS:-0} - ${ANTES:-0} >= 4)" test "$(( ${DEPOIS:-0} - ${ANTES:-0} ))" -ge 4
  "${COMPOSE[@]}" up -d --wait --wait-timeout 300 clamd >/dev/null 2>&1 || falha "o clamd não voltou"
  confere "quando o clamd volta o MESMO arquivo é aprovado" espera_status "$LOC1" "$ID_LIMPO" Aprovado 150
  confere "e o Worker registrou WORKER_RECUPERADO motivo=scanner" grep -q 'WORKER_RECUPERADO motivo=scanner' <<<"$(logs_worker)"
else
  falha "WAV limpo: o envio falhou (policy $UP_POLICY, multipart $UP_POST, concluir $UP_CONCLUIR)"
  "${COMPOSE[@]}" up -d --wait --wait-timeout 300 clamd >/dev/null 2>&1 || true
fi

# --- DoD 7: limites do S-M06 -------------------------------------------------------------------------------------------------
echo "== DoD 7: 20 por hora, 5 abertos e a quota"
python3 -I -c 'import json; print(json.dumps({"nomeOriginal": "a.mp3", "tipoMidia": "Vinheta", "tamanhoBytes": 1000, "mimeTypeDeclarado": "audio/mpeg"}))' > "$TMP/pequeno.json"
ruins=0
for i in $(seq 1 20); do
  r="$(api POST /api/v1/midias/uploads "$LOC2" "$TMP/pequeno.json")"
  [[ "$(status_de "$r")" == 201 ]] || { ruins=$((ruins + 1)); continue; }
  d="$(api DELETE "/api/v1/midias/$(json 'd["midiaId"]' <<<"$(corpo_de "$r")")" "$LOC2")"
  [[ "$(status_de "$d")" == 204 ]] || ruins=$((ruins + 1))
done
confere "20 envios com desistência (POST 201 e DELETE 204): nenhum falhou" test "$ruins" = 0
curl --silent --dump-header "$TMP/vinte-e-um.h" --output /dev/null --max-time 20 -X POST -H 'Content-Type: application/json' -H "Authorization: Bearer $LOC2" --data-binary "@$TMP/pequeno.json" "$BASE/api/v1/midias/uploads" || true
confere "o 21º POST na mesma hora → 429 (o cancelamento não devolveu o contador)" bash -c "head -n1 '$TMP/vinte-e-um.h' | grep -q ' 429'"
confere "com Retry-After" grep -qi '^retry-after:' "$TMP/vinte-e-um.h"

paralelo() { # um POST; imprime o código
  curl -s -o "$TMP/par-$1.json" --max-time 30 -w '%{http_code}\n' -X POST -H 'Content-Type: application/json' -H "Authorization: Bearer $ADMIN" --data-binary "@$TMP/pequeno.json" "$BASE/api/v1/midias/uploads"
}
export -f paralelo; export TMP BASE ADMIN
CODIGOS="$(seq 1 20 | xargs -P 20 -I{} bash -c 'paralelo {}' | sort | uniq -c | tr -s ' ' | tr '\n' ';')"
confere "20 POST paralelos do mesmo usuário: exatamente 5 × 201 e 15 × 429 ($CODIGOS)" test "$CODIGOS" = " 15 429; 5 201;"
for f in "$TMP"/par-*.json; do
  id="$(json 'd.get("midiaId","")' < "$f" 2>/dev/null || true)"
  [[ -n "$id" ]] && api DELETE "/api/v1/midias/$id" "$ADMIN" >/dev/null || true
done

# quota do bucket quarentena: recusa o que passa dela (o limite do teste é de 1 MiB; o arquivo tem ~2 MB)
mc_root quota set svc/quarentena --size 1MiB >/dev/null && QUOTA_MUDADA=1
subir "$LOC1" "$FIX/grande.wav" Vinheta audio/wav ""; ID_QUOTA="$UP_ID"
confere "com a quota de 1 MiB a policy sai (201) e o POST acima dela é recusado pelo MinIO (4xx)" test "$UP_POLICY" = 201 -a "${UP_POST:-0}" -ge 400
if [[ -n "$ID_QUOTA" ]]; then
  r="$(api POST "/api/v1/midias/$ID_QUOTA/concluir" "$LOC1")"
  confere "o concluir não acha o objeto (400): nada entrou na quarentena" test "$(status_de "$r")" = 400
  api DELETE "/api/v1/midias/$ID_QUOTA" "$LOC1" >/dev/null || true
fi
mc_root quota set svc/quarentena --size "$QUOTA_ORIGINAL" >/dev/null && QUOTA_MUDADA=0

# borda do storage: só POST na quarentena
for metodo in GET PUT DELETE; do
  codigo="$(curl -s -o /dev/null -w '%{http_code}' --max-time 10 -X "$metodo" "$BASE/storage/quarentena/2026/01/00000000-0000-0000-0000-000000000000" || true)"
  confere "$metodo em /storage/quarentena/... é recusado ($codigo)" test "$codigo" = 403 -o "$codigo" = 405
done

echo
if ((falhas > 0)); then
  echo "smoke-midia: $falhas verificação(ões) falharam."
  logs_worker | tail -n 60 >&2 || true
  exit 1
fi
echo "smoke-midia: tudo certo."
