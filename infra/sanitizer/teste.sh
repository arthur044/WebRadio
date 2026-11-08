#!/usr/bin/env bash
# =============================================================================
# WebRadio — infra/sanitizer/teste.sh (E4-F04)
# Constrói a imagem do sanitizer (infra/sanitizer/Dockerfile) e confere, com o mesmo hardening do compose (sem rede, read_only, uid 65532,
# cap_drop ALL, mem/pids/cpus), o que o E4-D1/E4-D2 e o S-C01 prometem:
#   1. o ffmpeg tem SÓ o que a lista do E4-D2 diz (demuxers, muxers, decoders, encoders, filtros, protocolos) e a versão mínima;
#   2. MP3, OGG/Vorbis, OGG/Opus, WAV PCM (16 e 24 bits) e FLAC válidos viram MP3 (192k, 44,1 kHz, estéreo), sem capa nem tag;
#   3. OGG com Theora, WAV com ADPCM, dois streams de áudio, playlist HLS e ffconcat (inclusive com ID3 na frente) são recusados, sem rede;
#   4. a duração e o tamanho da SAÍDA são cortados (-t e -fs); job.json fora do formato e symlink no diretório do job são job_invalido;
#   5. um ffmpeg que trava é morto no timeout; result.json sai sempre por rename e o original é apagado.
# Os arquivos de teste são gerados aqui, por um ffmpeg COMPLETO num contêiner descartável (o do sanitizer só tem o encoder de MP3).
# Uso (na raiz do repositório):  infra/sanitizer/teste.sh
# =============================================================================
set -euo pipefail
export LC_ALL=C

SANITIZER="webradio-sanitizer:teste"
ALPINE="alpine@sha256:f59abe656b692d901e184be3445e084786602108599d2a51d229c0f5a48c71bf" # o mesmo da imagem (o ffmpeg completo vem do apk, só para gerar arquivos)
P="wr-san-teste-$$"
TMP="$(mktemp -d)"
FALHAS=0
VERSAO_MINIMA="9.0.2" # o trivy não enxerga binário compilado: o pin sobe aqui e no Dockerfile quando sair CVE de demuxer/decoder (ver o Dockerfile)

limpar() {
  docker rm -f "$P-san" "$P-lento" >/dev/null 2>&1 || true
  # Os arquivos gerados pelo contêiner pertencem ao root dele.
  docker run --rm -v "$TMP:/x" --entrypoint sh "$ALPINE" -c 'rm -rf /x/*' >/dev/null 2>&1 || true
  rm -rf "$TMP"
}
trap limpar EXIT

ok()    { echo "  ok    $*"; }
falha() { echo "  FALHA $*" >&2; FALHAS=$((FALHAS + 1)); }
confere() { if "${@:2}"; then ok "$1"; else falha "$1"; fi; }

echo "== constrói a imagem do sanitizer"
docker build -q -f infra/sanitizer/Dockerfile -t "$SANITIZER" . >/dev/null
FFMPEG_NA_IMAGEM() { docker run --rm --network none --entrypoint /usr/local/bin/ffmpeg "$SANITIZER" -hide_banner "$@" 2>&1; }

echo "== o ffmpeg tem só o que o E4-D2 prevê"
VERSAO="$(FFMPEG_NA_IMAGEM -version | head -n 1 | sed -E 's/^ffmpeg version ([0-9]+(\.[0-9]+)*).*/\1/')"
confere "versão $VERSAO >= $VERSAO_MINIMA" test "$(printf '%s\n%s\n' "$VERSAO_MINIMA" "$VERSAO" | sort -V | head -n 1)" = "$VERSAO_MINIMA"

