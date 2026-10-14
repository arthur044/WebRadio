# Logs dos contêineres: rotação e retenção (B3-02, S-B04)

## O que o compose faz

Todo serviço usa o driver `json-file` com `max-size: 10m` e `max-file: 3` (âncora `x-logging` do `docker-compose.yml`, herdada
por `x-endurecido` e `x-one-shot`): no máximo **30 MB por serviço** (o arquivo corrente e dois rotacionados, `<id>-json.log.1` e
`.2`), e os mais antigos são descartados sozinhos. Sem isso o `json-file` cresceria sem limite. O `smoke-compose.sh` confere que
nenhum serviço ficou sem esse bloco.

## Quem guarda IP, e por quanto tempo

| Serviço | O que tem no log | IP |
|---|---|---|
| `web` (nginx) | access log em stdout (`access_anon`) | **anonimizado** na origem: último octeto IPv4 zerado, /64 em IPv6 (`infra/nginx/nginx.conf`) |
| `web` (nginx) | `error_log` em `warn` (`nginx.conf:2`), no stderr | **IP completo** (`client: 203.0.113.5`) a cada 429 de `limit_req`/`limit_conn`, justamente na hora de um ataque |
| `icecast` | só o error log (stderr); o access log vai para `/dev/null` | nenhum IP de ouvinte em claro |
| `api`, `worker` | JSON de aplicação | sem IP; ouvinte aparece só como hash |

O nível do log do nginx **não** foi alterado: o sinal dos 429 no `error_log` é útil, e o custo é a retenção abaixo. Se um dia
for preferível não ter o IP completo em log nenhum, `limit_req_log_level notice;` e `limit_conn_log_level notice;` no `http {}` tiram
essas linhas do `error_log warn` (o status 429 continua no access log anonimizado).

## A retenção de 14 dias depende de um passo no host

A rotação por **tamanho** limita o espaço, mas **não limita o tempo**: num dia de pouco acesso o arquivo corrente pode guardar
linhas por mais de 14 dias, e os dois rotacionados ficam no disco até uma rotação futura empurrá-los para fora. Por isso existe
`infra/scripts/limpar-logs.sh`: zera o log **corrente** do `web` e do `icecast` (truncando, o daemon o mantém aberto) e **apaga os
rotacionados** (`.1`, `.2`). Não derruba contêiner nem stream. Rodando **toda semana**, nenhuma linha com IP passa de 7 dias (folga
sobre os 14); o que garante isso é o script **instalado e agendado**, não o compose.

```bash
sudo infra/scripts/limpar-logs.sh --simular   # mostra o que faria
sudo infra/scripts/limpar-logs.sh             # limpa de verdade (precisa de root: /var/lib/docker/containers)
```

### Exemplo de `systemd.timer` (no host que roda o compose)

`/etc/systemd/system/webradio-limpar-logs.service`:

```ini
[Unit]
Description=WebRadio: retenção dos logs com IP (S-B04)

[Service]
Type=oneshot
# Ajuste o caminho do repositório. COMPOSE_PROJECT padrão: webradio.
ExecStart=/opt/webradio/infra/scripts/limpar-logs.sh
```

`/etc/systemd/system/webradio-limpar-logs.timer`:

```ini
[Unit]
Description=WebRadio: limpar logs com IP toda semana

[Timer]
OnCalendar=weekly
Persistent=true

[Install]
WantedBy=timers.target
```

```bash
sudo systemctl daemon-reload
sudo systemctl enable --now webradio-limpar-logs.timer
systemctl list-timers webradio-limpar-logs.timer     # confere a próxima execução
sudo systemctl start webradio-limpar-logs.service    # testa uma vez
```

**Sem esse timer (ou um equivalente) a retenção de 14 dias do S-B04 NÃO é cumprida**: só a rotação por tamanho está garantida
pelo compose. O passo é **operacional**: o script está no repositório, mas a instalação do timer é do host, e nenhum job do CI a
confere (registrado em "Riscos aceitos do B3" do `05`). Se um dia os logs forem para um coletor central (Loki, ELK), a retenção
de 14 dias passa a ser configurada lá e este passo sai.
