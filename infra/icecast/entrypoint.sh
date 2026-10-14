#!/bin/sh
# =============================================================================
# WebRadio — infra/icecast/entrypoint.sh (E1-F14)
# Renderiza o icecast.xml no tmpfs (/tmp) a partir de /run/secrets e sobe o Icecast.
# Aborta ANTES de subir se: um segredo faltar, estiver vazio, sair do alfabeto
# [A-Za-z0-9_-] (o valor entra num XML por substituição de texto), for curto, o
# usuário admin for "admin", sobrar marcador @@...@@ ou o arquivo sair pequeno demais.
# =============================================================================
set -eu
export LC_ALL=C

SEGREDOS="${SEGREDOS_DIR:-/run/secrets}"
DESTINO_DIR=/tmp/icecast
DESTINO="$DESTINO_DIR/icecast.xml"
TEMPLATE=/etc/icecast/icecast.xml.template

falhar() { echo "icecast-entrypoint: $*" >&2; exit 1; }

ler() {
  # $1 = arquivo em $SEGREDOS, $2 = mínimo de caracteres
  arq="$SEGREDOS/$1"
  [ -r "$arq" ] || falhar "não consegui ler $1."
  valor="$(cat "$arq")"
  [ -n "$valor" ] || falhar "$1 está vazio."
  case "$valor" in
    *[!A-Za-z0-9_-]*) falhar "$1 tem caractere fora de [A-Za-z0-9_-]." ;;
  esac
  [ "${#valor}" -ge "$2" ] || falhar "$1 tem menos de $2 caracteres."
  printf '%s' "$valor"
}

SOURCE="$(ler icecast_source_password 24)"
ADMIN="$(ler icecast_admin_password 24)"
RELAY="$(ler icecast_relay_password 24)"
ADMIN_USER="$(ler icecast_admin_user 8)"
[ "$ADMIN_USER" != "admin" ] || falhar "icecast_admin_user não pode ser 'admin'."

HOSTNAME_PUBLICO="${ICECAST_HOSTNAME:-localhost}"
case "$HOSTNAME_PUBLICO" in *[!A-Za-z0-9.:-]*) falhar "ICECAST_HOSTNAME inválido." ;; esac
MAX_CLIENTES="${ICECAST_MAX_CLIENTES:-200}"
case "$MAX_CLIENTES" in ''|*[!0-9]*) falhar "ICECAST_MAX_CLIENTES precisa ser número." ;; esac

umask 077
mkdir -p "$DESTINO_DIR"
# Os valores já passaram pelo alfabeto acima: nenhum deles tem '|', '&' ou '\' para quebrar o sed.
sed -e "s|@@SOURCE_PASSWORD@@|$SOURCE|g" \
    -e "s|@@ADMIN_PASSWORD@@|$ADMIN|g" \
    -e "s|@@RELAY_PASSWORD@@|$RELAY|g" \
    -e "s|@@ADMIN_USER@@|$ADMIN_USER|g" \
    -e "s|@@HOSTNAME@@|$HOSTNAME_PUBLICO|g" \
    -e "s|@@MAX_CLIENTES@@|$MAX_CLIENTES|g" \
    "$TEMPLATE" > "$DESTINO"

if grep -q '@@[A-Z_]*@@' "$DESTINO"; then falhar "sobrou marcador no icecast.xml."; fi
[ "$(wc -c < "$DESTINO")" -ge 1500 ] || falhar "icecast.xml renderizado pequeno demais."

# S-B04: access log (IP em claro) descartado; error log no stderr do contêiner.
ln -sf /dev/null "$DESTINO_DIR/access.log"
ln -sf /proc/self/fd/2 "$DESTINO_DIR/error.log"

unset SOURCE ADMIN RELAY
exec icecast -c "$DESTINO"
