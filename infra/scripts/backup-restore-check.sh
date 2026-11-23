#!/usr/bin/env bash
# =============================================================================
# WebRadio — infra/scripts/backup-restore-check.sh (E5-F03, 09-epico-5-tarefas.md: critério "Com teste (dados)", DoD 4 e 5 do Épico 5)
#
# O cenário que importa: o servidor MORRE e a rádio volta num servidor NOVO (SQL Server e MinIO novos, volumes novos, logins recriados com SID novo), a partir SÓ do
# storage externo e dos segredos. Usa os scripts de verdade (infra/backup/backup.sh e restaurar.sh) dentro da imagem de verdade (infra/backup/Dockerfile), o minio-init de
# verdade (identidade svc-backup e políticas) e o SQL Server real; um segundo MinIO faz o papel do storage externo S3.
#
#   A (origem)      : SQL Server + MinIO, dados de teste (banco com linhas, objetos em midia, publico e QUARENTENA)
#   backups         : falha simulada em CADA etapa (sql, midia, envio, retencao, check): BACKUP_FALHOU etapa=..., código diferente de 0, sem saída crua no log e sem rodar as
#                     etapas seguintes; depois o backup só local (BACKUP_EXTERNO_NAO_CONFIGURADO é aviso, código 0) e o backup com o externo ligado
#   B (servidor novo): volumes novos; db-init (logins com SID novo); a trava do restaurar.sh; restaurar.sh a partir do EXTERNO (o repositório local não existe)
#   confere         : usuários religados (a API abre o banco), contagem de linhas igual em TODAS as tabelas, objetos de midia e publico com o mesmo SHA-256, a quarentena NÃO
#                     foi para o backup, uma URL pré-assinada devolve os bytes certos (a "pré-escuta") e o svc-backup do MinIO não escreve nem lê a quarentena
#
# Precisa de docker (BuildKit). Cria redes, contêineres e volumes com o sufixo do PID e apaga tudo no fim. Senhas descartáveis. ~10 min (compila o mc na 1ª vez).
# Entrada (opcional): MSSQL_IMAGEM, MINIO_IMAGEM (padrão: os digests do docker-compose.yml), MSSQL_PID (Express), BRC_IMAGEM_BACKUP / BRC_IMAGEM_INIT (imagens já construídas).
# =============================================================================
set -euo pipefail
export LC_ALL=C

RAIZ="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$RAIZ"
IMG_SQL="${MSSQL_IMAGEM:-mcr.microsoft.com/mssql/server@sha256:4525e3efac9f3e241cb003bb2ff2a82818b502f37e12049d788b1a9fa7e13b7f}"
IMG_MINIO="${MINIO_IMAGEM:-cgr.dev/chainguard/minio@sha256:a1ac4589a5f8136b616e667b48a0bea9719bb10bd45799ea946f24bbedfafde4}"
PID_SQL="${MSSQL_PID:-Express}"
SUF="$$"
IMG_INIT="${BRC_IMAGEM_INIT:-brc-minio-init:$SUF}"
IMG_BKP="${BRC_IMAGEM_BACKUP:-brc-backup:$SUF}"
NET="brc-net-$SUF"
TMP="$(mktemp -d)"
chmod 755 "$TMP"
SEC="$TMP/segredos"; mkdir -p "$SEC"; chmod 755 "$SEC"
ARQ="$TMP/arq"; mkdir -p "$ARQ"; chmod 755 "$ARQ"
falhas=0

ok() { echo "  ok    $1"; }
falha() { echo "  FALHA $1" >&2; [[ -n "${2:-}" ]] && echo "$2" | sed 's/^/        /' >&2; falhas=$((falhas + 1)); }
confere() { local d="$1"; shift; if "$@" >/dev/null 2>&1; then ok "$d"; else falha "$d"; fi; }

limpar() {
  docker rm -f "brc-sql-a-$SUF" "brc-sql-b-$SUF" "brc-minio-a-$SUF" "brc-minio-b-$SUF" "brc-ext-$SUF" >/dev/null 2>&1 || true
  docker volume rm -f "brc-bak-a-$SUF" "brc-bak-b-$SUF" "brc-data-a-$SUF" "brc-data-b-$SUF" "brc-ext-data-$SUF" "brc-local-a-$SUF" "brc-local-b-$SUF" >/dev/null 2>&1 || true
  docker network rm "$NET" >/dev/null 2>&1 || true
  [[ -z "${BRC_IMAGEM_BACKUP:-}" ]] && docker rmi -f "$IMG_BKP" >/dev/null 2>&1 || true
  [[ -z "${BRC_IMAGEM_INIT:-}" ]] && docker rmi -f "$IMG_INIT" >/dev/null 2>&1 || true
  rm -rf "$TMP"
}
trap limpar EXIT

# ------------------------------------------------------------------ segredos descartáveis (arquivos, como no deploy)
seg() { printf '%s' "$2" > "$SEC/$1"; chmod 644 "$SEC/$1"; }
forte() { echo "Wr$(openssl rand -hex "$1")A1"; }
SA="$(forte 12)"
seg mssql_sa_password "$SA"
seg db_app_password "App$(openssl rand -hex 8)A1"
seg db_migrator_password "Mig$(openssl rand -hex 8)A1"
seg db_relatorio_password "Rel$(openssl rand -hex 8)A1"
seg sql_backup_senha "Bkp$(openssl rand -hex 8)A1"
seg restic_senha "$(openssl rand -hex 24)"
seg minio_root_user "$(openssl rand -hex 8)"
seg minio_root_password "$(openssl rand -hex 16)"
for n in api worker backup; do seg "minio_${n}_access_key" "$(openssl rand -hex 10)"; seg "minio_${n}_secret_key" "$(openssl rand -hex 15)"; done
EXT_USER="ext$(openssl rand -hex 6)"; EXT_PASS="$(openssl rand -hex 16)"
seg backup_s3_access_key ""   # nascem VAZIOS (o gerar-segredos): cópia externa desligada
seg backup_s3_secret_key ""
ROOT_USER="$(cat "$SEC/minio_root_user")"; ROOT_PASS="$(cat "$SEC/minio_root_password")"

