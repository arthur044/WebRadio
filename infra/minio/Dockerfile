# syntax=docker/dockerfile:1
# E1-F16 — minio-init: o mc (cliente MinIO) + o script de buckets/políticas/usuários de serviço.
# A imagem oficial do mc não tem shell; o binário (Go, estático) vem dela para um Alpine com sh.
# Contexto = RAIZ do repositório:  docker build -f infra/minio/Dockerfile -t webradio-minio-init .
# minio-client fixado por digest (a Chainguard só publica a tag latest no plano gratuito; o Dependabot troca o digest).
FROM cgr.dev/chainguard/minio-client:latest@sha256:799a60aa839c0d79b53f1dccdddc974d0fe94282cca712fbfbad3b196f88d0ee AS mc

FROM alpine:3.24.2@sha256:294b683cb724975bec92580e1e685676bd4b50bda910ddb8c51d4cabeaec77e6
COPY --from=mc /usr/bin/mc /usr/local/bin/mc
# Pastas criadas antes: um COPY --chmod=0444 de pasta deixaria a própria pasta sem o bit x (intransponível).
RUN mkdir -p -m 0755 /etc/minio-init/policies
COPY --chmod=0444 infra/minio/policies/*.json /etc/minio-init/policies/
COPY --chmod=0555 infra/minio/init-buckets.sh /usr/local/bin/init-buckets.sh
USER 65532:65532
ENTRYPOINT ["/usr/local/bin/init-buckets.sh"]
