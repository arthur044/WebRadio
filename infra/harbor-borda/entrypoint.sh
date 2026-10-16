#!/bin/sh
# =============================================================================
# WebRadio — infra/harbor-borda/entrypoint.sh (E2-F11)
# Gera o allow/deny do nginx a partir de HARBOR_PERMITIDOS (IPs ou redes CIDR, IPv4 ou IPv6, separados por vírgula ou espaço;
# ex.: "203.0.113.10, 10.8.0.0/24" para o IP do estúdio e a VPN). Entrada que não é IP/CIDR aborta a subida: um erro de
# digitação nunca vira "libera tudo". Lista vazia = ninguém entra (deny all) e um aviso no log.
# =============================================================================
set -eu

destino=/tmp/harbor-borda/permitidos.conf
mkdir -p /tmp/harbor-borda

v4='^([0-9]{1,3}\.){3}[0-9]{1,3}(/([0-9]|[12][0-9]|3[0-2]))?$'
v6='^[0-9A-Fa-f:]*:[0-9A-Fa-f:]*(/([0-9]|[1-9][0-9]|1[01][0-9]|12[0-8]))?$'

linhas=""
total=0
for item in $(printf '%s' "${HARBOR_PERMITIDOS:-}" | tr ',' ' '); do
  if printf '%s' "$item" | grep -Eq "$v4"; then
    for octeto in $(printf '%s' "${item%%/*}" | tr '.' ' '); do
      if [ "$octeto" -gt 255 ]; then
        echo "harbor-borda: HARBOR_PERMITIDOS tem um IPv4 inválido (octeto > 255). Corrija o .env." >&2
        exit 1
      fi
    done
  elif ! printf '%s' "$item" | grep -Eq "$v6"; then
    echo "harbor-borda: HARBOR_PERMITIDOS tem uma entrada que não é IP nem rede CIDR (item $((total + 1))). Corrija o .env." >&2
    exit 1
  fi
  linhas="${linhas}allow ${item};
"
  total=$((total + 1))
done

printf '%sdeny all;\n' "$linhas" > "$destino"
if [ "$total" -eq 0 ]; then
  echo "harbor-borda: HARBOR_PERMITIDOS vazio: nenhuma conexão de locutor será aceita." >&2
else
  echo "harbor-borda: $total entrada(s) permitida(s)." >&2
fi

exec nginx -g 'daemon off;'
