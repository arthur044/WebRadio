# syntax=docker/dockerfile:1
# E1-F16 — minio-init: o mc (cliente MinIO) + o script de buckets/políticas/usuários de serviço.
# A imagem oficial do mc não tem shell; o binário (Go, estático) é compilado aqui para um Alpine com sh.
# Contexto = RAIZ do repositório:  docker build -f infra/minio/Dockerfile -t webradio-minio-init .
#
# Por que compilar o mc em vez de copiar de cgr.dev/chainguard/minio-client: o mc da Chainguard (mesmo commit abaixo)
# traz github.com/klauspost/compress v1.18.5, com o CVE-2026-63209 (corrigido na 1.18.7), e o trivy do CI falha o job
# de vulnerabilidades (sem .trivyignore). Aqui o commit do minio/mc é o mesmo, com o compress forçado para >= 1.18.7.
# TODO: voltar para `FROM cgr.dev/chainguard/minio-client:latest@sha256:<digest> AS mc` + `COPY --from=mc /usr/bin/mc`
# quando o digest dela trouxer o compress >= 1.18.7 (o Dependabot avisa; conferir com `go version -m` e com o trivy).
# Última checagem: 2026-10-29, o `latest` da Chainguard (sha256:799a60aa…) ainda tinha o compress v1.18.5.
#
# Go >= 1.27.3 fixado por digest (o Dependabot troca). GOTOOLCHAIN=local: nunca baixa outro toolchain no build.
FROM golang:1.27.3-alpine@sha256:d200d2dd76b03a99b52651fd1325aff7dcd35422c35c01bac56571b0507f3deb AS mc
ENV CGO_ENABLED=0 GOTOOLCHAIN=local
# minio/mc, tag RELEASE.2025-08-13T08-35-41Z = commit 7394ce0dd2a80935aded936b09fa12cbb3cb8096 (hash completo: imutável).
ADD https://github.com/minio/mc.git#7394ce0dd2a80935aded936b09fa12cbb3cb8096 /src
WORKDIR /src
# Dependências: as versões do go.mod do mc de 2025-08 têm CVEs HIGH/CRITICAL (x/crypto, x/net, x/text, grpc, prometheus);
# são as mesmas versões que a Chainguard usa no mc dela, exceto o compress (1.18.7, que ela ainda não tem).
# go.sum das demais dependências vem do próprio mc; as versões acima são verificadas contra o sumdb (GOSUMDB padrão).
RUN go mod edit \
    -require=github.com/coreos/go-systemd/v22@v22.6.0 \
    -require=github.com/klauspost/compress@v1.18.7 \
    -require=github.com/prometheus/client_golang@v1.23.2 \
    -require=github.com/prometheus/common@v0.67.5 \
    -require=github.com/prometheus/procfs@v0.16.1 \
    -require=github.com/prometheus/prometheus@v0.311.3 \
    -require=go.uber.org/zap@v1.27.1 \
    -require=golang.org/x/crypto@v0.57.0 \
    -require=golang.org/x/net@v0.60.0 \
    -require=golang.org/x/sync@v0.23.0 \
    -require=golang.org/x/sys@v0.48.0 \
    -require=golang.org/x/term@v0.46.0 \
    -require=golang.org/x/text@v0.42.0 \
    -require=google.golang.org/genproto/googleapis/api@v0.0.0-20260526163538-3dc84a4a5aaa \
    -require=google.golang.org/genproto/googleapis/rpc@v0.0.0-20260526163538-3dc84a4a5aaa \
    -require=google.golang.org/grpc@v1.83.2 \
    -require=google.golang.org/protobuf@v1.36.11 \
 && go mod tidy \
 && go build -trimpath -ldflags="-s -w \
      -X github.com/minio/mc/cmd.Version=2025-08-13T08:35:41Z \
      -X github.com/minio/mc/cmd.ReleaseTag=RELEASE.2025-08-13T08-35-41Z \
      -X github.com/minio/mc/cmd.CommitID=7394ce0dd2a80935aded936b09fa12cbb3cb8096" -o /out/mc . \
 && go version -m /out/mc | grep -E '^\s+dep\s+github.com/klauspost/compress\s+v1\.18\.7(\s|$)'

FROM alpine:3.24.2@sha256:294b683cb724975bec92580e1e685676bd4b50bda910ddb8c51d4cabeaec77e6
COPY --from=mc --chmod=0555 /out/mc /usr/local/bin/mc
# Pastas criadas antes: um COPY --chmod=0444 de pasta deixaria a própria pasta sem o bit x (intransponível).
RUN mkdir -p -m 0755 /etc/minio-init/policies
COPY --chmod=0444 infra/minio/policies/*.json /etc/minio-init/policies/
COPY --chmod=0555 infra/minio/init-buckets.sh /usr/local/bin/init-buckets.sh
USER 65532:65532
ENTRYPOINT ["/usr/local/bin/init-buckets.sh"]
