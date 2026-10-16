#!/usr/bin/env bash
# =============================================================================
# WebRadio — infra/harbor-borda/teste.sh (E2-F11)
# Sobe o harbor-borda de verdade (imagem fixada, mesmo endurecimento do compose) numa rede Docker própria, na frente de um
# "liquidsoap" falso que só registra quem conectou, e confere:
#   1. HARBOR_PERMITIDOS com entrada que não é IP/CIDR aborta a subida, sem imprimir o valor;
#   2. IP fora da lista é recusado pelo proxy ANTES de chegar ao Liquidsoap; IP da lista passa;
#   3. lista vazia: ninguém entra;
#   4. limit_conn: a terceira conexão simultânea do mesmo IP é recusada;
#   5. o log tem o IP anonimizado (/24) e nunca o IP completo do cliente;
#   6. roda sem root, read-only, sem capabilities.
# Uso (na raiz do repositório):  infra/harbor-borda/teste.sh
# =============================================================================
set -euo pipefail
export LC_ALL=C

NGINX="nginxinc/nginx-unprivileged:1.31.6-alpine3.24@sha256:884e0b7f42f303641b0f4e78755aea647521dca0b89ca3be7992ad78b5c07434"
PYTHON="python:3.12-alpine"
P="wr-harbor-teste-$$"
REDE="172.31.250"
FALHAS=0
ENDURECIDO=(--read-only --cap-drop ALL --security-opt no-new-privileges:true --tmpfs /tmp:uid=101,gid=101)

limpar() {
  docker rm -f "$P-borda" "$P-liq" >/dev/null 2>&1 || true
  docker network rm "$P" >/dev/null 2>&1 || true
}
trap limpar EXIT

ok()    { echo "  ok    $*"; }
falha() { echo "  FALHA $*" >&2; FALHAS=$((FALHAS + 1)); }
confere() { if "${@:2}"; then ok "$1"; else falha "$1"; fi; }

borda() {
  # $1 = HARBOR_PERMITIDOS
  docker rm -f "$P-borda" >/dev/null 2>&1 || true
  docker run -d --name "$P-borda" --network "$P" --ip "$REDE.2" "${ENDURECIDO[@]}" \
    -e HARBOR_PERMITIDOS="$1" \
    -v "$PWD/infra/harbor-borda/nginx.conf:/etc/nginx/nginx.conf:ro" \
    -v "$PWD/infra/harbor-borda/entrypoint.sh:/usr/local/bin/harbor-borda.sh:ro" \
    --entrypoint /usr/local/bin/harbor-borda.sh "$NGINX" >/dev/null
  sleep 2
}

cliente() {
  # $1 = último octeto do IP do cliente, $2 = quantas conexões simultâneas. Cada uma manda um "SOURCE" e espera 3 s.
  # Imprime quantas o proxy deixou passar até o fim (a recusa fecha a conexão sem resposta).
  docker run --rm --network "$P" --ip "$REDE.$1" "$PYTHON" python -c "
import socket, time
abertas = []
for i in range($2):
    s = socket.create_connection(('$REDE.2', 8005), timeout=3)
    s.sendall(b'SOURCE /live HTTP/1.0\r\n\r\n')
    abertas.append(s)
    time.sleep(0.3)
time.sleep(1)
vivas = 0
for s in abertas:
    try:
        s.settimeout(0.5)
        dado = s.recv(1)
        vivas += 0 if dado == b'' else 1
    except socket.timeout:
        vivas += 1
    except OSError:
        pass
print(vivas)
" 2>/dev/null || echo erro
}

conexoes_no_liquidsoap() { docker logs "$P-liq" 2>&1 | grep -c "CONEXAO" || true; }

echo "== 1. lista inválida aborta a subida"
for ruim in "10.0.0.0/33" "300.1.1.1" "estudio.exemplo.com" "1.2.3.4;deny" "all"; do
  saida="$(timeout 30 docker run --rm --network none "${ENDURECIDO[@]}" -e HARBOR_PERMITIDOS="$ruim" \
    -v "$PWD/infra/harbor-borda/nginx.conf:/etc/nginx/nginx.conf:ro" \
    -v "$PWD/infra/harbor-borda/entrypoint.sh:/usr/local/bin/harbor-borda.sh:ro" \
    --entrypoint /usr/local/bin/harbor-borda.sh "$NGINX" 2>&1)" && codigo=0 || codigo=$?
  if [[ $codigo -ne 0 ]] && grep -q "HARBOR_PERMITIDOS" <<<"$saida" && ! grep -qF -- "$ruim" <<<"$saida"; then ok "recusa '$ruim' sem ecoar o valor"; else falha "'$ruim': saiu $codigo: $saida"; fi
done

echo "== sobe a rede e o liquidsoap falso"
docker network create --subnet "$REDE.0/24" "$P" >/dev/null
docker run -d --name "$P-liq" --network "$P" --ip "$REDE.3" --network-alias liquidsoap "${ENDURECIDO[@]}" "$PYTHON" python -u -c "
import socketserver
class H(socketserver.BaseRequestHandler):
    def handle(self):
        print('CONEXAO de', self.client_address[0], flush=True)
        while self.request.recv(4096):
            pass
socketserver.ThreadingTCPServer(('0.0.0.0', 8005), H).serve_forever()
" >/dev/null
sleep 2

echo "== 2. só a lista passa, e a recusa acontece no proxy"
borda "$REDE.10, 10.8.0.0/24"
confere "roda sem root" test "$(docker exec "$P-borda" id -u)" != "0"
confere "IP da lista passa" test "$(cliente 10 1)" = "1"
confere "o Liquidsoap vê a conexão vinda do proxy" grep -q "CONEXAO de $REDE.2" <(docker logs "$P-liq" 2>&1)
antes="$(conexoes_no_liquidsoap)"
confere "IP fora da lista é recusado" test "$(cliente 20 1)" = "0"
confere "a conexão recusada não chegou ao Liquidsoap" test "$(conexoes_no_liquidsoap)" = "$antes"

echo "== 4. limit_conn por IP"
antes="$(conexoes_no_liquidsoap)"
confere "a terceira conexão simultânea do mesmo IP é recusada" test "$(cliente 10 3)" = "2"
confere "só duas chegaram ao Liquidsoap" test "$(( $(conexoes_no_liquidsoap) - antes ))" = "2"

echo "== 5. log anonimizado"
docker logs "$P-borda" > /tmp/"$P".log 2>&1 || true
confere "log tem o /24 do cliente" grep -q "$REDE.0/24" /tmp/"$P".log
if grep -qE "$REDE\.(10|20)([^0-9]|$)" /tmp/"$P".log; then falha "o log tem o IP completo do cliente"; else ok "nenhum IP completo de cliente no log"; fi
confere "recusa registrada (403) e limite (503) no access log" grep -q " 403 " /tmp/"$P".log
confere "limite de conexões registrado (503)" grep -q " 503 " /tmp/"$P".log
rm -f /tmp/"$P".log

echo "== 3. lista vazia: ninguém entra"
borda ""
confere "aviso de lista vazia" grep -q "nenhuma conexão de locutor será aceita" <(docker logs "$P-borda" 2>&1)
confere "nem o IP de antes entra" test "$(cliente 10 1)" = "0"

echo
if [[ "$FALHAS" -eq 0 ]]; then
  echo "harbor-borda: tudo certo."
else
  echo "harbor-borda: $FALHAS falha(s)." >&2
  docker logs --tail 40 "$P-borda" >&2 || true
  exit 1
fi
