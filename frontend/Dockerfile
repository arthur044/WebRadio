# syntax=docker/dockerfile:1
# Build de contexto = raiz do repositório (o Compose do Forge referencia
# ../infra/nginx a partir daqui), dockerfile: frontend/Dockerfile.

FROM node@sha256:0a7108bf6c7bf5de370ffb1a3ed6be93d405b43ff159f681a8d18c0e2bc2e402 AS build
WORKDIR /app
COPY frontend/package.json frontend/package-lock.json ./
RUN npm ci
COPY frontend/ .
# Build puro, sem limpeza pós-build (Guardian H2): o vite.config.ts de produção não
# tem a entry do probe nem a fixture de áudio — elas só existem no
# vite.config.e2e.ts, usado pelo Playwright local, nunca por este Dockerfile.
RUN npm run build

# Tag+digest (não só a tag, que é mutável): nginxinc/nginx-unprivileged:1.27-alpine.
FROM nginxinc/nginx-unprivileged@sha256:65e3e85dbaed8ba248841d9d58a899b6197106c23cb0ff1a132b7bfe0547e4c0
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
