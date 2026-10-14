#!/usr/bin/env bash
# =============================================================================
# WebRadio — infra/scripts/smoke-compose.sh (E1-F16/F17, Definição de Pronto 1–4 e 8)
#
# Roda contra o stack JÁ no ar (docker compose -f docker-compose.yml -f docker-compose.dev.yml up -d --wait).
# Usado pelo job compose-smoke do CI e localmente. Confere TODOS os itens e acumula as falhas: imprime cada uma e sai com
# 1 no fim se houve alguma (0 se tudo ok). Nunca imprime segredo.
#
# O stack no ar é o do DEV (docker-compose.dev.yml); por isso as seções "base de produção" conferem a topologia do
# docker-compose.yml SOZINHO pelo `docker compose config --format json` (redes, portas, capabilities), sem subir nada.
#
#   infra/scripts/smoke-compose.sh
# =============================================================================
set -euo pipefail
export LC_ALL=C

RAIZ="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$RAIZ"
COMPOSE=(docker compose -f docker-compose.yml -f docker-compose.dev.yml)
BASE="${SMOKE_BASE:-http://localhost:8080}"
ORIGEM="${SMOKE_ORIGEM:-$BASE}"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

falhas=0
ok()    { printf '  ok    %s\n' "$1"; }
falha() { printf '  FALHA %s\n' "$1"; falhas=$((falhas + 1)); }
esperar() { # $1 descrição, $2 esperado, $3 obtido
  if [[ "$2" == "$3" ]]; then ok "$1 ($3)"; else falha "$1: esperado $2, veio $3"; fi
}
status() { curl -s -o /dev/null -w '%{http_code}' --max-time 10 "$@"; }

echo "== 1. todos os serviços healthy e os one-shots com exit 0"
while read -r servico estado; do
  case "$estado" in
    *healthy*|*"Exited (0)"*) ok "$servico" ;;
    *) falha "$servico: $estado" ;;
  esac
done < <("${COMPOSE[@]}" ps -a --format '{{.Service}} {{.Status}}' | grep -v '^sanitizer ')

echo "== 2. SPA e stream"
esperar "GET / (SPA)" 200 "$(status "$BASE/")"
cabecalhos="$(curl -s -I --max-time 10 "$BASE/stream/radio.mp3" | tr -d '\r')"
esperar "HEAD /stream/radio.mp3" "HTTP/1.1 200 OK" "$(head -1 <<<"$cabecalhos")"
grep -qi '^content-type: audio/mpeg' <<<"$cabecalhos" && ok "content-type audio/mpeg" || falha "content-type do stream"
curl -s --max-time 4 "$BASE/stream/radio.mp3" -o "$TMP/amostra.mp3" || true
[[ "$(stat -c %s "$TMP/amostra.mp3")" -gt 16000 ]] && ok "stream entrega áudio" || falha "stream sem dados"
esperar "S-A07 /stream/admin/" 404 "$(status "$BASE/stream/admin/")"
esperar "S-A07 PUT /stream/radio.mp3" 403 "$(status -X PUT "$BASE/stream/radio.mp3")"

echo "== 3. login do Admin semeado, troca de senha obrigatória, /auth/me"
corpo="$(python3 -c 'import json,sys; print(json.dumps({"email": open(sys.argv[1]).read(), "senha": open(sys.argv[2]).read()}))' secrets/seed_admin_email secrets/seed_admin_senha)"
# O corpo (com a senha do Admin) vai por stdin: no argv ele apareceria no `ps` numa máquina compartilhada.
resp="$(curl -s --max-time 10 -c "$TMP/cookies" -H 'Content-Type: application/json' --data-binary @- "$BASE/api/v1/auth/login" <<<"$corpo")"
token="$(python3 -c 'import json,sys; print(json.loads(sys.stdin.read()).get("accessToken",""))' <<<"$resp" 2>/dev/null || true)"
if [[ -n "$token" ]]; then
  [[ "${GITHUB_ACTIONS:-}" == "true" ]] && echo "::add-mask::$token"
  ok "login do Admin"
  deve="$(python3 -c 'import json,sys; print(json.loads(sys.stdin.read())["deveTrocarSenha"])' <<<"$resp")"
  esperar "deveTrocarSenha no primeiro login" True "$deve"
  esperar "/auth/me com token" 200 "$(status -H "Authorization: Bearer $token" "$BASE/api/v1/auth/me")"
  esperar "rota qualquer com token restrito" 403 "$(status -H "Authorization: Bearer $token" "$BASE/api/v1/stream/qualquer")"
  esperar "refresh sem Origin" 403 "$(status -b "$TMP/cookies" -X POST "$BASE/api/v1/auth/refresh")"
  esperar "refresh com Origin de outra origem" 403 "$(status -b "$TMP/cookies" -H 'Origin: https://evil.example' -X POST "$BASE/api/v1/auth/refresh")"
  esperar "refresh da mesma origem" 200 "$(status -b "$TMP/cookies" -H "Origin: $ORIGEM" -X POST "$BASE/api/v1/auth/refresh")"
