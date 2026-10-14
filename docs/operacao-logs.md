# Logs dos contêineres: rotação e retenção (B3-02, S-B04)

## O que o compose faz

Todo serviço usa o driver `json-file` com `max-size: 10m` e `max-file: 3` (âncora `x-logging` do `docker-compose.yml`, herdada
por `x-endurecido` e `x-one-shot`): no máximo **30 MB por serviço**, e os arquivos mais antigos são descartados sozinhos. Sem
isso o `json-file` cresceria sem limite. O `smoke-compose.sh` confere que nenhum serviço ficou sem esse bloco.

## Quem guarda IP, e por quanto tempo

| Serviço | O que tem no log | IP |
|---|---|---|
| `web` (nginx) | access log em stdout (`access_anon`) | **anonimizado** na origem: último octeto IPv4 zerado, /64 em IPv6 (`infra/nginx/nginx.conf`) |
| `icecast` | só o error log (stderr); o access log vai para `/dev/null` | nenhum IP de ouvinte em claro |
| `api`, `worker` | JSON de aplicação | sem IP; ouvinte aparece só como hash |

A rotação por **tamanho** limita o espaço, mas **não limita o tempo**: num dia de pouco acesso o arquivo atual pode guardar
linhas por mais de 14 dias. A retenção de **14 dias** do S-B04 não é cumprida só pelo compose; ela depende deste passo no host:

```bash
# cron semanal (root) no host que roda o compose: zera o log do web e do icecast, a única fonte com IP (já anonimizado).
for c in webradio-web-1 webradio-icecast-1; do
  arquivo="$(docker inspect --format '{{.LogPath}}' "$c")" && : > "$arquivo"
done
```

Zerar toda semana garante que nada passe de 7 dias (folga sobre os 14). O comando não derruba o contêiner nem perde o stream.
Se um dia os logs forem para um coletor central (Loki, ELK), a retenção de 14 dias passa a ser configurada lá e este passo sai.

Este passo é **operacional**, não está automatizado no repositório: quem opera a produção instala o cron.
