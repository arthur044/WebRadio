#!/bin/sh
# WebRadio — infra/redis/entrypoint.sh (E1-F16, 05 §5.3): gera o redis.conf com requirepass no tmpfs a partir
# de /run/secrets/redis_password. A senha não aparece em argv, no compose nem no `docker inspect`.
set -eu
ARQ=/run/secrets/redis_password
[ -r "$ARQ" ] || { echo "redis: não consegui ler $ARQ." >&2; exit 1; }
SENHA="$(cat "$ARQ")"
[ "${#SENHA}" -ge 24 ] || { echo "redis: senha curta demais." >&2; exit 1; }
case "$SENHA" in *[!A-Za-z0-9_-]*) echo "redis: senha fora de [A-Za-z0-9_-]." >&2; exit 1 ;; esac

umask 077
cat > /tmp/redis.conf <<CONF
requirepass $SENHA
bind 0.0.0.0
protected-mode yes
dir /data
appendonly yes
maxmemory 96mb
maxmemory-policy volatile-lru
rename-command FLUSHALL ""
rename-command FLUSHDB ""
rename-command CONFIG ""
rename-command DEBUG ""
CONF
unset SENHA
exec redis-server /tmp/redis.conf