lista() { # $1 = demuxers|muxers|decoders|encoders|filters: os nomes, ordenados
  FFMPEG_NA_IMAGEM "-$1" | awk '/^ *-+$/ {f=1; next} f && NF >= 2 {print $2}' | sort -u | tr '\n' ' '
}
conferir_lista() { # $1 = tipo, $2 = esperado (ordenado, separado por espaço)
  local atual; atual="$(lista "$1")"
  if [[ "$atual" == "$2 " ]]; then ok "$1: $2"; else falha "$1: esperava [$2], veio [${atual% }]"; fi
}
conferir_lista demuxers "flac mp3 ogg wav"
conferir_lista muxers "mp3"
conferir_lista decoders "flac mp3 mp3float opus pcm_f32le pcm_s16le pcm_s24le pcm_s32le vorbis"
conferir_lista encoders "libmp3lame"
# Os filtros do E4-D2 (aresample, aformat, anull) e os que o próprio ffmpeg não monta o grafo sem (abuffer, abuffersink). Os de vídeo (buffer, buffersink, format,
# null, trim, crop, hflip, vflip, transpose, rotate) e atrim vêm do núcleo do ffmpeg CLI e não saem com o --disable-everything: sem decoder de vídeo e sem protocolo, nada os alcança.
conferir_lista filters "abuffer abuffersink aformat anull aresample atrim buffer buffersink crop format hflip null rotate transpose trim vflip"
PROTOCOLOS="$(FFMPEG_NA_IMAGEM -protocols | grep -E '^  [a-z]' | awk '{print $1}' | sort -u | tr '\n' ' ')"
confere "protocolos: só file" test "$PROTOCOLOS" = "file "

echo "== gera os arquivos de teste (ffmpeg completo, contêiner descartável)"
mkdir -p "$TMP/fix"
cat > "$TMP/gera.sh" <<'GERA'
set -e
cd /out
S="-f lavfi -i sine=frequency=440:sample_rate=44100:duration=3"
ffmpeg -loglevel error -y $S -c:a libmp3lame -b:a 128k a.mp3
ffmpeg -loglevel error -y $S -c:a libvorbis a-vorbis.ogg
ffmpeg -loglevel error -y $S -c:a libopus a-opus.ogg
ffmpeg -loglevel error -y $S -c:a pcm_s16le a16.wav
ffmpeg -loglevel error -y $S -c:a pcm_s24le a24.wav
ffmpeg -loglevel error -y $S -c:a flac a.flac
ffmpeg -loglevel error -y -f lavfi -i color=c=red:s=64x64:d=1 -frames:v 1 capa.png
ffmpeg -loglevel error -y $S -i capa.png -map 0:a -map 1:v -c:a libmp3lame -c:v mjpeg -id3v2_version 3 -metadata title="Titulo Secreto" -metadata artist="Artista Secreto" -disposition:v attached_pic capa.mp3
ffmpeg -loglevel error -y $S -f lavfi -i testsrc=s=64x64:d=1:r=5 -map 0:a -map 1:v -c:a libvorbis -c:v libtheora teora.ogg
ffmpeg -loglevel error -y $S -c:a adpcm_ms adpcm.wav
ffmpeg -loglevel error -y $S -f lavfi -i sine=frequency=880:sample_rate=44100:duration=3 -map 0:a -map 1:a -c:a libvorbis dois.ogg
ffmpeg -loglevel error -y -f lavfi -i sine=frequency=440:sample_rate=22050:duration=40 -c:a pcm_s16le longo.wav
printf '#EXTM3U\nhttp://api:8080/health/live\n' > playlist.mp3
printf 'ffconcat version 1.0\nfile /etc/passwd\n' > concat.wav
printf 'ID3\003\000\000\000\000\000\000#EXTM3U\nhttp://api:8080/health/live\n' > id3-playlist.mp3
GERA
docker run --rm -v "$TMP/fix:/out" -v "$TMP/gera.sh:/gera.sh:ro" --entrypoint sh "$ALPINE" -c 'apk add --no-cache ffmpeg >/dev/null 2>&1 && sh /gera.sh'

echo "== sobe o sanitizer com o hardening do compose"
# Um ffmpeg "lento" no lugar do real, só para o teste do timeout (o script aceita SANITIZER_FFMPEG).
printf '#!/bin/sh\nexec sleep 120\n' > "$TMP/ffmpeg-lento.sh"
chmod 0755 "$TMP/ffmpeg-lento.sh" "$TMP"
ENDURECIDO=(--network none --read-only --cap-drop ALL --security-opt no-new-privileges:true --user 65532:65532 --pids-limit 50 --memory 512m --cpus 1
  --tmpfs /jobs:uid=65532,gid=65532,mode=0700,size=512m)