else
  falha "login do Admin (sem accessToken; o Admin já trocou a senha? use volumes limpos)"
fi
esperar "/auth/me sem token" 401 "$(status "$BASE/api/v1/auth/me")"

echo "== 4. health só no :8081 e bypass do S-A06 pela :8080"
ready="$("${COMPOSE[@]}" exec -T api wget -qO- http://127.0.0.1:8081/health/ready || true)"
[[ "$ready" == *'"status":"Healthy"'* ]] && ok "/health/ready interno: Healthy" || falha "/health/ready interno: $ready"
for u in /api/v1/internal/playout/proxima /api/v1/INTERNAL/playout/proxima /api/v1//internal/playout/proxima \
         /api/v1/%69nternal/playout/proxima /api/v1/internal /health/ready /health/live /HEALTH/live; do
  esperar "pela 8080 $u" 404 "$(status -H "Authorization: Bearer x" "$BASE$u")"
done
esperar "Host: localhost:8081 na 8080" 404 "$(status -H 'Host: localhost:8081' "$BASE/api/v1/internal/playout/proxima")"

echo "== F16: isolamento de rede"
alcanca() { "${COMPOSE[@]}" exec -T "$1" bash -c "timeout 3 bash -c 'exec 3<>/dev/tcp/$2/$3'" >/dev/null 2>&1 && echo sim || echo nao; }
esperar "liquidsoap → sqlserver:1433" nao "$(alcanca liquidsoap sqlserver 1433)"
esperar "liquidsoap → redis:6379" nao "$(alcanca liquidsoap redis 6379)"
esperar "liquidsoap → api:8080" nao "$(alcanca liquidsoap api 8080)"
esperar "liquidsoap → api:8081" sim "$(alcanca liquidsoap api 8081)"
web_alcanca() { "${COMPOSE[@]}" exec -T web sh -c "nc -z -w 3 $1 $2" >/dev/null 2>&1 && echo sim || echo nao; }
# Controle positivo: sem ele um `nc` ausente na imagem daria "nao" nos dois testes e o isolamento passaria sem ser provado.
esperar "web → api:8080 (controle positivo do nc)" sim "$(web_alcanca api 8080)"
esperar "web → api:8081 (S-M15)" nao "$(web_alcanca api 8081)"

echo "== 8. portas publicadas"
mapeamentos="$(docker ps --filter label=com.docker.compose.project=webradio --format '{{.Ports}}' | tr ',' '\n' | grep -- '->' | sed 's/^ *//' | sort -u)"
# IPv4 exato; IPv6 só pode ser o [::]:8080 que o Docker acrescenta à porta pública (ele pode ou não aparecer, conforme o host).
publicadas_v4="$(grep -oE '^(0\.0\.0\.0|127\.0\.0\.1):[0-9]+' <<<"$mapeamentos" | sort -u | tr '\n' ' ')"
publicadas_v6="$(grep -oE '^\[::\]:[0-9]+' <<<"$mapeamentos" | sort -u | tr '\n' ' ')"
esperar "só 8080 pública em 0.0.0.0 (+ 127.0.0.1 no dev)" "0.0.0.0:8080 127.0.0.1:1433 127.0.0.1:8005 127.0.0.1:9001 " "$publicadas_v4"
[[ -z "$publicadas_v6" || "$publicadas_v6" == "[::]:8080 " ]] && ok "IPv6 publicado: só o [::]:8080 (ou nenhum)" || falha "IPv6 publicado inesperado: $publicadas_v6"
# Qualquer mapeamento que não comece por um desses endereços (ex.: um IP de LAN) seria ignorado pelos greps acima.
estranhos="$(grep -vE '^(0\.0\.0\.0|127\.0\.0\.1|\[::\]):[0-9]+->' <<<"$mapeamentos" || true)"
[[ -z "$estranhos" ]] && ok "nenhuma porta publicada em outro endereço" || falha "porta publicada em endereço inesperado: $estranhos"

