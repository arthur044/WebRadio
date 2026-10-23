#!/usr/bin/env bash
# =============================================================================
# WebRadio — infra/scripts/smoke-ao-vivo.sh (E2-F10, Definição de Pronto do Épico 2: itens 1, 3, 4 e 7)
#
# Roda contra o stack de DEV já no ar, com o seed ligado (SEED_DEV_DATA=true: o locutor1 existe), depois do
# smoke-compose.sh (que confere o primeiro login do Admin semeado, ainda com a troca de senha pendente):
#   docker compose -f docker-compose.yml -f docker-compose.dev.yml up -d --wait   (com SEED_DEV_DATA=true)
#   E2E_ADMIN_SENHA_NOVA=<senha> infra/scripts/smoke-ao-vivo.sh
#
#   1. um programa criado pela API para "agora" vira AoVivo em até 20 s (o Worker de verdade);
#   3. com a credencial do locutor dono do programa da janela, um source client de teste conecta pelo harbor-borda e o ao vivo
#      entra no ar; credencial errada, de outro locutor, revogada ou fora da janela é recusada;
#   4. ao encerrar o programa o ao vivo cai em até 30 s e o stream volta para a rotação SEM derrubar o ouvinte, com o Worker PARADO
#      (`docker compose stop worker` antes do encerrar, `start` depois): a derrubada depende só da API e do Liquidsoap;
#   7. o harbor-borda recusa um IP fora da lista antes de o Liquidsoap ver a conexão. (A base de produção publicar só a 8080 é
#      conferido pelo smoke-compose.sh, "base de PRODUÇÃO".)
#
# O source client de teste é um Liquidsoap (a imagem fixada do compose, que já está no host), não o ffmpeg: o runner não precisa de
# nada além do Docker. A senha nova do Admin vem do chamador (o E2E do CI usa a mesma): este script a define, o E2E só confere.
# Nunca imprime segredo. Acumula as falhas e sai com 1 se houve alguma.
# =============================================================================
set -euo pipefail
export LC_ALL=C

RAIZ="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$RAIZ"
COMPOSE=(docker compose -f docker-compose.yml -f docker-compose.dev.yml)
BASE="${SMOKE_BASE:-http://localhost:8080}"
HARBOR="${SMOKE_HARBOR:-127.0.0.1:8005}"
TMP="$(mktemp -d)"
FONTES=()
CONTINUO=""
WORKER_PARADO=0

: "${E2E_ADMIN_SENHA_NOVA:?Defina E2E_ADMIN_SENHA_NOVA (a senha que o Admin terá depois deste script; o E2E usa a mesma)}"
[[ "${GITHUB_ACTIONS:-}" == "true" ]] && echo "::add-mask::$E2E_ADMIN_SENHA_NOVA"

