#!/bin/sh
# E4-F04 — sanitizer (E4-D1, E4-D2, S-C01, S-A01, S-B09). Laço de 1 s sobre /jobs: cada diretório `{guid}` com um arquivo `pronto` é um job do Worker.
# Entrada: in.bin (o original, NÃO confiável) e job.json ({ formato, duracaoMaxSegundos, tamanhoMaxSaidaBytes, timeoutSegundos }).
# Saída: out.mp3 e result.json ({ ok, motivo?, duracaoSegundos?, streamsAudio, codec }), cada um escrito em arquivo temporário e movido com rename (atômico);
# no fim apaga in.bin. `motivo` é de uma LISTA FECHADA de códigos; nunca texto do stderr do ffmpeg (o Worker troca o código pela mensagem fixa).
# Códigos de `motivo` (lista fechada): job_invalido, formato_invalido, streams_audio_invalido, stream_nao_audio, codec_nao_permitido, falha_na_decodificacao, timeout.
# Os seis primeiros são do ARQUIVO (rejeição determinística); `timeout` é falha de processamento (o Worker conta a tentativa, E4-D13). A duração mínima e máxima
# do tipo são conferidas pelo Worker sobre a SAÍDA: aqui o `-t` só corta em duração máxima + 1 s.
# Nada do conteúdo do arquivo é interpolado em comando: formato e números são validados antes de qualquer uso.
set -u

JOBS="${SANITIZER_JOBS:-/jobs}"
FFMPEG="${SANITIZER_FFMPEG:-/usr/local/bin/ffmpeg}"
FFPROBE="${SANITIZER_FFPROBE:-/usr/local/bin/ffprobe}"
UMA_VEZ="${SANITIZER_UMA_VEZ:-0}" # teste: processa o que houver e sai

# Resultado de um job: $1 = diretório, $2 = JSON já montado. O Worker só lê result.json (rename => nunca pela metade).
escrever_resultado() {
  printf '%s\n' "$2" > "$1/result.json.tmp" && mv -f "$1/result.json.tmp" "$1/result.json"
}

rejeitar() { # $1 = diretório, $2 = código do motivo (lista fechada), $3 = streams de áudio, $4 = codec (já validado por regex)
  rm -f "$1/in.bin" "$1/out.tmp.mp3" "$1/out.mp3"
  escrever_resultado "$1" "{\"ok\":false,\"motivo\":\"$2\",\"streamsAudio\":${3:-0},\"codec\":\"${4:-}\"}"
  echo "sanitizer: job $(basename "$1") recusado: $2"
}

