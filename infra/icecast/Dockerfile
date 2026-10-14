# syntax=docker/dockerfile:1
# E1-F14 — Icecast próprio (Alpine, sem root). Contexto de build = RAIZ do repositório:
#   docker build -f infra/icecast/Dockerfile -t webradio-icecast .
# O icecast.xml NUNCA entra na imagem com senha: o entrypoint o renderiza no tmpfs (/tmp) a partir de /run/secrets.
FROM alpine@sha256:5291449c3df73caf6ed85e649dec1b9e818b39a5d8c871e97afc13e9cd5e8fa8

# icecast 2.4.x do community; o pacote cria o usuário de sistema "icecast" (uid 100).
RUN apk add --no-cache icecast \
    && rm -f /etc/icecast.xml \
    && mkdir -p -m 0755 /etc/icecast

COPY --chmod=0444 infra/icecast/icecast.xml.template /etc/icecast/icecast.xml.template
COPY --chmod=0555 infra/icecast/entrypoint.sh /usr/local/bin/entrypoint.sh

USER icecast
EXPOSE 8000
HEALTHCHECK --interval=15s --timeout=3s --retries=5 CMD wget -q -O /dev/null http://127.0.0.1:8000/status-json.xsl || exit 1
ENTRYPOINT ["/usr/local/bin/entrypoint.sh"]
