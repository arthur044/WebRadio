# syntax=docker/dockerfile:1
# E5-F03 — imagem do `backup` e do `restaurar`: restic + sqlcmd + mc + os dois scripts. Contexto = RAIZ do repositório:
#   docker build -f infra/backup/Dockerfile --build-context mc-img=docker-image://webradio-minio-init:local -t webradio-backup:local .
# (no Compose, o `additional_contexts: mc-img: service:minio-init` constrói o minio-init antes e passa a imagem.)
#
# Os três binários são Go estáticos, sem root e sem apt:
#   - restic 0.20.0 e go-sqlcmd 1.11.0: baixados do GitHub com o SHA-256 CONFERIDO pelo próprio BuildKit (`ADD --checksum`: o build FALHA se o arquivo mudar). Os dois valores foram
#     cruzados em 2026-11-23 com o SHA256SUMS da release do restic e com o `digest` que a API do GitHub publica para cada asset (os três iguais).
#   - mc: o MESMO binário do minio-init (compilado a partir do commit fixado, com as dependências corrigidas para o trivy): não se baixa outro mc sem essas correções.
FROM alpine:3.24.4@sha256:f59abe656b692d901e184be3445e084786602108599d2a51d229c0f5a48c71bf AS baixar
RUN apk add --no-cache bzip2
ADD --checksum=sha256:0ccd9fa7885074a24603b051e01bf9bd8b5e45d6a1791e9d9887e9e9372107f7 https://github.com/restic/restic/releases/download/v0.20.0/restic_0.20.0_linux_amd64.bz2 /tmp/restic.bz2
ADD --checksum=sha256:2e41111d2e00782d50b9f8887bf603890af1e85e308708703e4ce27dc3c410e7 https://github.com/microsoft/go-sqlcmd/releases/download/v1.11.0/sqlcmd-linux-amd64.tar.bz2 /tmp/sqlcmd.tar.bz2
RUN mkdir /out \
 && bunzip2 -c /tmp/restic.bz2 > /out/restic \
 && tar -xjf /tmp/sqlcmd.tar.bz2 -C /out sqlcmd \
 && chmod 0555 /out/restic /out/sqlcmd \
 && /out/restic version && /out/sqlcmd --version

FROM alpine:3.24.4@sha256:f59abe656b692d901e184be3445e084786602108599d2a51d229c0f5a48c71bf
# bash (os scripts usam arrays e [[ ]]); o resto do que precisam (flock, date, du, tr, grep) é do busybox.
RUN apk add --no-cache bash
COPY --from=baixar --chmod=0555 /out/restic /usr/local/bin/restic
COPY --from=baixar --chmod=0555 /out/sqlcmd /usr/local/bin/sqlcmd
COPY --from=mc-img --chmod=0555 /usr/local/bin/mc /usr/local/bin/mc
COPY --chmod=0555 infra/backup/backup.sh /usr/local/bin/backup.sh
COPY --chmod=0555 infra/backup/restaurar.sh /usr/local/bin/restaurar.sh
# O volume backup-local nasce com este dono (o Docker copia o dono da pasta da imagem para um volume novo): o uid 10001 é o do `mssql`, dono do volume sql-backup (E5-D13).
RUN mkdir -p /backup && chown 10001:0 /backup && chmod 0750 /backup
ENV HOME=/tmp MC_CONFIG_DIR=/tmp/.mc
USER 10001:0
ENTRYPOINT ["/usr/local/bin/backup.sh"]