processar() {
  d="$1"
  id="$(basename "$d")"

  # Só diretório de job legítimo: nome GUID e nenhum symlink dentro (o Worker e o sanitizer dividem o tmpfs; um symlink apontaria para fora dele).
  echo "$id" | grep -Eq '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$' || return 0
  [ -L "$d" ] && return 0
  [ -f "$d/pronto" ] && [ ! -L "$d/pronto" ] || return 0
  [ -e "$d/result.json" ] && return 0
  if [ -n "$(find "$d" -type l 2>/dev/null)" ]; then
    rejeitar "$d" job_invalido 0 ""
    return 0
  fi
  if [ ! -f "$d/in.bin" ] || [ ! -f "$d/job.json" ]; then
    rejeitar "$d" job_invalido 0 ""
    return 0
  fi

  # job.json: UMA linha, exatamente neste formato (o Worker o escreve assim); qualquer desvio é job inválido.
  job="$(head -c 512 "$d/job.json")"
  if ! echo "$job" | grep -Eq '^\{"formato":"(mp3|ogg|wav|flac)","duracaoMaxSegundos":[0-9]{1,5},"tamanhoMaxSaidaBytes":[0-9]{1,10},"timeoutSegundos":[0-9]{1,4}\}$'; then
    rejeitar "$d" job_invalido 0 ""
    return 0
  fi
  formato="$(echo "$job" | sed -E 's/^\{"formato":"([a-z0-9]+)".*$/\1/')"
  duracao_max="$(echo "$job" | sed -E 's/.*"duracaoMaxSegundos":([0-9]+).*/\1/')"
  tamanho_max="$(echo "$job" | sed -E 's/.*"tamanhoMaxSaidaBytes":([0-9]+).*/\1/')"
  tempo="$(echo "$job" | sed -E 's/.*"timeoutSegundos":([0-9]+).*/\1/')"
  case "$formato" in mp3|ogg|wav|flac) ;; *) rejeitar "$d" job_invalido 0 ""; return 0 ;; esac
  [ "$duracao_max" -ge 1 ] && [ "$tamanho_max" -ge 1 ] && [ "$tempo" -ge 1 ] || { rejeitar "$d" job_invalido 0 ""; return 0; }

  # 1) ffprobe com o demuxer FORÇADO e só o protocolo file (S-C01 item 2). Uma linha `codec,tipo` por stream (a ordem é a do ffprobe, não a pedida).
  streams="$(timeout -s KILL "$tempo" "$FFPROBE" -v error -protocol_whitelist file -f "$formato" -show_entries stream=codec_type,codec_name -of csv=p=0 "$d/in.bin" 2>/dev/null)"
  rc=$?
  if [ "$rc" -ne 0 ]; then
    if [ "$rc" -ge 124 ]; then rejeitar "$d" timeout 0 ""; else rejeitar "$d" formato_invalido 0 ""; fi
    return 0
  fi
  audio="$(echo "$streams" | grep -c ',audio$')"
  codec="$(echo "$streams" | grep ',audio$' | head -n 1 | cut -d, -f1)"
  echo "$codec" | grep -Eq '^[a-z0-9_]{1,24}$' || codec=""
  if [ "$audio" -ne 1 ]; then
    rejeitar "$d" streams_audio_invalido "$audio" "$codec"
    return 0
  fi
  # Fora do mp3 (onde a capa APIC é um stream de vídeo ignorado pelo -map 0:a:0), qualquer stream que não seja áudio (Theora no OGG) é recusado.
  if [ "$formato" != "mp3" ] && [ "$(echo "$streams" | grep -vc ',audio$')" -gt 0 ]; then
    rejeitar "$d" stream_nao_audio "$audio" "$codec"
    return 0
  fi
  # Whitelist de codec (S-C01 item 3).
  case "$codec" in
    mp3|vorbis|opus|flac|pcm_s16le|pcm_s24le|pcm_s32le|pcm_f32le) ;;
    *) rejeitar "$d" codec_nao_permitido "$audio" "$codec"; return 0 ;;
  esac

  # 2) recodifica (S-C01 item 2): só 0:a:0, sem metadado nem capítulo, bitexact, 192k/44,1 kHz/estéreo; -t corta em duração máxima + 1 s (a duração que vale é a da
  # saída, conferida pelo Worker) e -fs limita o tamanho.
  rm -f "$d/out.tmp.mp3"
  timeout -s KILL "$tempo" "$FFMPEG" -nostdin -hide_banner -v error -protocol_whitelist file -f "$formato" -i "$d/in.bin" \
    -map 0:a:0 -vn -sn -dn -map_metadata -1 -map_chapters -1 -fflags +bitexact -flags:a +bitexact \
    -c:a libmp3lame -b:a 192k -ar 44100 -ac 2 -t "$((duracao_max + 1))" -fs "$tamanho_max" -f mp3 -y "$d/out.tmp.mp3" >/dev/null 2>&1
  rc=$?
  if [ "$rc" -ge 124 ]; then rejeitar "$d" timeout "$audio" "$codec"; return 0; fi
  if [ "$rc" -ne 0 ] || [ ! -s "$d/out.tmp.mp3" ] || [ -L "$d/out.tmp.mp3" ]; then
    rejeitar "$d" falha_na_decodificacao "$audio" "$codec"
    return 0
  fi

  # 3) duração da SAÍDA (S-A01): o cabeçalho do original pode mentir.
  dur="$(timeout -s KILL "$tempo" "$FFPROBE" -v error -protocol_whitelist file -f mp3 -show_entries format=duration -of csv=p=0 "$d/out.tmp.mp3" 2>/dev/null | head -n 1)"
  echo "$dur" | grep -Eq '^[0-9]{1,6}(\.[0-9]{1,6})?$' || { rejeitar "$d" falha_na_decodificacao "$audio" "$codec"; return 0; }

  mv -f "$d/out.tmp.mp3" "$d/out.mp3"
  rm -f "$d/in.bin"
  escrever_resultado "$d" "{\"ok\":true,\"duracaoSegundos\":$dur,\"streamsAudio\":$audio,\"codec\":\"$codec\"}"
  echo "sanitizer: job $id ok (${formato}, ${codec}, ${dur}s)"
}

while :; do
  for d in "$JOBS"/*/; do
    [ -d "$d" ] || continue
    processar "${d%/}"
  done
  [ "$UMA_VEZ" = "1" ] && exit 0
  sleep 1
done