limpar() {
  [[ -n "$CONTINUO" ]] && kill "$CONTINUO" 2>/dev/null || true
  if ((${#FONTES[@]})); then docker rm -f "${FONTES[@]}" >/dev/null 2>&1 || true; fi
  docker rm -f smoke-borda-restrita >/dev/null 2>&1 || true
  # O DoD 4 para o Worker: se o script morrer no meio, o stack não pode ficar sem ele para quem vem depois (o E2E do CI).
  if [[ "$WORKER_PARADO" == 1 ]]; then "${COMPOSE[@]}" start worker >/dev/null 2>&1 || true; fi
  rm -rf "$TMP"
}
trap limpar EXIT

falhas=0
ok()    { printf '  ok    %s\n' "$1"; }
falha() { printf '  FALHA %s\n' "$1"; falhas=$((falhas + 1)); }
confere() { if "${@:2}"; then ok "$1"; else falha "$1"; fi; }

# --- utilidades -------------------------------------------------------------------------------------------------------
json() { python3 -I -c 'import json,sys; d=json.loads(sys.stdin.read()); print(eval(sys.argv[1], {"d": d}))' "$1"; }
api() { # $1 método, $2 caminho, $3 token ("-" = sem), $4 corpo JSON em arquivo (opcional); imprime "STATUS corpo"
  local args=(-s --max-time 15 -X "$1" -w '\n%{http_code}' -H 'Content-Type: application/json')
  [[ "$3" != "-" ]] && args+=(-H "Authorization: Bearer $3")
  [[ -n "${4:-}" ]] && args+=(--data-binary "@$4")
  local saida; saida="$(curl "${args[@]}" "$BASE$2")"
  printf '%s %s' "${saida##*$'\n'}" "${saida%$'\n'*}"
}
corpo_de() { printf '%s' "${1#* }"; }
status_de() { printf '%s' "${1%% *}"; }
iso() { date -u -d "$1" +%Y-%m-%dT%H:%M:%SZ; }

logs_liq() { "${COMPOSE[@]}" logs --no-color liquidsoap 2>&1; }
conta_liq() { logs_liq | grep -c -F -e "$1" || true; }
espera_mais() { # $1 texto, $2 contagem anterior, $3 segundos
  for _ in $(seq 1 "$3"); do [[ "$(conta_liq "$1")" -gt "$2" ]] && return 0; sleep 1; done
  return 1
}
espera_ate() { # $1 segundos, resto = comando
  local t="$1"; shift
  for _ in $(seq 1 "$t"); do "$@" && return 0; sleep 1; done
  return 1
}
programa_ao_vivo_e() { [[ "$(curl -s --max-time 10 "$BASE/api/v1/programas/ao-vivo" | json 'd.get("id","") if isinstance(d, dict) else ""' 2>/dev/null || true)" == "$1" ]]; }
no_ar() { [[ "$(curl -s --max-time 10 "$BASE/api/v1/stream/info" | json 'str(d["aoVivo"]["noAr"]).lower()' 2>/dev/null || true)" == "$1" ]]; }
worker_saudavel() { [[ "$(docker inspect -f '{{.State.Health.Status}}' "$("${COMPOSE[@]}" ps -q worker)" 2>/dev/null || true)" == healthy ]]; }
toca() {
  rm -f "$TMP/amostra.mp3" "$TMP/cab"
  curl -s -m 4 -D "$TMP/cab" -o "$TMP/amostra.mp3" "$BASE/stream/radio.mp3" || true
  grep -q -i '^content-type: audio/mpeg' "$TMP/cab" 2>/dev/null && [[ "$(stat -c %s "$TMP/amostra.mp3")" -gt 16000 ]]
}

LIQ_IMG="$(grep -m1 -oE 'savonet/liquidsoap@sha256:[0-9a-f]{64}' docker-compose.yml)"
cat > "$TMP/fonte.liq" <<'LIQ'
settings.log.stdout := true
settings.log.file := false
usuario = environment.get(default="x", "FONTE_USER")
host = environment.get(default="127.0.0.1", "FONTE_HOST")
porta = int_of_string(environment.get(default="8005", "FONTE_PORTA"))
senha = string.trim(file.contents("/run/secrets/senha"))
ao_vivo = metadata.map(insert_missing=true, fun (_) -> [("title", "Smoke ao vivo"), ("artist", "Locutor de teste")], sine(660.))
output.icecast(%mp3(bitrate=128), host=host, port=porta, user=usuario, password=senha, mount="live", fallible=true, ao_vivo)
LIQ
chmod 0644 "$TMP/fonte.liq"

fonte() { # $1 nome, $2 identificador (usuário), $3 arquivo com o segredo, $4 rede ("host" = a do host), $5 host, $6 porta
  docker rm -f "$1" >/dev/null 2>&1 || true
  docker run -d --name "$1" --network "${4:-host}" --user "$(id -u):$(id -g)" --read-only --cap-drop ALL \
    --security-opt no-new-privileges:true --tmpfs /tmp -e TZ=UTC -e FONTE_USER="$2" -e FONTE_HOST="${5:-${HARBOR%%:*}}" -e FONTE_PORTA="${6:-${HARBOR##*:}}" \
    -v "$TMP/fonte.liq:/etc/liquidsoap/fonte.liq:ro" -v "$3:/run/secrets/senha:ro" "$LIQ_IMG" /etc/liquidsoap/fonte.liq >/dev/null
  FONTES+=("$1")
}
sem_fonte() { docker rm -f "$@" >/dev/null 2>&1 || true; }

# --- quem é quem ------------------------------------------------------------------------------------------------------
echo "== Admin (senha nova) e locutor1 do seed"
EMAIL_ADMIN="$(cat secrets/seed_admin_email)"
SENHA_SEMEADA="$(cat secrets/seed_admin_senha)"
SENHA_DEV="$(cat secrets/seed_dev_senha)"
login() { # $1 e-mail, $2 senha; imprime a resposta (corpo) ou "" se o login falhou
  python3 -I -c 'import json,sys; print(json.dumps({"email": sys.argv[1], "senha": sys.argv[2]}))' "$1" "$2" > "$TMP/login.json"
  local r; r="$(api POST /api/v1/auth/login - "$TMP/login.json")"
  [[ "$(status_de "$r")" == 200 ]] && corpo_de "$r" || true
}
resp="$(login "$EMAIL_ADMIN" "$SENHA_SEMEADA")"
if [[ -n "$resp" && "$(json 'd["deveTrocarSenha"]' <<<"$resp")" == "True" ]]; then
  python3 -I -c 'import json,sys; print(json.dumps({"senhaAtual": sys.argv[1], "novaSenha": sys.argv[2]}))' "$SENHA_SEMEADA" "$E2E_ADMIN_SENHA_NOVA" > "$TMP/troca.json"
  r="$(api POST /api/v1/auth/trocar-senha "$(json 'd["accessToken"]' <<<"$resp")" "$TMP/troca.json")"
  esperado=204; [[ "$(status_de "$r")" == "$esperado" ]] && ok "senha do Admin trocada" || falha "troca da senha do Admin: HTTP $(status_de "$r")"
fi
resp="$(login "$EMAIL_ADMIN" "$E2E_ADMIN_SENHA_NOVA")"
if [[ -z "$resp" ]]; then falha "login do Admin com a senha nova"; echo "smoke-ao-vivo: sem Admin não há como seguir."; exit 1; fi
ADMIN="$(json 'd["accessToken"]' <<<"$resp")"; [[ "${GITHUB_ACTIONS:-}" == "true" ]] && echo "::add-mask::$ADMIN"
ok "Admin autenticado"
resp="$(login locutor1@example.com "$SENHA_DEV")"
[[ -n "$resp" ]] || { falha "login do locutor1 (o stack subiu com SEED_DEV_DATA=true?)"; exit 1; }
LOC1="$(json 'd["accessToken"]' <<<"$resp")"; LOC1_ID="$(json 'd["usuario"]["id"]' <<<"$resp")"
ULTIMO_LOGIN=$SECONDS
[[ "${GITHUB_ACTIONS:-}" == "true" ]] && echo "::add-mask::$LOC1"
# O "outro locutor" é o próprio Admin (tem papel de transmissão e nenhum programa na janela): um login a menos, porque /auth/login tem
# 5 por minuto por IP (janela fixa) e o smoke-compose.sh e o E2E logam no mesmo stack.
ok "locutor1 autenticado"

credencial() { # $1 token, $2 rótulo, $3 senha atual da conta; grava $TMP/cred-$2.{id,segredo}
  python3 -I -c 'import json,sys; print(json.dumps({"senhaAtual": sys.argv[1]}))' "$3" > "$TMP/gerar.json"
  local r; r="$(api POST /api/v1/locutores/me/credencial-transmissao "$1" "$TMP/gerar.json")"
  [[ "$(status_de "$r")" == 201 ]] || return 1
  json 'd["identificador"]' <<<"$(corpo_de "$r")" > "$TMP/cred-$2.id"
  json 'd["segredo"]' <<<"$(corpo_de "$r")" > "$TMP/cred-$2.segredo"
  chmod 0644 "$TMP/cred-$2.segredo"
}
programa_agora() { # $1 locutorId, $2 título; imprime o id do programa
  local inicio fim
  inicio="$(date -u +%Y-%m-%dT%H:%M:%SZ)"; fim="$(iso '+30 minutes')"
  python3 -I -c 'import json,sys; print(json.dumps({"titulo": sys.argv[1], "descricao": None, "locutorId": sys.argv[2], "inicioUtc": sys.argv[3], "fimUtc": sys.argv[4]}))' \
    "$2" "$1" "$inicio" "$fim" > "$TMP/prog.json"
  local r id conflito
  for _ in 1 2 3; do
    r="$(api POST /api/v1/programas "$ADMIN" "$TMP/prog.json")"
    if [[ "$(status_de "$r")" == 201 ]]; then json 'd["id"]' <<<"$(corpo_de "$r")"; return 0; fi
    [[ "$(status_de "$r")" == 409 ]] || return 1
    # O seed ocupa uma janela por dia e a rodada anterior do E2E pode ter deixado um programa de agora: encerra quem conflita.
    conflito="$(json 'd["programaConflitanteId"]' <<<"$(corpo_de "$r")")"
    api POST "/api/v1/programas/$conflito/encerrar" "$ADMIN" >/dev/null
  done
  return 1
}

# O ouvinte que não sai durante o roteiro inteiro: o stream de saída não cai em nenhum dos casos (DoD 4).
curl -s -m 600 -o "$TMP/continuo.mp3" "$BASE/stream/radio.mp3" &
CONTINUO=$!

echo "== DoD 1: programa criado para agora vira AoVivo em até 20 s"
PROG1="$(programa_agora "$LOC1_ID" "[smoke] ao vivo 1")" || { falha "criar o programa de agora"; exit 1; }
T0=$SECONDS
confere "o Worker o põe AoVivo em até 20 s" espera_ate 20 programa_ao_vivo_e "$PROG1"
echo "        (levou $((SECONDS - T0)) s)"

echo "== DoD 3: recusas"
credencial "$LOC1" l1a "$SENHA_DEV" || { falha "gerar a credencial do locutor1"; exit 1; }
credencial "$ADMIN" l2 "$E2E_ADMIN_SENHA_NOVA" || { falha "gerar a credencial do Admin"; exit 1; }
ID1="$(cat "$TMP/cred-l1a.id")"; ID2="$(cat "$TMP/cred-l2.id")"
ok "credenciais geradas (locutor1 e Admin)"
printf '%s' "$(head -c 32 /dev/urandom | base64 | tr '+/' '-_' | tr -d '=\n')" > "$TMP/segredo-errado"; chmod 0644 "$TMP/segredo-errado"

tenta_recusada() { # $1 descrição, $2 identificador, $3 arquivo do segredo
  local antes; antes="$(conta_liq 'Recusado:')"
  fonte smoke-fonte "$2" "$3"
  confere "$1" espera_mais 'Recusado:' "$antes" 40
  sem_fonte smoke-fonte
}
CON0="$(conta_liq 'Source client conectado')"
tenta_recusada "segredo errado é recusado" "$ID1" "$TMP/segredo-errado"
tenta_recusada "credencial de outro locutor (sem programa na janela) é recusada" "$ID2" "$TMP/cred-l2.segredo"
confere "nenhuma dessas conexões entrou no ar" test "$(conta_liq 'Source client conectado')" = "$CON0"

echo "== DoD 3: a credencial certa, do dono do programa da janela, entra no ar"
fonte smoke-fonte "$ID1" "$TMP/cred-l1a.segredo"
confere "o source client conecta pelo harbor-borda" espera_mais 'Source client conectado' "$CON0" 60
confere "aoVivo.noAr passa a true em /stream/info" espera_ate 30 no_ar true
confere "o stream segue tocando durante o ao vivo" toca

echo "== DoD 4: com o Worker PARADO, encerrar o programa derruba o ao vivo em até 30 s e o stream volta para a rotação"
# O Worker só muda o status por horário (Agendado -> AoVivo -> Finalizado). Parado, quem derruba é a API (o encerrar e a autorização
# pela janela, em SQL) com o Liquidsoap reconferindo a cada 10 s: o ouvinte contínuo, que já está conectado, não pode notar.
WORKER_PARADO=1 # antes do stop: se ele morrer no meio, o trap religa o Worker
"${COMPOSE[@]}" stop worker >/dev/null 2>&1 || falha "docker compose stop worker"
confere "o Worker está parado" test -z "$("${COMPOSE[@]}" ps -q --status running worker)"
DESC0="$(conta_liq 'Source client desconectado')"
T0=$SECONDS
r="$(api POST "/api/v1/programas/$PROG1/encerrar" "$LOC1")"
esperado=200; [[ "$(status_de "$r")" == "$esperado" ]] && ok "o locutor encerra o próprio programa" || falha "encerrar: HTTP $(status_de "$r")"
confere "o ao vivo cai" espera_mais 'Source client desconectado' "$DESC0" 30
confere "caiu em $((SECONDS - T0)) s (limite de 30 s)" test $((SECONDS - T0)) -le 30
confere "aoVivo.noAr volta a false" espera_ate 20 no_ar false
confere "o stream volta para a rotação" toca
sem_fonte smoke-fonte
"${COMPOSE[@]}" start worker >/dev/null 2>&1 && WORKER_PARADO=0
confere "o Worker volta a ficar healthy" espera_ate 90 worker_saudavel

echo "== DoD 3: fora da janela e credencial revogada"
CON0="$(conta_liq 'Source client conectado')"
tenta_recusada "credencial certa depois do encerramento (fora da janela) é recusada" "$ID1" "$TMP/cred-l1a.segredo"
PROG2="$(programa_agora "$LOC1_ID" "[smoke] ao vivo 2")" || { falha "criar o segundo programa de agora"; exit 1; }
credencial "$LOC1" l1b "$SENHA_DEV" || { falha "gerar a segunda credencial do locutor1"; exit 1; }
ok "segunda credencial gerada (a primeira foi revogada na mesma transação)"
tenta_recusada "a credencial revogada é recusada mesmo dentro da janela" "$ID1" "$TMP/cred-l1a.segredo"
confere "nenhuma dessas conexões entrou no ar" test "$(conta_liq 'Source client conectado')" = "$CON0"
fonte smoke-fonte "$(cat "$TMP/cred-l1b.id")" "$TMP/cred-l1b.segredo"
confere "a credencial nova entra no ar" espera_mais 'Source client conectado' "$CON0" 60
DESC0="$(conta_liq 'Source client desconectado')"
api POST "/api/v1/programas/$PROG2/encerrar" "$ADMIN" >/dev/null
confere "o Admin também encerra: o ao vivo cai em até 30 s" espera_mais 'Source client desconectado' "$DESC0" 30
sem_fonte smoke-fonte

echo "== DoD 7: o harbor-borda recusa IP fora da lista antes de o Liquidsoap ver a conexão"
# Uma segunda borda, com a lista só de 192.0.2.0/24 (TEST-NET-1), na mesma imagem, no mesmo arquivo de configuração e na mesma rede
# playout da borda do stack: o source client novo vem de outro IP. É um `docker run` (e não `compose run`) para não depender de
# o compose reconciliar as redes do projeto.
BORDA_ID="$("${COMPOSE[@]}" ps -q harbor-borda)"
BORDA_IMG="$(docker inspect -f '{{.Config.Image}}' "$BORDA_ID")"
REDE_PLAYOUT="$(docker inspect -f '{{range $k, $v := .NetworkSettings.Networks}}{{$k}}{{"\n"}}{{end}}' "$BORDA_ID" | grep '_playout$' | head -n1)"
docker run -d --name smoke-borda-restrita --network "$REDE_PLAYOUT" --read-only --cap-drop ALL --security-opt no-new-privileges:true \
  --tmpfs /tmp:uid=101,gid=101 -e HARBOR_PERMITIDOS=192.0.2.0/24 \
  -v "$RAIZ/infra/harbor-borda/nginx.conf:/etc/nginx/nginx.conf:ro" \
  -v "$RAIZ/infra/harbor-borda/entrypoint.sh:/usr/local/bin/harbor-borda.sh:ro" \
  --entrypoint /usr/local/bin/harbor-borda.sh "$BORDA_IMG" >/dev/null
sleep 3
ANTES="$(conta_liq 'Recusado:'):$(conta_liq 'Autorizado')"
fonte smoke-fonte-restrita "$ID1" "$TMP/cred-l1b.segredo" "$REDE_PLAYOUT" smoke-borda-restrita 8005
espera_ate 30 bash -c "docker logs smoke-borda-restrita 2>&1 | grep -q ' 403 '" || true
confere "o proxy encerra a sessão com 403 (fora da lista)" bash -c "docker logs smoke-borda-restrita 2>&1 | grep -q ' 403 '"
DEPOIS="$(conta_liq 'Recusado:'):$(conta_liq 'Autorizado')"
confere "o Liquidsoap nem chegou a ver a tentativa" test "$ANTES" = "$DEPOIS"
sem_fonte smoke-fonte-restrita
docker rm -f smoke-borda-restrita >/dev/null 2>&1 || true

echo "== o ouvinte contínuo nunca caiu"
confere "o curl do stream segue conectado depois de todos os casos" kill -0 "$CONTINUO"
T1="$(stat -c %s "$TMP/continuo.mp3")"; sleep 5; T2="$(stat -c %s "$TMP/continuo.mp3")"
confere "e recebendo áudio" test "$T2" -gt "$T1"

# O E2E (próximo passo do CI) loga no mesmo IP e a janela de 5 logins por minuto é fixa: espera 62 s depois do último login deste
# script para o E2E começar numa janela nova (o script costuma já ter passado disso).
RESTANTE=$((62 - (SECONDS - ULTIMO_LOGIN)))
if ((RESTANTE > 0)); then echo "(aguardando ${RESTANTE} s para a janela de logins renovar)"; sleep "$RESTANTE"; fi

echo
if [[ $falhas -gt 0 ]]; then echo "smoke-ao-vivo: $falhas falha(s)."; exit 1; fi
echo "smoke-ao-vivo: tudo ok."