# ------------------------------------------------------------------ imagens e rede
echo "== 0. imagens (minio-init para o mc e o init de verdade; backup) e rede"
if [[ -z "${BRC_IMAGEM_INIT:-}" ]]; then docker build -q -f infra/minio/Dockerfile -t "$IMG_INIT" . >/dev/null; fi
if [[ -z "${BRC_IMAGEM_BACKUP:-}" ]]; then docker build -q -f infra/backup/Dockerfile --build-context "mc-img=docker-image://$IMG_INIT" -t "$IMG_BKP" . >/dev/null; fi
docker network create "$NET" >/dev/null
confere "imagem backup tem restic, sqlcmd e mc" docker run --rm --entrypoint sh "$IMG_BKP" -c 'restic version && sqlcmd --version && mc --version'

# ------------------------------------------------------------------ helpers
SQLCMD=/opt/mssql-tools18/bin/sqlcmd
sa_sql() { local c="$1"; shift
  docker exec -i -e SQLCMDPASSWORD="$SA" -e DB_APP_PASSWORD="$(cat "$SEC/db_app_password")" -e DB_MIGRATOR_PASSWORD="$(cat "$SEC/db_migrator_password")" \
    -e DB_RELATORIO_PASSWORD="$(cat "$SEC/db_relatorio_password")" -e DB_BACKUP_PASSWORD="$(cat "$SEC/sql_backup_senha")" "$c" "$SQLCMD" -C -N -S localhost -U sa -b -I "$@"; }
como() { local c="$1" u="$2" p="$3"; shift 3; docker exec -i -e SQLCMDPASSWORD="$p" "$c" "$SQLCMD" -C -N -S localhost -U "$u" -b -I "$@"; }
subir_sql() { # $1 contêiner, $2 volume do .bak
  docker volume create "$2" >/dev/null
  docker run --rm --user 0:0 --cap-drop ALL --cap-add CHOWN --security-opt no-new-privileges --network none --read-only -v "$2:/var/opt/mssql/backup" --entrypoint chown "$IMG_SQL" 10001:0 /var/opt/mssql/backup
  docker run -d --name "$1" --network "$NET" --network-alias "${1%-$SUF}" -e ACCEPT_EULA=Y -e MSSQL_PID="$PID_SQL" -e MSSQL_SA_PASSWORD="$SA" -e MSSQL_MEMORY_LIMIT_MB=1400 -v "$2:/var/opt/mssql/backup" "$IMG_SQL" >/dev/null
  for _ in $(seq 1 60); do sa_sql "$1" -Q "SELECT 1" >/dev/null 2>&1 && return 0; sleep 2; done
  echo "FALHA: o SQL Server $1 não subiu." >&2; docker logs --tail 20 "$1" >&2; exit 1
}
db_init() { # o que o db-init faz num servidor: init-db, logins, logins-backup
  sa_sql "$1" -v Ambiente=Production -i /dev/stdin < infra/sql/init-db.sql >/dev/null && sa_sql "$1" -i /dev/stdin < infra/sql/logins.sql >/dev/null && sa_sql "$1" -i /dev/stdin < infra/sql/logins-backup.sql >/dev/null
}
subir_minio() { # $1 contêiner, $2 volume de dados
  docker volume create "$2" >/dev/null
  docker run -d --name "$1" --network "$NET" --network-alias "${1%-$SUF}" -e MINIO_ROOT_USER="$ROOT_USER" -e MINIO_ROOT_PASSWORD="$ROOT_PASS" -e MINIO_BROWSER=off -v "$2:/data" "$IMG_MINIO" server /data >/dev/null
  for _ in $(seq 1 30); do mc_raiz "${1%-$SUF}" ready local >/dev/null 2>&1 && return 0; sleep 2; done
  echo "FALHA: o MinIO $1 não subiu." >&2; docker logs --tail 20 "$1" >&2; exit 1
}
# mc como o uid 10001, com a credencial raiz do MinIO $1 (alias `x`). Argumentos: o resto.
mc_raiz() { local h="$1"; shift
  docker run --rm -i --network "$NET" --user 10001:0 -e HOME=/tmp -e MC_CONFIG_DIR=/tmp/.mc -e "MC_HOST_x=http://$ROOT_USER:$ROOT_PASS@$h:9000" -e "MC_HOST_local=http://$h:9000" -v "$ARQ:/arq" --entrypoint mc "$IMG_BKP" "$@"; }
