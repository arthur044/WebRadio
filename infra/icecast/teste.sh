#!/usr/bin/env bash
# =============================================================================
# WebRadio — infra/icecast/teste.sh (E1-F14)
# Testa a IMAGEM do Icecast com o mesmo hardening do compose (sem root, read_only,
# cap_drop ALL, tmpfs em /tmp): o entrypoint recusa segredo ruim ANTES de subir, o
# icecast.xml renderizado fica só no tmpfs e admin/source exigem credencial.
# Uso (na raiz do repositório):  infra/icecast/teste.sh
# Os segredos são sintéticos, gerados na hora num diretório temporário. A porta 8000
# é publicada só em 127.0.0.1, numa porta livre escolhida pelo Docker.
# =============================================================================
set -euo pipefail
export LC_ALL=C

IMAGEM="webradio-icecast:teste"
NOME="webradio-icecast-teste-$$"
TMP="$(mktemp -d)"
FALHAS=0
ENDURECIDO=(--read-only --cap-drop ALL --security-opt no-new-privileges:true --tmpfs /tmp:uid=100,gid=101,mode=0700)

limpar() {
  docker rm -f "$NOME" >/dev/null 2>&1 || true
  rm -rf "$TMP"
}
trap limpar EXIT

ok()    { echo "  ok    $*"; }
falha() { echo "  FALHA $*" >&2; FALHAS=$((FALHAS + 1)); }
confere() { if "${@:2}"; then ok "$1"; else falha "$1"; fi; }

