#!/usr/bin/env bash
# =============================================================================
# WebRadio — infra/scripts/limpar-logs.sh (S-B04, B3-02; docs/operacao-logs.md)
#
# Retenção de 14 dias dos logs que podem ter IP: o compose só rotaciona por TAMANHO (json-file 10 MB × 3), então num dia
# calmo as linhas ficam no disco por mais de 14 dias. Este script, rodado semanalmente no host (systemd.timer do doc), zera
# o log CORRENTE e apaga os ROTACIONADOS (<id>-json.log.1, .2) do `web` (nginx: access log com IP anonimizado, mas o
# error_log em warn grava o IP completo a cada 429) e do `icecast` (error log). Nada derruba o contêiner nem o stream:
# o daemon mantém o corrente aberto (por isso truncar, não apagar) e não mantém os rotacionados.
#
# Precisa de root: os arquivos ficam em /var/lib/docker/containers.
#
#   sudo infra/scripts/limpar-logs.sh             # limpa de verdade
#   infra/scripts/limpar-logs.sh --simular        # só mostra o que faria (não precisa de root)
#
# Variáveis: LIMPAR_LOGS_SERVICOS (padrão "web icecast"), COMPOSE_PROJECT (padrão "webradio"),
#            LIMPAR_LOGS_ARQUIVOS (lista de LogPath separada por espaço, para teste: pula o docker).
# =============================================================================
set -euo pipefail
export LC_ALL=C

simular=0
[[ "${1:-}" == "--simular" ]] && simular=1

servicos="${LIMPAR_LOGS_SERVICOS:-web icecast}"
projeto="${COMPOSE_PROJECT:-webradio}"

if [[ $simular -eq 0 && "${LIMPAR_LOGS_ARQUIVOS:-}" == "" && "$(id -u)" -ne 0 ]]; then
  echo "limpar-logs: rode como root (os logs ficam em /var/lib/docker/containers) ou use --simular." >&2
  exit 1
fi

arquivos=()
faltou=0   # algum serviço pedido sem contêiner: o script limpa os outros, mas sai com 1 no fim (retenção não cumprida para aquele)
if [[ -n "${LIMPAR_LOGS_ARQUIVOS:-}" ]]; then
  read -r -a arquivos <<<"$LIMPAR_LOGS_ARQUIVOS"
else
  for servico in $servicos; do
    ids="$(docker ps -aq --filter "label=com.docker.compose.project=${projeto}" --filter "label=com.docker.compose.service=${servico}")"
    if [[ -z "$ids" ]]; then
      echo "limpar-logs: nenhum contêiner do serviço '${servico}' no projeto '${projeto}'." >&2
      faltou=1
      continue
    fi
    for id in $ids; do
      caminho="$(docker inspect --format '{{.LogPath}}' "$id")"
      [[ -n "$caminho" ]] && arquivos+=("$caminho")
    done
  done
fi

if [[ ${#arquivos[@]} -eq 0 ]]; then
  echo "limpar-logs: nada a limpar (nenhum log encontrado)." >&2
  exit 1
fi

for arquivo in "${arquivos[@]}"; do
  rotacionados=()
  for r in "$arquivo".[0-9]*; do [[ -e "$r" ]] && rotacionados+=("$r"); done
  if [[ $simular -eq 1 ]]; then
    echo "simular: zeraria $arquivo e apagaria ${#rotacionados[@]} rotacionado(s)"
    continue
  fi
  : > "$arquivo"                       # o corrente: truncado (o daemon o mantém aberto)
  [[ ${#rotacionados[@]} -gt 0 ]] && rm -f -- "${rotacionados[@]}"
  echo "limpar-logs: $arquivo zerado; ${#rotacionados[@]} rotacionado(s) apagado(s)."
done

if [[ $faltou -ne 0 ]]; then
  echo "limpar-logs: ao menos um serviço pedido não foi encontrado (COMPOSE_PROJECT='${projeto}'): a retenção dele NÃO foi cumprida." >&2
  exit 1
fi