minio_init() { # o minio-init de verdade, com os mesmos segredos do deploy
  docker run --rm --network "$NET" --read-only --cap-drop ALL --security-opt no-new-privileges --tmpfs /tmp -e "MINIO_ENDPOINT=$1:9000" -v "$SEC:/run/secrets:ro" "$IMG_INIT"
}
contagens() { # contagem EXATA de linhas de todas as tabelas, uma por linha: "schema.tabela|n"
  sa_sql "$1" -d WebRadio -h -1 -W -s '|' -Q "SET NOCOUNT ON;
    DECLARE @q nvarchar(max) = (SELECT STRING_AGG(CAST(N'SELECT N''' + s.name + N'.' + t.name + N''' AS t, COUNT_BIG(*) AS n FROM ' + QUOTENAME(s.name) + N'.' + QUOTENAME(t.name) AS nvarchar(max)), N' UNION ALL ')
                                FROM sys.tables AS t JOIN sys.schemas AS s ON s.schema_id = t.schema_id);
    EXEC (N'SELECT t, n FROM (' + @q + N') AS x ORDER BY t');" | grep -v '^$' | sort
}
# O go-sqlcmd da imagem do backup, como sa (o MESMO binário do restaurar.sh), contra o SQL Server $1: devolve o código de saída e guarda a saída em OUT.
sa_gosql() { local host="$1"; shift
  set +e
  OUT="$(docker run --rm --network "$NET" --user 10001:0 -e SQLCMDPASSWORD="$SA" -v "$RAIZ/infra/sql:/sql:ro" --entrypoint sqlcmd "$IMG_BKP" -b -C -N -I -S "$host" -U sa -l 30 "$@" 2>&1)"
  RC=$?
  set -e
}
# O backup.sh / restaurar.sh de verdade, na imagem de verdade e com as restrições do compose (uid 10001, sem capability, rootfs só leitura, sem segredo em argv).
OUT=""; RC=0
rodar_na_imagem() { # $1 entrypoint (backup.sh | restaurar.sh), $2 diretório de segredos, $3 volume do .bak, $4 modo do .bak (ro|rw), $5 volume local, $6 SQL host, $7 MinIO host, resto = -e do docker
  local ep="$1" sec="$2" bak="$3" modo="$4" local_="$5" sql="$6" minio="$7"; shift 7
  set +e
  OUT="$(docker run --rm --network "$NET" --user 10001:0 --read-only --cap-drop ALL --security-opt no-new-privileges --tmpfs /tmp:uid=10001,gid=0,mode=0700 \
    -v "$sec:/run/secrets:ro" -v "$bak:/var/opt/mssql/backup:$modo" -v "$local_:/backup" -v "$RAIZ/infra/sql:/sql:ro" \
    -e "DB_SERVER=$sql" -e "MINIO_ENDPOINT=$minio:9000" --entrypoint "/usr/local/bin/$ep" "$@" "$IMG_BKP" 2>&1)"
  RC=$?
  set -e
}
# Nenhum segredo, host ou caminho cru no log (E5, cobrança do Sentinel): só o token de motivo.
log_limpo() { # $1 descrição
  local vazou=0 v
  for f in sql_backup_senha restic_senha minio_backup_secret_key minio_backup_access_key; do v="$(cat "$SEC/$f")"; [[ -n "$v" ]] && grep -qF -- "$v" <<<"$OUT" && vazou=1; done
  grep -qiE 'brc-(sql|minio|ext)|\.mc|dial tcp|stack|goroutine' <<<"$OUT" && vazou=1
  [[ "$vazou" == 0 ]] && ok "$1: o log não traz segredo, host nem saída crua" || falha "$1: o log tem segredo, host ou saída crua" "$OUT"
}

# ================================================================== A: a origem
echo "== 1. servidor A (origem): SQL Server, MinIO (com o minio-init de verdade) e o 'storage externo' (outro MinIO)"
docker volume create "brc-local-a-$SUF" >/dev/null
subir_sql "brc-sql-a-$SUF" "brc-bak-a-$SUF"
db_init "brc-sql-a-$SUF" && ok "db-init no A (init-db, logins, logins-backup: svc_backup criado)" || { falha "db-init no A falhou"; exit 1; }
sa_sql "brc-sql-a-$SUF" -d WebRadio -i /dev/stdin < docs/specs/03-schema-sqlserver.sql >/dev/null
sa_sql "brc-sql-a-$SUF" -d WebRadio -Q "
SET NOCOUNT ON;
DECLARE @u uniqueidentifier = NEWID();
INSERT seg.Usuario (Id, Nome, Email, EmailNormalizado, SenhaHash, Role) VALUES (@u, N'Dono', N'dono@teste.local', N'dono@teste.local', N'hash', 3);
INSERT midia.ArquivoMidia (NomeOriginal, TipoMidia, Bucket, ChaveStorage, MimeTypeDeclarado, TamanhoBytes, EnviadoPorUsuarioId, UploadExpiraEmUtc)
  SELECT N'faixa' + CONVERT(nvarchar(10), n) + N'.mp3', 1, 'midia', '2026/m' + CONVERT(varchar(10), n) + '.bin', 'audio/mpeg', 1000 + n, @u, SYSUTCDATETIME()
  FROM (SELECT TOP (40) ROW_NUMBER() OVER (ORDER BY (SELECT 1)) AS n FROM sys.all_objects) AS x;
INSERT infra.EventoOutbox (Tipo, Payload) SELECT TOP (25) 'TesteBackup', N'{}' FROM sys.all_objects;" >/dev/null
contagens "brc-sql-a-$SUF" > "$TMP/contagem-a.txt"
grep -q '^midia.ArquivoMidia|40$' "$TMP/contagem-a.txt" && ok "dados no banco A ($(grep -c . "$TMP/contagem-a.txt") tabelas contadas)" || falha "semente do banco A" "$(cat "$TMP/contagem-a.txt")"

subir_minio "brc-minio-a-$SUF" "brc-data-a-$SUF"
minio_init "brc-minio-a" >/dev/null && ok "minio-init de verdade no A (buckets, svc-api, svc-worker e svc-backup)" || falha "minio-init no A falhou"
# objetos: 6 em midia (um com subpasta), 2 em publico e 1 na QUARENTENA (que NÃO pode ir para o backup)
for i in 1 2 3 4 5 6; do head -c $((20000 + i * 1111)) /dev/urandom > "$ARQ/m$i.bin"; done
head -c 5000 /dev/urandom > "$ARQ/img1.webp"; head -c 6000 /dev/urandom > "$ARQ/img2.webp"; head -c 3000 /dev/urandom > "$ARQ/hostil.bin"
MARCADOR="TEXTO-EM-CLARO-$(openssl rand -hex 12)"; printf '%s' "$MARCADOR" > "$ARQ/marcador.txt"
chmod 644 "$ARQ"/*
for i in 1 2 3 4 5 6; do mc_raiz brc-minio-a cp "/arq/m$i.bin" "x/midia/2026/m$i.bin" >/dev/null; done
mc_raiz brc-minio-a cp /arq/img1.webp x/publico/divulgacoes/img1.webp >/dev/null
mc_raiz brc-minio-a cp /arq/img2.webp x/publico/divulgacoes/img2.webp >/dev/null
mc_raiz brc-minio-a cp /arq/marcador.txt x/publico/divulgacoes/marcador.txt >/dev/null
mc_raiz brc-minio-a cp /arq/hostil.bin x/quarentena/2026/hostil.bin >/dev/null
[[ "$(mc_raiz brc-minio-a ls --recursive x/midia | wc -l)" -eq 6 ]] && ok "objetos de teste no A (6 em midia, 3 em publico, 1 na quarentena)" || falha "objetos de teste no A"

# o "storage externo": outro MinIO, só com o bucket do backup
docker volume create "brc-ext-data-$SUF" >/dev/null
docker run -d --name "brc-ext-$SUF" --network "$NET" --network-alias brc-ext -e MINIO_ROOT_USER="$EXT_USER" -e MINIO_ROOT_PASSWORD="$EXT_PASS" -e MINIO_BROWSER=off -v "brc-ext-data-$SUF:/data" "$IMG_MINIO" server /data >/dev/null
for _ in $(seq 1 30); do docker run --rm --network "$NET" --user 10001:0 -e HOME=/tmp -e "MC_HOST_local=http://brc-ext:9000" --entrypoint mc "$IMG_BKP" ready local >/dev/null 2>&1 && break; sleep 2; done
docker run --rm --network "$NET" --user 10001:0 -e HOME=/tmp -e "MC_HOST_e=http://$EXT_USER:$EXT_PASS@brc-ext:9000" --entrypoint mc "$IMG_BKP" mb e/backup-ext >/dev/null && ok "bucket externo criado (o MinIO do 'storage externo')"

# a identidade do backup: o que ela pode e o que não pode no MinIO
BKP_K="$(cat "$SEC/minio_backup_access_key")"; BKP_S="$(cat "$SEC/minio_backup_secret_key")"
mc_bkp() { docker run --rm -i --network "$NET" --user 10001:0 -e HOME=/tmp -e "MC_HOST_b=http://$BKP_K:$BKP_S@brc-minio-a:9000" -v "$ARQ:/arq" --entrypoint mc "$IMG_BKP" "$@"; }
confere "svc-backup lê midia" mc_bkp cat b/midia/2026/m1.bin
confere "svc-backup lê publico" mc_bkp cat b/publico/divulgacoes/img1.webp
confere "svc-backup NÃO lê a quarentena" bash -c "! mc_bkp cat b/quarentena/2026/hostil.bin"
confere "svc-backup NÃO escreve em midia" bash -c "! mc_bkp cp /arq/m1.bin b/midia/2026/invasor.bin"
confere "svc-backup NÃO apaga em midia" bash -c "! mc_bkp rm b/midia/2026/m1.bin"
export -f mc_bkp 2>/dev/null || true

# ================================================================== backups com falha simulada em cada etapa
BAK_A="brc-bak-a-$SUF"; LOC_A="brc-local-a-$SUF"
copia_segredos() { local d="$TMP/$1"; mkdir -p "$d"; chmod 755 "$d"; cp "$SEC"/* "$d/"; chmod 644 "$d"/*; echo "$d"; }

echo "== 2. falha simulada em cada etapa: BACKUP_FALHOU etapa=..., código diferente de 0, as etapas seguintes não rodam, nada cru no log"
bad="$(copia_segredos sec-vazio)"; printf '' > "$bad/sql_backup_senha"
rodar_na_imagem backup.sh "$bad" "$BAK_A" ro "$LOC_A" brc-sql-a brc-minio-a
{ [[ "$RC" == 2 ]] && grep -q 'BACKUP_FALHOU etapa=config motivo=segredo_ausente_ou_vazio:sql_backup_senha' <<<"$OUT" && ! grep -q 'BACKUP_SQL_OK' <<<"$OUT"; } && ok "segredo VAZIO: etapa=config (código 2), nomeia o arquivo e nenhuma etapa rodou" || falha "segredo vazio (RC=$RC)" "$OUT"
bad="$(copia_segredos sec-vazio-sa)"; printf '' > "$bad/mssql_sa_password"
rodar_na_imagem restaurar.sh "$bad" "$BAK_A" ro "$LOC_A" brc-sql-a brc-minio-a -e RESTAURAR_CONFIRMA=WebRadio
{ [[ "$RC" == 2 ]] && grep -q 'RESTAURACAO_ABORTADA motivo=segredo_ausente_ou_vazio' <<<"$OUT"; } && ok "restaurar com segredo obrigatório vazio: aborta (2) antes de tocar em qualquer coisa" || falha "restaurar com segredo vazio (RC=$RC)" "$OUT"
bad="$(copia_segredos sec-vazio-root)"; printf '' > "$bad/minio_root_password"
rodar_na_imagem restaurar.sh "$bad" "$BAK_A" ro "$LOC_A" brc-sql-a brc-minio-a -e RESTAURAR_CONFIRMA=WebRadio
{ [[ "$RC" == 2 ]] && grep -q 'RESTAURACAO_ABORTADA motivo=segredo_ausente_ou_vazio' <<<"$OUT" && ! grep -q 'RESTAURACAO_SQL_OK' <<<"$OUT"; } && ok "restaurar sem a credencial root do MinIO: aborta no pré-voo, ANTES do RESTORE (não restaura o banco para depois falhar na mídia)" || falha "restaurar sem a credencial root (RC=$RC)" "$OUT"
bad="$(copia_segredos sec-sql)"; printf '%s' "SenhaErrada123abc" > "$bad/sql_backup_senha"
rodar_na_imagem backup.sh "$bad" "$BAK_A" ro "$LOC_A" brc-sql-a brc-minio-a
{ [[ "$RC" == 10 ]] && grep -q 'BACKUP_FALHOU etapa=sql motivo=' <<<"$OUT" && ! grep -q 'BACKUP_MIDIA_OK' <<<"$OUT"; } && ok "etapa sql (senha do svc_backup errada): código 10 e a etapa midia não rodou" || falha "etapa sql (RC=$RC)" "$OUT"
log_limpo "etapa sql"

bad="$(copia_segredos sec-midia)"; printf '%s' "ChaveErrada0123456789" > "$bad/minio_backup_secret_key"
rodar_na_imagem backup.sh "$bad" "$BAK_A" ro "$LOC_A" brc-sql-a brc-minio-a
{ [[ "$RC" == 20 ]] && grep -q 'BACKUP_FALHOU etapa=midia motivo=credencial_ou_permissao_recusada' <<<"$OUT" && grep -q 'BACKUP_SQL_OK' <<<"$OUT" && ! grep -q 'BACKUP_ENVIO_OK' <<<"$OUT"; } && ok "etapa midia (chave do MinIO errada): código 20, o SQL já tinha rodado e o envio não" || falha "etapa midia (RC=$RC)" "$OUT"
log_limpo "etapa midia"

rodar_na_imagem backup.sh "$SEC" "$BAK_A" ro "$LOC_A" brc-sql-a brc-minio-a
{ [[ "$RC" == 0 ]] && grep -q 'BACKUP_EXTERNO_NAO_CONFIGURADO' <<<"$OUT" && grep -q 'BACKUP_OK' <<<"$OUT" && ! grep -q 'BACKUP_FALHOU' <<<"$OUT"; } && ok "sem cópia externa configurada: BACKUP_EXTERNO_NAO_CONFIGURADO é aviso, o backup local fecha com BACKUP_OK (código 0)" || falha "backup só local (RC=$RC)" "$OUT"
grep -q 'BACKUP_ENVIO_OK destino=local' <<<"$OUT" && ok "envio local (repositório restic criptografado no volume backup-local)" || falha "envio local" "$OUT"
confere "o espelho tem midia e publico e NÃO tem a quarentena" docker run --rm --user 10001:0 -v "$LOC_A:/backup:ro" --entrypoint sh "$IMG_BKP" -c '[ -f /backup/espelho/midia/2026/m6.bin ] && [ -f /backup/espelho/publico/divulgacoes/img2.webp ] && [ ! -e /backup/espelho/quarentena ] && ! find /backup/espelho -name hostil.bin | grep -q .'
confere "o marcador em texto está no espelho e NÃO aparece em claro em lugar nenhum do repositório (restic criptografa)" docker run --rm --user 10001:0 -v "$LOC_A:/backup:ro" -e M="$MARCADOR" --entrypoint sh "$IMG_BKP" -c 'grep -rqF "$M" /backup/espelho && ! grep -rqF "$M" /backup/repo'

# externo ligado com endpoint inalcançável: a etapa envio (externo) falha com 30
# as chaves externas vazias desligam o externo; para este caso elas precisam existir:
ext="$(copia_segredos sec-ext)"; printf '%s' "$EXT_USER" > "$ext/backup_s3_access_key"; printf '%s' "$EXT_PASS" > "$ext/backup_s3_secret_key"
rodar_na_imagem backup.sh "$ext" "$BAK_A" ro "$LOC_A" brc-sql-a brc-minio-a -e BACKUP_PERMITIR_HTTP=1 -e BACKUP_ETAPAS=envio -e BACKUP_TIMEOUT_EXTERNO_S=20 -e BACKUP_S3_ENDPOINT=http://brc-ninguem:9000 -e BACKUP_S3_BUCKET=backup-ext
{ [[ "$RC" == 30 ]] && grep -qE 'BACKUP_FALHOU etapa=envio destino=externo motivo=(sem_conexao|tempo_esgotado)' <<<"$OUT" && grep -q 'BACKUP_ENVIO_OK destino=local' <<<"$OUT"; } && ok "etapa envio (storage externo inalcançável): código 30, destino=externo, o local já tinha sido gravado" || falha "etapa envio (RC=$RC)" "$OUT"
log_limpo "etapa envio"

echo "== 3. backup completo COM a cópia externa (o MinIO 'externo'), retenção e restic check"
rodar_na_imagem backup.sh "$ext" "$BAK_A" ro "$LOC_A" brc-sql-a brc-minio-a -e BACKUP_S3_ENDPOINT=http://brc-ext:9000 -e BACKUP_S3_BUCKET=backup-ext
{ [[ "$RC" == 2 ]] && grep -q 'BACKUP_FALHOU etapa=config motivo=endpoint_s3_precisa_de_https' <<<"$OUT" && ! grep -q 'BACKUP_SQL_OK' <<<"$OUT"; } && ok "BACKUP_S3_ENDPOINT em http:// é recusado (2, etapa=config): as chaves e os dados não vão em claro" || falha "endpoint http sem a variável de teste (RC=$RC)" "$OUT"
rodar_na_imagem backup.sh "$ext" "$BAK_A" ro "$LOC_A" brc-sql-a brc-minio-a -e BACKUP_PERMITIR_HTTP=1 -e BACKUP_S3_ENDPOINT=http://brc-ext:9000 -e BACKUP_S3_BUCKET=backup-ext -e BACKUP_FORCAR_CHECK=1
{ [[ "$RC" == 0 ]] && grep -q 'BACKUP_ENVIO_OK destino=externo' <<<"$OUT" && grep -q 'BACKUP_RETENCAO_OK' <<<"$OUT" && grep -q 'BACKUP_CHECK_OK' <<<"$OUT" && grep -q 'BACKUP_OK' <<<"$OUT" && ! grep -q 'NAO_CONFIGURADO' <<<"$OUT"; } && ok "BACKUP_OK com envio externo, retenção e check" || falha "backup com externo (RC=$RC)" "$OUT"
# o repositório externo existe e tem os snapshots (o `restic copy`): só com a senha do restic
snaps_ext="$(docker run --rm --network "$NET" --user 10001:0 -e HOME=/tmp -e RESTIC_PASSWORD_FILE=/run/secrets/restic_senha -e AWS_ACCESS_KEY_ID="$EXT_USER" -e AWS_SECRET_ACCESS_KEY="$EXT_PASS" -v "$SEC:/run/secrets:ro" --entrypoint restic "$IMG_BKP" -r s3:http://brc-ext:9000/backup-ext snapshots --json 2>/dev/null | python3 -I -c 'import json,sys; print(len(json.load(sys.stdin)))' || echo 0)"
[[ "$snaps_ext" -ge 1 ]] && ok "o repositório externo tem $snaps_ext snapshot(s)" || falha "o repositório externo está sem snapshot"
confere "sem a senha do restic o repositório externo não abre" bash -c "! docker run --rm --network '$NET' --user 10001:0 -e HOME=/tmp -e RESTIC_PASSWORD=senha-errada -e AWS_ACCESS_KEY_ID='$EXT_USER' -e AWS_SECRET_ACCESS_KEY='$EXT_PASS' --entrypoint restic '$IMG_BKP' -r s3:http://brc-ext:9000/backup-ext snapshots"

echo "== 4. check com o repositório local danificado (um pack apagado) e retenção com o storage externo fora do ar"
docker run --rm --user 10001:0 -v "$LOC_A:/backup" --entrypoint sh "$IMG_BKP" -c 'f="$(find /backup/repo/data -type f | head -1)"; rm -f "$f"'
rodar_na_imagem backup.sh "$SEC" "$BAK_A" ro "$LOC_A" brc-sql-a brc-minio-a -e BACKUP_ETAPAS=check -e BACKUP_FORCAR_CHECK=1
{ [[ "$RC" == 50 ]] && grep -q 'BACKUP_FALHOU etapa=check destino=local motivo=' <<<"$OUT"; } && ok "etapa check (pack apagado no repositório): código 50" || falha "etapa check (RC=$RC)" "$OUT"
log_limpo "etapa check"
# A retenção local passa mesmo com o pack apagado (não há o que podar); a que falha é a EXTERNA, com o storage fora do ar na hora (o caso real).
rodar_na_imagem backup.sh "$ext" "$BAK_A" ro "$LOC_A" brc-sql-a brc-minio-a -e BACKUP_PERMITIR_HTTP=1 -e BACKUP_ETAPAS=retencao -e BACKUP_TIMEOUT_EXTERNO_S=20 -e BACKUP_S3_ENDPOINT=http://brc-ninguem:9000 -e BACKUP_S3_BUCKET=backup-ext
{ [[ "$RC" == 40 ]] && grep -qE 'BACKUP_FALHOU etapa=retencao destino=externo motivo=(sem_conexao|tempo_esgotado)' <<<"$OUT"; } && ok "etapa retencao (storage externo fora do ar): código 40, destino=externo" || falha "etapa retencao (RC=$RC)" "$OUT"
log_limpo "etapa retencao"

# ================================================================== o servidor morre
echo "== 5. o servidor A morre: SQL Server, MinIO e o volume local somem. Fica só o storage externo e os segredos."
docker rm -f "brc-sql-a-$SUF" "brc-minio-a-$SUF" >/dev/null
docker volume rm -f "$BAK_A" "brc-data-a-$SUF" "$LOC_A" >/dev/null

echo "== 6. servidor B (novo): volumes novos, logins com SID novo, MinIO novo com o minio-init"
docker volume create "brc-local-b-$SUF" >/dev/null
subir_sql "brc-sql-b-$SUF" "brc-bak-b-$SUF"
db_init "brc-sql-b-$SUF" && ok "db-init no B (logins recriados com SID novo; o WebRadio nasce vazio)" || { falha "db-init no B falhou"; exit 1; }
subir_minio "brc-minio-b-$SUF" "brc-data-b-$SUF"
minio_init "brc-minio-b" >/dev/null && ok "minio-init no B" || falha "minio-init no B falhou"
BAK_B="brc-bak-b-$SUF"; LOC_B="brc-local-b-$SUF"
ext_cfg=(-e BACKUP_PERMITIR_HTTP=1 -e BACKUP_S3_ENDPOINT=http://brc-ext:9000 -e BACKUP_S3_BUCKET=backup-ext)

echo "-- a trava e a confirmação do restaurar.sh"
rodar_na_imagem restaurar.sh "$ext" "$BAK_B" rw "$LOC_B" brc-sql-b brc-minio-b "${ext_cfg[@]}" -e RESTAURAR_CONFIRMA=NaoEhOBanco
{ [[ "$RC" == 22 ]] && grep -q 'RESTAURACAO_ABORTADA motivo=confirmacao_diferente_do_nome_do_banco' <<<"$OUT"; } && ok "confirmação com outro nome: aborta (22) sem tocar no banco" || falha "confirmação errada (RC=$RC)" "$OUT"
rodar_na_imagem restaurar.sh "$ext" "$BAK_B" rw "$LOC_B" brc-sql-b brc-minio-b "${ext_cfg[@]}"
{ [[ "$RC" == 2 ]] && grep -q 'sem_terminal_e_sem_RESTAURAR_CONFIRMA' <<<"$OUT"; } && ok "sem terminal e sem RESTAURAR_CONFIRMA: aborta (nunca restaura sem a pergunta)" || falha "sem terminal (RC=$RC)" "$OUT"
sa_sql "brc-sql-b-$SUF" -h -1 -W -Q "SET NOCOUNT ON; SELECT COUNT(*) FROM WebRadio.sys.tables" | tr -d '[:space:]' | grep -qx 0 && ok "o banco B segue vazio depois dos abortos" || falha "o banco B foi tocado por uma restauração abortada"

echo "-- caminho de FALHA do SQL (go-sqlcmd): .bak corrompido no restore.sql (VERIFYONLY) deixa o banco intacto e MULTI_USER"
docker run --rm --user 10001:0 -v "$BAK_B:/bak" --entrypoint sh "$IMG_BKP" -c 'head -c 4096 /dev/urandom > /bak/webradio.bak'
sa_gosql brc-sql-b-$SUF -i /sql/restore.sql
{ [[ "$RC" != 0 ]] && grep -qE 'Msg 3(189|241|013|203)|VERIFYONLY|not a valid|damaged|incorrectly formatted' <<<"$OUT"; } && ok "restore.sql com .bak corrompido: o go-sqlcmd sai com código $RC (não 0) e o VERIFYONLY recusa" || falha "restore.sql com .bak corrompido (RC=$RC)" "$OUT"
sa_sql "brc-sql-b-$SUF" -h -1 -W -Q "SET NOCOUNT ON; SELECT state_desc + N' ' + user_access_desc FROM sys.databases WHERE name = N'WebRadio'" | tr -d '\r' | grep -qx 'ONLINE MULTI_USER' && ok "o WebRadio do B segue ONLINE e MULTI_USER depois da recusa" || falha "o WebRadio do B ficou em estado errado depois do .bak corrompido"
sa_sql "brc-sql-b-$SUF" -h -1 -W -Q "SET NOCOUNT ON; SELECT COUNT(*) FROM WebRadio.sys.tables" | tr -d '[:space:]' | grep -qx 0 && ok "o banco B segue intacto (vazio, como o db-init o deixou)" || falha "o banco B foi alterado pelo .bak corrompido"
docker run --rm --user 10001:0 -v "$BAK_B:/bak" --entrypoint sh "$IMG_BKP" -c 'rm -f /bak/webradio.bak'

echo "-- restaurar.sh com um backup em andamento (o flock do backup.sh segurado por outro contêiner no MESMO volume) aborta"
docker run -d --name "brc-lock-$SUF" --user 10001:0 -v "$LOC_B:/backup" --entrypoint sh "$IMG_BKP" -c 'exec 9>/backup/.backup.lock; flock -n 9 && sleep 120' >/dev/null
sleep 3
rodar_na_imagem restaurar.sh "$ext" "$BAK_B" rw "$LOC_B" brc-sql-b brc-minio-b "${ext_cfg[@]}" -e RESTAURAR_CONFIRMA=WebRadio
{ [[ "$RC" == 23 ]] && grep -q 'RESTAURACAO_ABORTADA motivo=backup_em_andamento' <<<"$OUT"; } && ok "backup em andamento: o restaurar aborta (23) sem tocar no banco" || falha "restaurar com backup em andamento (RC=$RC)" "$OUT"
docker rm -f "brc-lock-$SUF" >/dev/null 2>&1 || true
echo "-- restaurar.sh a partir SÓ do storage externo (o volume local do B é novo, sem repositório)"
rodar_na_imagem restaurar.sh "$ext" "$BAK_B" rw "$LOC_B" brc-sql-b brc-minio-b "${ext_cfg[@]}" -e RESTAURAR_CONFIRMA=WebRadio
{ [[ "$RC" == 0 ]] && grep -q 'RESTAURACAO_FONTE fonte=externo' <<<"$OUT" && grep -q 'RESTAURACAO_SQL_OK' <<<"$OUT" && grep -q 'RESTAURACAO_POS_RESTORE_OK' <<<"$OUT" && grep -q 'RESTAURACAO_OK' <<<"$OUT"; } && ok "RESTAURACAO_OK a partir do repositório EXTERNO (db-init → restore.sql → pos-restore.sql → mc mirror)" || falha "restaurar.sh (RC=$RC)" "$OUT"
log_limpo "restaurar"

echo "-- caminho de FALHA do SQL (go-sqlcmd): pos-restore.sql com um usuário órfão SEM login homônimo (o db-init não rodou) aborta"
sa_sql "brc-sql-b-$SUF" -d WebRadio -Q "CREATE LOGIN orfao_teste WITH PASSWORD = N'Orf$(openssl rand -hex 8)A1'; CREATE USER orfao_teste FOR LOGIN orfao_teste; DROP LOGIN orfao_teste;" >/dev/null
sa_gosql brc-sql-b-$SUF -i /sql/pos-restore.sql
{ [[ "$RC" != 0 ]] && grep -q 'orfao_teste' <<<"$OUT" && grep -qiE 'não tem login|usuário a mais' <<<"$OUT"; } && ok "pos-restore.sql com órfão sem login: o go-sqlcmd sai com código $RC (não 0) e nomeia o usuário (a auditoria do #171 o barra como 'usuário a mais' ou o religamento como 'sem login')" || falha "pos-restore.sql com órfão sem login (RC=$RC)" "$OUT"
sa_sql "brc-sql-b-$SUF" -d WebRadio -Q "DROP USER orfao_teste" >/dev/null
sa_gosql brc-sql-b-$SUF -i /sql/pos-restore.sql
{ [[ "$RC" == 0 ]] && grep -q 'POS_RESTORE_OK' <<<"$OUT"; } && ok "pos-restore.sql idempotente depois (sem órfão: código 0, POS_RESTORE_OK)" || falha "pos-restore.sql depois de limpar o órfão (RC=$RC)" "$OUT"

echo "== 7. conferência do que voltou"
for u in "webradio_app:db_app_password" "webradio_migrator:db_migrator_password" "webradio_relatorio:db_relatorio_password" "svc_backup:sql_backup_senha"; do
  confere "login ${u%%:*} abre o WebRadio (usuário religado, sem órfão)" como "brc-sql-b-$SUF" "${u%%:*}" "$(cat "$SEC/${u#*:}")" -d WebRadio -Q "SELECT 1"
done
contagens "brc-sql-b-$SUF" > "$TMP/contagem-b.txt"
diff -q "$TMP/contagem-a.txt" "$TMP/contagem-b.txt" >/dev/null && ok "contagem de linhas IGUAL em todas as tabelas ($(grep -c . "$TMP/contagem-b.txt"))" || { falha "contagem de linhas diferente" "$(diff "$TMP/contagem-a.txt" "$TMP/contagem-b.txt")"; }
sa_sql "brc-sql-b-$SUF" -h -1 -W -Q "SET NOCOUNT ON; SELECT recovery_model_desc + N' ' + state_desc + N' ' + user_access_desc FROM sys.databases WHERE name = N'WebRadio'" | tr -d '\r' | grep -qx 'SIMPLE ONLINE MULTI_USER' && ok "WebRadio no B: SIMPLE, ONLINE, MULTI_USER" || falha "estado do WebRadio no B"
sa_sql "brc-sql-b-$SUF" -d WebRadio -h -1 -W -Q "SET NOCOUNT ON; DBCC CHECKDB (WebRadio) WITH NO_INFOMSGS" >/dev/null 2>&1 && ok "DBCC CHECKDB limpo no B" || falha "DBCC CHECKDB no B"
for obj in "midia/2026/m1.bin:m1.bin" "midia/2026/m6.bin:m6.bin" "publico/divulgacoes/img1.webp:img1.webp" "publico/divulgacoes/img2.webp:img2.webp"; do
  chave="${obj%%:*}"; orig="${obj##*:}"
  a="$(sha256sum "$ARQ/$orig" | cut -d' ' -f1)"; b="$(mc_raiz brc-minio-b cat "x/$chave" | sha256sum | cut -d' ' -f1)"
  [[ "$a" == "$b" ]] && ok "objeto $chave voltou com o mesmo SHA-256" || falha "objeto $chave diferente do original"
done
[[ "$(mc_raiz brc-minio-b ls --recursive x/midia | wc -l)" -eq 6 ]] && ok "midia no B tem os 6 objetos" || falha "midia no B não tem os 6 objetos"
[[ "$(mc_raiz brc-minio-b ls --recursive x/quarentena | wc -l)" -eq 0 ]] && ok "a quarentena NÃO foi para o backup (vazia no B)" || falha "a quarentena tem objeto no B"
# a "pré-escuta": uma URL pré-assinada de GET (como a API gera) devolve os bytes certos
url="$(mc_raiz brc-minio-b share download --expire 5m x/midia/2026/m3.bin | sed -n 's/^Share: //p' | tr -d '\r')"
bytes="$(docker run --rm --network "$NET" --entrypoint sh "$IMG_BKP" -c "wget -q -O - '$url'" | sha256sum | cut -d' ' -f1)"
[[ -n "$url" && "$bytes" == "$(sha256sum "$ARQ/m3.bin" | cut -d' ' -f1)" ]] && ok "uma música da biblioteca toca pela URL pré-assinada do B (os bytes batem)" || falha "pré-escuta pela URL pré-assinada"

echo
if [[ "$falhas" -gt 0 ]]; then echo "backup-restore-check: $falhas falha(s)." >&2; exit 1; fi
echo "backup-restore-check: tudo ok."
