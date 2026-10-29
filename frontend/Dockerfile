# syntax=docker/dockerfile:1
# Build de contexto = raiz do repositório (o Compose do Forge referencia
# ../infra/nginx a partir daqui), dockerfile: frontend/Dockerfile.

FROM node:22.23.4-alpine3.24@sha256:fe4f6d74e20370acb04ad889077d14d0f64a9c8ea6a42bc60cddf1e844699914 AS build
WORKDIR /app
COPY frontend/package.json frontend/package-lock.json ./
RUN npm ci
COPY frontend/ .
# Build puro, sem limpeza pós-build (Guardian H2): o vite.config.ts de produção não
# tem a entry do probe nem a fixture de áudio — elas só existem no
# vite.config.e2e.ts, usado pelo Playwright local, nunca por este Dockerfile.
RUN npm run build

# Fixado por digest (a tag é mutável): nginxinc/nginx-unprivileged:1.31.6-alpine3.24 (nginx mainline 1.31.6 sobre Alpine 3.24.2).
# Era 1.27.5-alpine3.21 (jun/2025): sem imagem corrigida na linha 3.21 (B3, trivy).
FROM nginxinc/nginx-unprivileged:1.31.6-alpine3.24@sha256:884e0b7f42f303641b0f4e78755aea647521dca0b89ca3be7992ad78b5c07434
# Correções que a base ainda não traz (trivy HIGH, com correção disponível no Alpine 3.24):
# libexpat CVE-2026-77214 (2.8.5-r0, corrigido em 2.9.0-r0) e libx11 CVE-2026-88806 (1.8.13-r0,
# corrigido em 1.8.14-r0). A versão mínima é explícita (B3-09): se a correção sumir do mirror, o build
# falha em vez de passar com o pacote vulnerável. Remover este bloco (USER root, apk e o USER 101 de
# volta) quando a base fixada acima trouxer as versões corrigidas; num bump da base (Dependabot), se o
# trivy estiver limpo sem o bloco, ele sai no mesmo PR.
USER root
RUN apk add --no-cache --upgrade 'libexpat>=2.9.0-r0' 'libx11>=1.8.14-r0'
USER 101
# Hosts públicos válidos (server_name aceita lista espaço-separada) — o compose
# sobrescreve em outros ambientes; ver comentário de Host header injection em
# infra/nginx/default.conf.template.
ENV PUBLIC_HOST="localhost 127.0.0.1"
# Host:porta EXATA que o proxy repassa aos upstreams (revisão do Sentinel — nunca o
# que o cliente mandou). Tem que bater com Storage__PublicEndpoint do backend.
ENV PUBLIC_AUTHORITY="localhost:8080"
COPY --from=build /app/dist /usr/share/nginx/html
COPY infra/nginx/nginx.conf /etc/nginx/nginx.conf
COPY infra/nginx/security-headers.conf /etc/nginx/security-headers.conf
COPY infra/nginx/default.conf.template /etc/nginx/templates/default.conf.template

EXPOSE 8080