segredos() {
  # $1 = pasta; cria os 4 arquivos válidos, legíveis pelo uid do contêiner.
  mkdir -p "$1"
  printf '%s' "fonte-$(openssl rand -hex 16)" > "$1/icecast_source_password"
  printf '%s' "admin-$(openssl rand -hex 16)" > "$1/icecast_admin_password"
  printf '%s' "relay-$(openssl rand -hex 16)" > "$1/icecast_relay_password"
  printf '%s' "operador-radio" > "$1/icecast_admin_user"
  chmod 0755 "$1"
  chmod 0644 "$1"/*
}

deve_recusar() {
  # $1 = descrição, $2 = pasta de segredos, $3 = trecho esperado na mensagem
  local saida
  if saida="$(timeout 30 docker run --rm "${ENDURECIDO[@]}" -v "$2:/run/secrets:ro" "$IMAGEM" 2>&1)"; then
    falha "$1: o contêiner subiu"
  elif [[ "$saida" == *"$3"* ]]; then
    ok "$1"
  else
    falha "$1: recusou, mas com outra mensagem: $saida"
  fi
}

echo "== build"
docker build -q -f infra/icecast/Dockerfile -t "$IMAGEM" . >/dev/null

echo "== o entrypoint recusa segredo ruim antes de subir"
segredos "$TMP/sem-arquivo"; rm "$TMP/sem-arquivo/icecast_relay_password"
deve_recusar "segredo ausente" "$TMP/sem-arquivo" "não consegui ler icecast_relay_password"

segredos "$TMP/vazio"; : > "$TMP/vazio/icecast_source_password"
deve_recusar "segredo vazio" "$TMP/vazio" "icecast_source_password está vazio"

segredos "$TMP/curto"; printf 'curta123' > "$TMP/curto/icecast_admin_password"
deve_recusar "senha curta" "$TMP/curto" "icecast_admin_password tem menos de 24"

segredos "$TMP/alfabeto"; printf '%s' 'senha</source-password><x>aaaaaaaaaaaa' > "$TMP/alfabeto/icecast_source_password"
deve_recusar "caractere que quebraria o XML" "$TMP/alfabeto" "fora de [A-Za-z0-9_-]"

segredos "$TMP/exemplo"; printf '%s' '__GERAR__' > "$TMP/exemplo/icecast_source_password"
deve_recusar "marcador __GERAR__" "$TMP/exemplo" "icecast_source_password tem menos de 24"

segredos "$TMP/admin"; printf 'admin' > "$TMP/admin/icecast_admin_user"
deve_recusar "usuário admin = admin" "$TMP/admin" "icecast_admin_user"

echo "== sobe com segredos válidos"
segredos "$TMP/bom"
docker run -d --name "$NOME" "${ENDURECIDO[@]}" -v "$TMP/bom:/run/secrets:ro" -p 127.0.0.1::8000 "$IMAGEM" >/dev/null
BASE="http://$(docker port "$NOME" 8000/tcp | head -n1)"
for _ in $(seq 1 30); do
  curl -fsS -o /dev/null "$BASE/status-json.xsl" 2>/dev/null && break
  sleep 1
done
confere "status-json.xsl responde" curl -fsS -o /dev/null "$BASE/status-json.xsl"
confere "roda sem root" test "$(docker exec "$NOME" id -u)" != "0"

xml() { docker exec "$NOME" grep -q -e "$1" /tmp/icecast/icecast.xml; }
if xml '@@[A-Z_][A-Z_]*@@'; then falha "sobrou marcador no XML"; else ok "sem marcador no XML renderizado"; fi
confere "sources = 2 (S-A07)" xml '<sources>2</sources>'
confere "mount hidden (S-A07)" xml '<hidden>1</hidden>'
confere "source-timeout (S-A07)" xml '<source-timeout>'
confere "XML com modo 600" test "$(docker exec "$NOME" stat -c %a /tmp/icecast/icecast.xml)" = "600"
confere "access log descartado (S-B04)" test "$(docker exec "$NOME" readlink /tmp/icecast/access.log)" = "/dev/null"

# A senha não pode aparecer no ambiente nem nos argumentos do processo.
FONTE="$(cat "$TMP/bom/icecast_source_password")"
if docker exec "$NOME" sh -c "cat /proc/1/environ /proc/1/cmdline | tr '\0' '\n'" | grep -q -F "$FONTE"; then
  falha "senha de source no ambiente ou no argv"
else
  ok "senha fora do ambiente e do argv"
fi

codigo() { curl -s -o /dev/null -m 10 -w '%{http_code}' "$@"; }
confere "/admin sem credencial → 401" test "$(codigo "$BASE/admin/stats")" = "401"
confere "/admin com admin:hackme → 401" test "$(codigo -u admin:hackme "$BASE/admin/stats")" = "401"
confere "/admin com o usuário e a senha gerados → 200" test "$(codigo -u "operador-radio:$(cat "$TMP/bom/icecast_admin_password")" "$BASE/admin/stats")" = "200"
# Sonda de stream (corpo por stdin, sem Content-Length), como o Liquidsoap conecta. No Icecast 2.5 (Alpine 3.24) um PUT
# curto com --data-binary dá "000" (conexão fechada) com QUALQUER senha, até a certa: não prova nada sobre a autenticação.
# Aqui a recusa é 401 estrito (resposta imediata) e há um controle positivo com a senha certa: o source aceito segura a
# conexão, então o curl vê o "100 Continue" e estoura o -m (código 100, exit 28) ou, se o source cair antes, recebe 200.
# "000" (timeout sem resposta, recusa, reset) nunca vale como recusa nem como aceite.
fonte() { printf x | curl -s -o /dev/null -m 3 -w '%{http_code}' -H 'Content-Type: audio/mpeg' -T - "$@" "$BASE/radio.mp3"; }
aceito() { [[ "$1" == "100" || "$1" == "200" ]]; }
confere "PUT /radio.mp3 sem credencial → 401" test "$(fonte)" = "401"
confere "PUT /radio.mp3 com source:hackme → 401" test "$(fonte -u source:hackme)" = "401"
confere "PUT /radio.mp3 com a senha de source gerada → aceito (100 ou 200)" aceito "$(fonte -u "source:$(cat "$TMP/bom/icecast_source_password")")"
# O source de teste cai ao fechar a conexão do curl; espera o Icecast soltar o mount antes de conferir que está vazio.
for _ in $(seq 1 20); do [[ "$(codigo "$BASE/radio.mp3")" == "404" ]] && break; sleep 0.5; done
confere "mount sem fonte → 404" test "$(codigo "$BASE/radio.mp3")" = "404"

echo
if [[ "$FALHAS" -eq 0 ]]; then echo "icecast: tudo certo."; else echo "icecast: $FALHAS falha(s)." >&2; exit 1; fi