docker run -d --name "$P-san" "${ENDURECIDO[@]}" "$SANITIZER" >/dev/null
docker run -d --name "$P-lento" "${ENDURECIDO[@]}" -v "$TMP/ffmpeg-lento.sh:/stub/ffmpeg:ro" -e SANITIZER_FFMPEG=/stub/ffmpeg "$SANITIZER" >/dev/null

confere "uid 65532, sem rede, root somente leitura, sem capacidades" test \
  "$(docker inspect -f '{{.Config.User}} {{.HostConfig.NetworkMode}} {{.HostConfig.ReadonlyRootfs}} {{.HostConfig.CapDrop}}' "$P-san")" = "65532:65532 none true [ALL]"
confere "a única interface de rede é a de loopback" test "$(docker exec "$P-san" sh -c 'ls /sys/class/net')" = "lo"
confere "não grava fora do /jobs" bash -c "! docker exec '$P-san' sh -c 'touch /nao-pode' 2>/dev/null"

job() { # $1 = contêiner, $2 = id, $3 = formato, $4 = arquivo no host, $5 = duração máx (s), $6 = tamanho máx da saída, $7 = timeout (s)
  docker exec -i "$1" sh -c "mkdir /jobs/$2 && cat > /jobs/$2/in.bin" < "$4"
  printf '{"formato":"%s","duracaoMaxSegundos":%s,"tamanhoMaxSaidaBytes":%s,"timeoutSegundos":%s}\n' "$3" "$5" "$6" "$7" \
    | docker exec -i "$1" sh -c "cat > /jobs/$2/job.json"
  docker exec "$1" sh -c "touch /jobs/$2/pronto"
}
espera_resultado() { # $1 = contêiner, $2 = id, $3 = segundos: imprime o result.json
  for _ in $(seq 1 "$3"); do
    docker exec "$1" cat "/jobs/$2/result.json" 2>/dev/null && return 0
    sleep 1
  done
  return 1
}
novo_id() { cat /proc/sys/kernel/random/uuid; }
campo() { sed -E "s/.*\"$1\":(\"[^\"]*\"|[^,}]*).*/\1/" | tr -d '"'; } # valor de um campo do result.json (de uma linha)
no_job() { docker exec "$P-san" sh -c "$2" ; }

aprova() { # $1 = formato, $2 = arquivo, $3 = codec esperado
  local id res; id="$(novo_id)"
  job "$P-san" "$id" "$1" "$TMP/fix/$2" 20 5000000 60
  res="$(espera_resultado "$P-san" "$id" 60)" || { falha "$2: sem result.json em 60 s"; return; }
  confere "$2 ($1/$3) vira MP3: ok=true, 1 stream de áudio, codec $3" test "$(echo "$res" | campo ok)|$(echo "$res" | campo streamsAudio)|$(echo "$res" | campo codec)" = "true|1|$3"
  confere "$2: out.mp3 existe e o original foi apagado" bash -c "docker exec '$P-san' sh -c 'test -s /jobs/$id/out.mp3 && test ! -e /jobs/$id/in.bin && test ! -e /jobs/$id/out.tmp.mp3 && test ! -e /jobs/$id/result.json.tmp'"
  # A saída: MP3, 192k, 44,1 kHz, estéreo, exatamente 1 stream, nenhum metadado.
  confere "$2: saída mp3 44100 Hz 2 canais, 1 stream, sem tag" test \
    "$(docker exec "$P-san" ffprobe -v error -show_entries stream=codec_name,sample_rate,channels,codec_type -of csv=p=0 "/jobs/$id/out.mp3" | tr '\n' '|')" = "mp3,audio,44100,2|"
  confere "$2: saída sem metadado de formato" test -z "$(docker exec "$P-san" ffprobe -v error -show_entries format_tags -of default=nw=1 "/jobs/$id/out.mp3")"
  LAST_ID="$id"
}

