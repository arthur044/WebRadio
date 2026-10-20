# syntax=docker/dockerfile:1
# E1-F16 — minio-init: o mc (cliente MinIO) + o script de buckets/políticas/usuários de serviço.
# A imagem oficial do mc não tem shell; o binário (Go, estático) vem dela para um Alpine com sh.
# Contexto = RAIZ do repositório:  docker build -f infra/minio/Dockerfile -t webradio-minio-init .
# minio-client:latest de 2026-10-13 (a Chainguard só publica a tag latest no plano gratuito): fixado por digest.
FROM cgr.dev/chainguard/minio-client:latest@sha256:7d0cf8f3de478cd4d2a8df4efcff9563091e298c8916122bbbbedb2ce08edcad AS mc

FROM alpine:3.22.6@sha256:5291449c3df73caf6ed85e649dec1b9e818b39a5d8c871e97afc13e9cd5e8fa8
COPY --from=mc /usr/bin/mc /usr/local/bin/mc
# Pastas criadas antes: um COPY --chmod=0444 de pasta deixaria a própria pasta sem o bit x (intransponível).
RUN mkdir -p -m 0755 /etc/minio-init/policies
COPY --chmod=0444 infra/minio/policies/*.json /etc/minio-init/policies/
COPY --chmod=0555 infra/minio/init-buckets.sh /usr/local/bin/init-buckets.sh
USER 65532:65532
ENTRYPOINT ["/usr/local/bin/init-buckets.sh"]