echo "== 7. docker compose config sem segredo literal"
config="$("${COMPOSE[@]}" config)"
vazou=0
for f in secrets/*; do
  v="$(cat "$f")"
  if [[ ${#v} -ge 16 ]] && grep -qF -- "$v" <<<"$config"; then vazou=1; falha "segredo de $(basename "$f") aparece no compose config"; fi
done
[[ $vazou -eq 0 ]] && ok "nenhum segredo no compose config"

echo "== base de PRODUÇÃO (docker-compose.yml sozinho): redes, portas, capabilities, logs, ambiente e segredos"
# O stack acima é o do dev; aqui a base é conferida sem o override (B3-08, B3-02/03/04/11). O JSON fica em arquivo: nunca é impresso.
docker compose -f docker-compose.yml --profile '*' config --format json > "$TMP/prod.json"
docker compose -f docker-compose.yml -f docker-compose.dev.yml --profile '*' config --format json > "$TMP/dev.json"
topologia="$(python3 -I - "$TMP/prod.json" "$TMP/dev.json" <<'PY'
import json, re, sys

prod = json.load(open(sys.argv[1]))
dev = json.load(open(sys.argv[2]))
saida = []

def confere(descricao, condicao, detalhe=""):
    saida.append(("ok" if condicao else "falha") + "|" + descricao + ("" if condicao or not detalhe else f": {detalhe}"))

servicos = prod["services"]
def redes(nome):
    return set((servicos[nome].get("networks") or {}).keys())

# --- redes e portas (04 §Redes, S-M15)
confere("redes da base: exatamente borda, app, dados, playout, midia-borda (nenhuma diag-*)",
        set(prod["networks"]) == {"borda", "app", "dados", "playout", "midia-borda"}, str(sorted(prod["networks"])))
publicadas = {n: [(p.get("published"), p.get("target"), p.get("host_ip")) for p in s.get("ports", [])] for n, s in servicos.items() if s.get("ports")}
confere("só o web publica porta, e só a 8080", publicadas == {"web": [("8080", 8080, None)]}, str(publicadas))
confere("web nas redes borda, app e midia-borda, e NÃO na playout", redes("web") == {"borda", "app", "midia-borda"}, str(sorted(redes("web"))))
confere("liquidsoap só na playout", redes("liquidsoap") == {"playout"}, str(sorted(redes("liquidsoap"))))
confere("api em app, dados e playout", redes("api") == {"app", "dados", "playout"}, str(sorted(redes("api"))))
confere("worker só na dados", redes("worker") == {"dados"}, str(sorted(redes("worker"))))
confere("sanitizer sem rede (network_mode none)", servicos["sanitizer"].get("network_mode") == "none")
for n in ("app", "dados", "playout", "midia-borda"):
    confere(f"rede {n} é internal", bool(prod["networks"][n].get("internal")))

# --- gatilho (ii) do risco aceito do TrustServerCertificate (B3-03): nenhum contêiner da dados com capability de rede
perigosas = {"NET_RAW", "NET_ADMIN", "ALL", "SYS_ADMIN"}
for nome, s in sorted(servicos.items()):
    if "dados" not in redes(nome):
        continue
    adicionadas = set(s.get("cap_add") or [])
    confere(f"{nome} (rede dados): sem NET_RAW/NET_ADMIN, sem network_mode host, cap_drop ALL",
            not (adicionadas & perigosas) and s.get("network_mode") != "host" and "ALL" in (s.get("cap_drop") or []),
            f"cap_add={sorted(adicionadas)} network_mode={s.get('network_mode')}")

# --- TrustServerCertificate com o MESMO valor em api, worker e migrator; Encrypt=True fixo (B3-03)
def valor(cs, chave):
    m = re.search(rf"(?:^|;)\s*{re.escape(chave)}\s*=\s*([^;]*)", cs, re.I)
    return m.group(1).strip().lower() if m else None
api_cs = servicos["api"]["environment"]["ConnectionStrings__RadioDb"]
worker_cs = servicos["worker"]["environment"]["ConnectionStrings__RadioDb"]
migrator = (servicos["migrator"]["environment"].get("DB_TRUST_SERVER_CERTIFICATE") or "").lower()
confere("TrustServerCertificate igual em api, worker e migrator",
        valor(api_cs, "TrustServerCertificate") == valor(worker_cs, "TrustServerCertificate") == migrator != "",
        f"api={valor(api_cs, 'TrustServerCertificate')} worker={valor(worker_cs, 'TrustServerCertificate')} migrator={migrator}")
confere("Encrypt=True fixo na api e no worker", valor(api_cs, "Encrypt") == "true" and valor(worker_cs, "Encrypt") == "true")

# --- B3-02: rotação de log em todo serviço
for nome, s in sorted(servicos.items()):
    opcoes = (s.get("logging") or {}).get("options") or {}
    confere(f"{nome}: logging com max-size e max-file", "max-size" in opcoes and "max-file" in opcoes)

# --- B3-04: o Worker é host genérico e lê DOTNET_ENVIRONMENT
confere("worker: DOTNET_ENVIRONMENT=Production na base", servicos["worker"]["environment"].get("DOTNET_ENVIRONMENT") == "Production")
confere("worker: DOTNET_ENVIRONMENT=Development com o dev.yml", dev["services"]["worker"]["environment"].get("DOTNET_ENVIRONMENT") == "Development")
confere("api: ASPNETCORE_ENVIRONMENT=Production na base e Development com o dev.yml",
        servicos["api"]["environment"].get("ASPNETCORE_ENVIRONMENT") == "Production"
        and dev["services"]["api"]["environment"].get("ASPNETCORE_ENVIRONMENT") == "Development")

# --- B3-11: o Worker só recebe o segredo que o código lê
alvos = sorted(x["target"] for x in servicos["worker"].get("secrets", []))
confere("worker: só o segredo Banco__Senha", alvos == ["Banco__Senha"], str(alvos))
confere("worker: depende só do migrator", set(servicos["worker"].get("depends_on", {})) == {"migrator"})

print("\n".join(saida))
PY
)" || { falha "a checagem da base de produção não rodou (python3)"; topologia=""; }
while IFS='|' read -r resultado texto; do
  [[ -z "$resultado" ]] && continue
  [[ "$resultado" == "ok" ]] && ok "$texto" || falha "$texto"
done <<<"$topologia"

echo "== sessões no SQL Server (Application Name por serviço, B3-03) e ambiente do Worker no log (B3-04)"
consulta="SET NOCOUNT ON; SELECT DISTINCT program_name FROM sys.dm_exec_sessions WHERE program_name LIKE N'WebRadio.%' OR program_name COLLATE Latin1_General_BIN LIKE N'webradio%'"
sessoes="$("${COMPOSE[@]}" exec -T -e CONSULTA="$consulta" sqlserver bash -c 'SQLCMDPASSWORD="$(cat /run/secrets/mssql_sa_password)" /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -b -h -1 -W -Q "$CONSULTA"' || true)"
grep -qx 'WebRadio.Api' <<<"$sessoes" && ok "sessão WebRadio.Api" || falha "nenhuma sessão com Application Name WebRadio.Api"
grep -qx 'WebRadio.Worker.Lideranca' <<<"$sessoes" && ok "sessão WebRadio.Worker.Lideranca (conexão do líder)" || falha "nenhuma sessão WebRadio.Worker.Lideranca"
if grep -q '^webradio' <<<"$sessoes"; then falha "sessão com o Application Name antigo (webradio…): $(grep '^webradio' <<<"$sessoes" | tr '\n' ' ')"; else ok "nenhum Application Name antigo (webradio, webradio-worker-leader)"; fi
logs_worker="$("${COMPOSE[@]}" logs worker 2>&1 || true)" # em variável: `logs | grep -q` com pipefail daria SIGPIPE
grep -q 'Hosting environment: Development' <<<"$logs_worker" && ok "Worker sobe como Development com o dev.yml" || falha "o log do Worker não mostra 'Hosting environment: Development'"

echo
if [[ $falhas -gt 0 ]]; then echo "smoke: $falhas falha(s)."; exit 1; fi
echo "smoke: tudo ok."
