# syntax=docker/dockerfile:1
# Build de contexto = raiz do repositório (o Compose do Forge referencia
# ../infra/nginx a partir daqui), dockerfile: frontend/Dockerfile.

FROM node:22-alpine AS build
WORKDIR /app
COPY frontend/package.json frontend/package-lock.json ./
RUN npm ci
COPY frontend/ .
# dist/e2e: fixture do Playwright (não é rota do app). dist/stream: fixture de áudio
# de teste local do P03 — em produção o nginx sempre intercepta /stream/radio.mp3
# antes de chegar nos arquivos estáticos (location exata vence o catch-all "/"),
# então esse arquivo nunca seria servido mesmo — só peso morto na imagem.
RUN npm run build && rm -rf dist/e2e dist/stream

FROM nginxinc/nginx-unprivileged:1.27-alpine
# Hosts públicos válidos (server_name aceita lista espaço-separada) — o compose
# sobrescreve em outros ambientes; ver comentário de Host header injection em
# infra/nginx/default.conf.template.
ENV PUBLIC_HOST="localhost 127.0.0.1"
COPY --from=build /app/dist /usr/share/nginx/html
COPY infra/nginx/nginx.conf /etc/nginx/nginx.conf
COPY infra/nginx/security-headers.conf /etc/nginx/security-headers.conf
COPY infra/nginx/default.conf.template /etc/nginx/templates/default.conf.template

EXPOSE 8080
