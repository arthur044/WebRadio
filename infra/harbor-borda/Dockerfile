# syntax=docker/dockerfile:1
# E4-F01 — harbor-borda próprio: o nginx-unprivileged por digest (a mesma base do frontend/Dockerfile) com as correções que ela ainda não traz.
# É a única imagem de terceiros publicada para fora (docker-compose.live.yml). O nginx.conf e o entrypoint continuam entrando por volume.
# Contexto de build = RAIZ do repositório:  docker build -f infra/harbor-borda/Dockerfile -t webradio-harbor-borda:local .
FROM nginxinc/nginx-unprivileged:1.31.7-alpine3.24@sha256:b5047b9b76845eac124358373c6852377c52e4b05834a47647e955dd91cb8f1e
# Correções que a base ainda não traz (trivy HIGH/CRITICAL, com correção disponível no Alpine 3.24): brotli-libs CVE-2026-111497 (CRITICAL,
# 1.2.0-r1, corrigido em 1.2.1-r0), libexpat CVE-2026-77214, libx11 CVE-2026-88806, curl/libcurl CVE-2026-92392 e musl/musl-utils CVE-2026-107891
# (os mesmos pacotes e versões mínimas do frontend/Dockerfile). A versão mínima é explícita (B3-09): se a correção sumir do mirror, o build
# falha em vez de passar com o pacote vulnerável. Remover este bloco (USER root, apk e o USER 101 de volta) quando a base fixada trouxer as
# versões corrigidas; num bump da base (Dependabot), se o trivy estiver limpo sem o bloco, ele sai no mesmo PR. Manter igual ao do frontend.
USER root
RUN apk add --no-cache --upgrade 'libexpat>=2.9.0-r0' 'libx11>=1.8.14-r0' 'brotli-libs>=1.2.1-r0' 'curl>=8.23.0-r0' 'libcurl>=8.23.0-r0' 'musl>=1.2.6-r3' 'musl-utils>=1.2.6-r3'
USER 101
