#!/bin/sh
# =============================================================================
# WebRadio — infra/harbor-borda/entrypoint.sh (E2-F11)
# Gera o allow/deny do nginx a partir de HARBOR_PERMITIDOS (IPs ou redes CIDR, IPv4 ou IPv6, separados por vírgula ou espaço;
# ex.: "203.0.113.10, 10.8.0.0/24" para o IP do estúdio e a VPN). Entrada que não é IP/CIDR aborta a subida: um erro de
# digitação nunca vira "libera tudo". Lista vazia = ninguém entra (deny all) e um aviso no log.
#
# "Nunca 0.0.0.0" (06 F11, risco aceito 2 do 05 §4: harbor sem TLS só vale com a porta alcançável por VPN ou lista de IPs): a subida
# também aborta com HARBOR_BIND_IP = 0.0.0.0 ou :: (o live.yml o repassa) e com qualquer entrada de HARBOR_PERMITIDOS de prefixo /0
# (0.0.0.0/0, ::/0). Só HARBOR_LIBERAR_TUDO=1 passa, e só o docker-compose.dev.yml o define.
# =============================================================================
set -eu

destino=/tmp/harbor-borda/permitidos.conf
mkdir -p /tmp/harbor-borda

v4='^([0-9]{1,3}\.){3}[0-9]{1,3}(/([0-9]|[12][0-9]|3[0-2]))?$'
v6='^[0-9A-Fa-f:]*:[0-9A-Fa-f:]*(/([0-9]|[1-9][0-9]|1[01][0-9]|12[0-8]))?$'

aberto() {
  echo "harbor-borda: $1 deixaria o ao vivo (sem TLS) aberto para qualquer IP. Use o IP da VPN/estúdio e uma lista de IPs; HARBOR_LIBERAR_TUDO=1 existe só no docker-compose.dev.yml." >&2
  exit 1
}

if [ "${HARBOR_LIBERAR_TUDO:-}" != "1" ]; then
  # Qualquer grafia do endereço "todos": 0.0.0.0, 00.0.0.0, ::, ::0, 0::, 0::0, 0:0:0:0:0:0:0:0, com ou sem colchetes.
  if printf '%s' "${HARBOR_BIND_IP:-}" | tr -d ' []' | grep -Eq '^(0+(\.0+){3}|[0:]*:[0:]*)$'; then aberto "HARBOR_BIND_IP"; fi
  for item in $(printf '%s' "${HARBOR_PERMITIDOS:-}" | tr ',' ' '); do
    case "$item" in
      */0) aberto "HARBOR_PERMITIDOS com prefixo /0" ;;
    esac
  done
fi

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
