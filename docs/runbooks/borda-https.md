# Borda HTTPS (Caddy): operação

Runbook do E5-F02 (decisões E5-D1, D2, D9 e D12 do [`09-epico-5-tarefas.md`](../specs/09-epico-5-tarefas.md)). Vale para o Compose de produção (`docker-compose.yml`). O dev **não** tem Caddy: a `8080` em HTTP continua sendo a entrada.

## O que é

- Serviço `edge` (Caddy, imagem oficial por digest), o **único** contêiner publicado: `0.0.0.0:80` (só redireciona e valida o domínio no Let's Encrypt) e `0.0.0.0:443` (HTTPS). O `web` (nginx) **não publica porta**; só o `edge` o alcança, pela rede `edge` (subnet fixa, só os dois).
- O Caddy emite e **renova sozinho** o certificado do `DOMINIO` no Let's Encrypt (só ele; o ZeroSSL fica desligado). O certificado e a conta do ACME ficam no volume `edge-data`.
- Arquivos: `infra/edge/Caddyfile` (a configuração), `infra/edge/tls-acme.caddy` (produção: só Let's Encrypt) e `infra/edge/tls-interno.caddy` (`tls internal`, **só** o override do CI).
- Variáveis do `.env` (não são segredos): `DOMINIO` (o host público, sem porta nem esquema), `ACME_EMAIL`, `HSTS_MAX_AGE` (padrão `86400`) e, se colidir com outra rede do servidor, `EDGE_SUBNET` (padrão `172.30.250.0/29`).
- O `DOMINIO` é a **única** fonte do host público: o Caddy, o `server_name` e o `Host` canônico do nginx, o `AllowedHosts` da API, o `Storage__PublicEndpoint` (`https://<DOMINIO>/storage`) e o hostname do Icecast saem dele.

## Antes da primeira subida

1. O registro **A** do `DOMINIO` aponta para o IP da VPS. **Sem registro AAAA** na v1 (E5-D12): os limites por IP foram pensados para IPv4, e com AAAA o Docker entregaria ao Caddy o IP do `docker-proxy` e não o do cliente.
2. As portas **80 e 443** do host estão livres e liberadas no firewall (e a `443/udp` **fechada**: sem HTTP/3 na v1). IPv6 desligado no Docker e o firewall recusando entrada por IPv6.
3. `.env` com `DOMINIO=radio.exemplo.com.br` e um `ACME_EMAIL` real. Com o `DOMINIO` ainda `localhost` o Caddy não consegue certificado público.
4. Primeira subida: `docker compose up -d --wait`. Confira que o certificado saiu: `docker compose logs edge | grep -i 'certificate obtained'`.

## HSTS: de 1 dia para 1 ano

O HSTS sai **só** do Caddy (o nginx não o emite, para não duplicar) com `max-age=86400`, sem `includeSubDomains` nem `preload` na v1. Um valor longo logo de saída prenderia os navegadores ao HTTPS se o deploy precisasse voltar atrás.

Depois de **uma semana** sem problema de certificado, suba:

1. No `.env`: `HSTS_MAX_AGE=31536000`.
2. `docker compose up -d --force-recreate --wait edge` (o `up` sem `--force-recreate` não relê o `.env` se o YAML não mudou). O volume `edge-data` **não** é recriado: o certificado fica.
3. Confira: `curl -sI https://<domínio>/ | grep -i strict-transport-security` mostra um único cabeçalho com `max-age=31536000`.

## Conferências rápidas

```bash
curl -sI  https://<domínio>/                      # 200, um Strict-Transport-Security, uma Content-Security-Policy
curl -sI  http://<domínio>/                       # 308 para https://<domínio>/
docker compose logs --no-color edge | tail -20    # JSON: sem query string, IP com o último octeto zerado
docker compose ps edge                            # healthy
ss -ltn '( sport = :80 or sport = :443 )'         # só 0.0.0.0 (nenhuma linha com [::] ou *)
```

O IP real do ouvinte chega ao nginx e à API: dois clientes de IPs diferentes **não** dividem o balde dos limites, e um `X-Forwarded-For` forjado pelo cliente não muda o IP visto. O CI confere isso a cada PR (job `compose-producao`, `infra/scripts/smoke-borda.sh`); no deploy, repita pelo roteiro do guia.

## Cuidados

- **Nunca** `docker compose down -v` em produção: apaga o `edge-data` e o próximo `up` pede certificado de novo. O Let's Encrypt limita a emissão por domínio (50 por semana, 5 certificados iguais por semana): um deploy repetido pode ficar sem certificado por dias.
- Trocar de domínio, ou mexer no `Caddyfile`: `docker compose up -d --force-recreate --wait edge` (o `Caddyfile` é um bind mount e o Compose não vê a mudança no conteúdo).
- O log do Caddy **não** traz IP completo nem query string, de propósito (E5-D9). Para investigar um IP, use o log do `web` (anonimizado também) e o do firewall do host.
- O log do `web` (nginx) agora mostra o IP **do cliente**, não o do Caddy: o `error_log` com os 429 traz o IP completo (a retenção de 14 dias de `docs/operacao-logs.md` vale).

## Erros comuns

| Sintoma | Causa | O que fazer |
|---|---|---|
| `refresh` e `logout` dão 403 no navegador, o login funciona | A API viu `http://` como a própria origem: o `PROTO_PUBLICO` do `web` não está `https` | Confira `docker compose config web` (`PROTO_PUBLICO: https`) e recrie o `web` |
| Todo ouvinte leva 429, ou o 26º ouvinte do `/stream/` é derrubado | O nginx vê o IP do Caddy: o `EDGE_SUBNET` do `web` não é a subnet real da rede `edge`, ou o cliente chegou por IPv6 (registro AAAA, ou `[::]` publicado) | `docker network inspect webradio_edge` e o `EDGE_SUBNET` do `.env`; remova o AAAA |
| O `edge` não sobe: `bind: address already in use` | Outro processo (nginx, apache) escuta na 80 ou na 443 do host | `ss -ltnp '( sport = :80 or sport = :443 )'` e pare quem estiver lá |
| `edge` sobe, mas o navegador mostra erro de certificado | O Let's Encrypt recusou: DNS ainda não aponta para a VPS, porta 80 fechada, ou limite de emissão | `docker compose logs edge \| grep -iE 'error\|acme'` |
| Falha de rede entre o `edge` e o `web` | O `web` não está `healthy` | `docker compose ps web` e `docker compose logs web` |