recusa() { # $1 = formato, $2 = arquivo, $3 = motivos aceitos (separados por |), $4 = descrição
  local id res motivo; id="$(novo_id)"
  job "$P-san" "$id" "$1" "$TMP/fix/$2" 20 5000000 60
  res="$(espera_resultado "$P-san" "$id" 60)" || { falha "$4: sem result.json em 60 s"; return; }
  motivo="$(echo "$res" | campo motivo)"
  confere "$4: recusado ($motivo)" bash -c "[[ \"$(echo "$res" | campo ok)\" == false && \"|$3|\" == *\"|$motivo|\"* ]]"
  confere "$4: não sobra saída nem original" bash -c "docker exec '$P-san' sh -c 'test ! -e /jobs/$id/out.mp3 && test ! -e /jobs/$id/in.bin'"
}

echo "== formatos válidos viram MP3"
aprova mp3 a.mp3 mp3
aprova ogg a-vorbis.ogg vorbis
aprova ogg a-opus.ogg opus
aprova wav a16.wav pcm_s16le
aprova wav a24.wav pcm_s24le
aprova flac a.flac flac

echo "== MP3 com capa (APIC) e tags ID3: a saída não tem capa nem tag"
aprova mp3 capa.mp3 mp3
confere "a capa não vai para a saída (nenhum stream de vídeo)" test -z "$(docker exec "$P-san" ffprobe -v error -select_streams v -show_entries stream=codec_name -of csv=p=0 "/jobs/$LAST_ID/out.mp3")"
confere "o título e o artista do original não aparecem nos bytes da saída" bash -c "! docker exec '$P-san' cat /jobs/$LAST_ID/out.mp3 | grep -a -q -e 'Titulo Secreto' -e 'Artista Secreto'"

echo "== recusas (S-C01)"
recusa ogg teora.ogg "stream_nao_audio" "OGG com Theora"
recusa wav adpcm.wav "codec_nao_permitido" "WAV com ADPCM"
recusa ogg dois.ogg "streams_audio_invalido" "OGG com dois streams de áudio"
recusa mp3 playlist.mp3 "formato_invalido|streams_audio_invalido" "playlist HLS (#EXTM3U) forçada como mp3"
recusa wav concat.wav "formato_invalido|streams_audio_invalido" "ffconcat forçado como wav"
recusa mp3 id3-playlist.mp3 "formato_invalido|streams_audio_invalido" "ID3 na frente e #EXTM3U no corpo, forçado como mp3"
confere "o log do sanitizer não mostra nenhuma conexão nem leitura de /etc/passwd" bash -c "! docker logs '$P-san' 2>&1 | grep -q -i -e 'http' -e 'passwd' -e 'tcp' -e 'connect'"

echo "== duração e tamanho da saída (S-A01)"
ID="$(novo_id)"
job "$P-san" "$ID" wav "$TMP/fix/longo.wav" 10 5000000 60
RES="$(espera_resultado "$P-san" "$ID" 60)" || RES=""
DUR="$(echo "$RES" | campo duracaoSegundos)"
confere "WAV de 40 s com limite de 10 s: a saída é cortada em ~11 s (duração máx + 1; o Worker rejeita pela duração da saída)" \
  bash -c "awk -v d='$DUR' 'BEGIN{exit !(d >= 10.5 && d <= 11.6)}'"
ID="$(novo_id)"
job "$P-san" "$ID" wav "$TMP/fix/longo.wav" 100 30000 60
RES="$(espera_resultado "$P-san" "$ID" 60)" || RES=""
confere "-fs: a saída que bateu no limite de tamanho é RECUSADA (saida_grande_demais), nunca ok com a música cortada" \
  test "$(echo "$RES" | campo ok)|$(echo "$RES" | campo motivo)" = "false|saida_grande_demais"
confere "-fs: não sobra out.mp3" bash -c "! docker exec '$P-san' test -e /jobs/$ID/out.mp3"
ID="$(novo_id)"
job "$P-san" "$ID" wav "$TMP/fix/longo.wav" 100 5000000 60
RES="$(espera_resultado "$P-san" "$ID" 60)" || RES=""
confere "com folga no limite de tamanho o mesmo arquivo é aprovado" test "$(echo "$RES" | campo ok)" = "true"

