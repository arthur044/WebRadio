# syntax=docker/dockerfile:1
# E5-F03 — imagem do `backup` e do `restaurar`: restic + sqlcmd + mc + os dois scripts. Contexto = RAIZ do repositório:
#   docker build -f infra/backup/Dockerfile --build-context mc-img=docker-image://webradio-minio-init:local -t webradio-backup:local .
# (no Compose, o `additional_contexts: mc-img: service:minio-init` constrói o minio-init antes e passa a imagem.)
#
# Os três binários são Go estáticos, sem root e sem apt:
#   - restic 0.20.0: baixado do GitHub com o SHA-256 CONFERIDO pelo próprio BuildKit (`ADD --checksum`: o build FALHA se o arquivo mudar). O valor foi cruzado em 2026-11-23 com o SHA256SUMS
#     da release e com o `digest` que a API do GitHub publica para o asset (iguais). O trivy o lê limpo.
#   - go-sqlcmd 1.11.0: COMPILADO aqui, não baixado. O binário da release vem com Go 1.26.0 e golang.org/x/net 0.60.0 e o trivy do CI acusou 27 HIGH corrigíveis (stdlib e x/net/html);
#     o mesmo commit da tag (hash completo: imutável), compilado com o Go do `mc` e o x/net forçado para >= 0.62.0, sai limpo. Mesmo padrão do `mc` no infra/minio/Dockerfile.
#   - mc: o MESMO binário do minio-init (compilado a partir do commit fixado, com as dependências corrigidas para o trivy): não se baixa outro mc sem essas correções.
FROM alpine:3.24.4@sha256:f59abe656b692d901e184be3445e084786602108599d2a51d229c0f5a48c71bf AS baixar
RUN apk add --no-cache bzip2
ADD --checksum=sha256:0ccd9fa7885074a24603b051e01bf9bd8b5e45d6a1791e9d9887e9e9372107f7 https://github.com/restic/restic/releases/download/v0.20.0/restic_0.20.0_linux_amd64.bz2 /tmp/restic.bz2
RUN mkdir /out && bunzip2 -c /tmp/restic.bz2 > /out/restic && chmod 0555 /out/restic && /out/restic version

# Go >= 1.27.3 fixado por digest (o mesmo do mc). GOTOOLCHAIN=local: nunca baixa outro toolchain no build.
FROM golang:1.27.4-alpine@sha256:0a5f7fe796599ff487605987432e7619ae45beeb2fba4c2ce0b907879932aebf AS sqlcmd
ENV CGO_ENABLED=0 GOTOOLCHAIN=local
# microsoft/go-sqlcmd, tag v1.11.0 = commit a97013a80b01f3aaac4fb17e9ef422283f15cb2f.
ADD https://github.com/microsoft/go-sqlcmd.git#a97013a80b01f3aaac4fb17e9ef422283f15cb2f /src
WORKDIR /src
RUN go mod edit -require=golang.org/x/net@v0.62.0 \
 && go mod tidy \
 && go build -trimpath -ldflags="-s -w" -o /out/sqlcmd ./cmd/modern \
 && /out/sqlcmd --version \
 && go version -m /out/sqlcmd | grep -E '^\s+dep\s+golang.org/x/net\s+v0\.(6[2-9]|[7-9][0-9])\.'

FROM alpine:3.24.4@sha256:f59abe656b692d901e184be3445e084786602108599d2a51d229c0f5a48c71bf
# bash (os scripts usam arrays e [[ ]]); o resto do que precisam (flock, date, du, tr, grep) é do busybox.
RUN apk add --no-cache bash
COPY --from=baixar --chmod=0555 /out/restic /usr/local/bin/restic
COPY --from=sqlcmd --chmod=0555 /out/sqlcmd /usr/local/bin/sqlcmd
COPY --from=mc-img --chmod=0555 /usr/local/bin/mc /usr/local/bin/mc
COPY --chmod=0555 infra/backup/backup.sh /usr/local/bin/backup.sh
COPY --chmod=0555 infra/backup/restaurar.sh /usr/local/bin/restaurar.sh
# O volume backup-local nasce com este dono (o Docker copia o dono da pasta da imagem para um volume novo): o uid 10001 é o do `mssql`, dono do volume sql-backup (E5-D13).
RUN mkdir -p /backup && chown 10001:0 /backup && chmod 0750 /backup
ENV HOME=/tmp MC_CONFIG_DIR=/tmp/.mc
USER 10001:0
ENTRYPOINT ["/usr/local/bin/backup.sh"]
