# syntax=docker/dockerfile:1
# E4-F01 — Liquidsoap próprio: a imagem oficial v2.4.6 (Debian 13, uid 100) com os pacotes que o trivy marca como CRITICAL/HIGH
# corrigíveis já atualizados. A base fica por digest; o `apt-get install --only-upgrade` pega a revisão corrente de segurança do
# Debian (a oficial é publicada de tempos em tempos e fica para trás). Contexto de build = RAIZ do repositório:
#   docker build -f infra/liquidsoap/Dockerfile -t webradio-liquidsoap:local .
# O radio.liq e a playlist de teste entram por volume (docker-compose.yml), como antes. Sem root em execução: volta para o uid 100.
FROM savonet/liquidsoap:v2.4.6@sha256:63966c36c5ea208ba56fac787e955502b348eb00c5c32cdd134ee5ec1d20de53

USER root
# Só o que o trivy aponta como corrigível (HIGH e CRITICAL; em 2026-11-23 o krb5 1.21.3-5+deb13u1, corrigido em deb13u2: libgssapi-krb5-2, libk5crypto3, libkrb5-3, libkrb5support0); a lista de índices não fica na imagem. Quando a oficial vier com a correção, o
# pacote já está na última versão e o comando não muda nada.
RUN apt-get update \
    && apt-get install -y --no-install-recommends --only-upgrade libbrotli1 zlib1g apt libapt-pkg7.0 libgssapi-krb5-2 libk5crypto3 libkrb5-3 libkrb5support0 \
    && rm -rf /var/lib/apt/lists/*
USER liquidsoap