echo "== job inválido e diretório estranho"
for ruim in \
  '{"formato":"mp3;rm -rf /","duracaoMaxSegundos":10,"tamanhoMaxSaidaBytes":1000,"timeoutSegundos":5}' \
  '{"formato":"mkv","duracaoMaxSegundos":10,"tamanhoMaxSaidaBytes":1000,"timeoutSegundos":5}' \
  '{"formato":"mp3","duracaoMaxSegundos":99999999,"tamanhoMaxSaidaBytes":1000,"timeoutSegundos":5}' \
  '{"formato":"mp3","duracaoMaxSegundos":10,"tamanhoMaxSaidaBytes":1000,"timeoutSegundos":5,"extra":"x"}' \
  '{"formato":"mp3","duracaoMaxSegundos":0,"tamanhoMaxSaidaBytes":1000,"timeoutSegundos":5}' \
  $'{"formato":"mp3","duracaoMaxSegundos":10,"tamanhoMaxSaidaBytes":1000,"timeoutSegundos":5}\n{"formato":"mp3","duracaoMaxSegundos":10,"tamanhoMaxSaidaBytes":1000,"timeoutSegundos":5}' \
  'não é json'; do
  ID="$(novo_id)"
  docker exec -i "$P-san" sh -c "mkdir /jobs/$ID && cat > /jobs/$ID/in.bin" < "$TMP/fix/a.mp3"
  printf '%s\n' "$ruim" | docker exec -i "$P-san" sh -c "cat > /jobs/$ID/job.json"
  docker exec "$P-san" sh -c "touch /jobs/$ID/pronto"
  RES="$(espera_resultado "$P-san" "$ID" 30)" || RES=""
  confere "job.json fora do formato é job_invalido: ${ruim:0:60}" test "$(echo "$RES" | campo motivo)" = "job_invalido"
done

ID="$(novo_id)"
docker exec "$P-san" sh -c "mkdir /jobs/$ID && ln -s /etc/passwd /jobs/$ID/in.bin && echo '{\"formato\":\"mp3\",\"duracaoMaxSegundos\":10,\"tamanhoMaxSaidaBytes\":1000,\"timeoutSegundos\":5}' > /jobs/$ID/job.json && touch /jobs/$ID/pronto"
RES="$(espera_resultado "$P-san" "$ID" 30)" || RES=""
confere "symlink no diretório do job é job_invalido (nunca é seguido)" test "$(echo "$RES" | campo motivo)" = "job_invalido"

docker exec "$P-san" sh -c "mkdir /jobs/nao-e-guid && touch /jobs/nao-e-guid/in.bin /jobs/nao-e-guid/pronto"
ID="$(novo_id)"
docker exec -i "$P-san" sh -c "mkdir /jobs/$ID && cat > /jobs/$ID/in.bin" < "$TMP/fix/a.mp3"
printf '{"formato":"mp3","duracaoMaxSegundos":10,"tamanhoMaxSaidaBytes":1000000,"timeoutSegundos":30}\n' | docker exec -i "$P-san" sh -c "cat > /jobs/$ID/job.json"
sleep 4
confere "diretório sem o arquivo pronto não é processado" bash -c "! docker exec '$P-san' test -e /jobs/$ID/result.json"
confere "diretório que não é GUID é ignorado" bash -c "! docker exec '$P-san' test -e /jobs/nao-e-guid/result.json"

echo "== timeout: um ffmpeg que trava é morto"
ID="$(novo_id)"
T0=$SECONDS
job "$P-lento" "$ID" mp3 "$TMP/fix/a.mp3" 10 1000000 2
RES="$(espera_resultado "$P-lento" "$ID" 30)" || RES=""
confere "estourou o timeout de 2 s: motivo timeout em $((SECONDS - T0)) s" test "$(echo "$RES" | campo motivo)" = "timeout"
confere "o processo travado foi morto" test "$(docker exec "$P-lento" sh -c 'ps | grep -c "[s]leep 120"' || true)" = "0"

echo
if [[ "$FALHAS" -eq 0 ]]; then
  echo "sanitizer: tudo certo."
else
  echo "sanitizer: $FALHAS falha(s)." >&2
  docker logs --tail 40 "$P-san" >&2 || true
  exit 1
fi
