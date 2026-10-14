# Guia de conexão e pool — SQL Server (WebRadio)

> Autor: Atlas (DBA) · tarefa E1-A05 · 2026-09-27 · Atualizado em 2026-10-14 (B3-03)
> Público: Forge (configuração de `SqlConnection`/EF Core em `WebRadio.Infrastructure`).
> Não redefine segredos: a senha de cada principal (`webradio_app`, `webradio_migrator`,
> `webradio_relatorio`) vem de arquivo (`/run/secrets`, S-A10) e é montada em runtime,
> nunca gravada aqui nem num único segredo copiado entre serviços
> (docs/specs/05-seguranca-auditoria.md §5.3). Os logins são criados pelo `db-init`
> via `infra/sql/logins.sql` (E1-A02).

## 1. Regra geral

A connection string é montada em runtime a partir de config + arquivo de segredo, nunca
como uma única string literal num `.env`:

```
Server=sqlserver;Database=WebRadio;User Id=<principal>;Password=<lido do arquivo>;
Encrypt=True;TrustServerCertificate=<ver §2>;Application Name=<ver §3>;
Max Pool Size=<ver §4>;Min Pool Size=0;Connect Timeout=10
```

## 2. `Encrypt` / `TrustServerCertificate`

- `Encrypt=True` sempre, em todo ambiente e em todo cliente do SQL Server (Api, Worker, migrator e
  `sqlcmd` do db-init) — sem exceção. É o gatilho (iii) abaixo.
- `TrustServerCertificate=True` vale em dev **e, por enquanto, em produção**. A imagem do SQL Server
  no contêiner usa certificado autoassinado e, sem a flag, a conexão falha. Em produção isso é um
  **risco aceito e registrado** (decisão do dono do produto em 2026-10-13, posição do Sentinel no
  PR #30): a conexão é cifrada, mas o cliente não autentica o servidor, então um MITM ativo dentro
  da rede `dados` não seria detectado.
- O risco só é aceito **enquanto os três gatilhos valerem**:
  1. a rede `dados` é `internal` e de um único host (sem Swarm, sem Kubernetes, sem multi-host);
  2. nenhum contêiner da `dados` tem `NET_RAW`, `NET_ADMIN` ou `privileged` (que devolve as duas
     mesmo com `cap_drop: [ALL]`), nem `network_mode: host` nem `pid: host`, e todos têm
     `cap_drop: [ALL]`. O `infra/scripts/smoke-compose.sh` confere isso sobre o `docker compose config`
     da base;
  3. `Encrypt=True` fica fixo (nunca `Encrypt=False`).
- **Se um gatilho cair, o certificado confiável vira pré-requisito** e `TrustServerCertificate`
  volta a `False` em produção (fail-closed). Desenho de referência (PR #30): CA própria gerada pelo
  `gerar-segredos`; certificado do `sqlserver` com SAN `sqlserver`; `network.tlscert` e
  `network.tlskey` no `mssql.conf` (chave como Docker secret); a parte pública da CA montada nos
  clientes com `SSL_CERT_FILE`/`SSL_CERT_DIR`; rotação no runbook de segredos. Validar a conexão do
  `efbundle` e a do `sqlcmd` (o `-C` do db-init sai nesse dia).
- O valor de `TrustServerCertificate` é o **mesmo** em api, worker e migrator, e fica sempre
  explícito (nunca confiar no default do driver): Api e Worker no `ConnectionStrings__RadioDb` do
  compose; migrator em `DB_TRUST_SERVER_CERTIFICATE` (obrigatória, sem valor padrão no
  `entrypoint.sh`); o db-init usa `sqlcmd -C`. O smoke compara os três primeiros.

## 3. `Application Name` (identificação de sessão)

Cada serviço se identifica de um jeito próprio, para diferenciar sessões em
`sys.dm_exec_sessions` (diagnóstico de bloqueio e performance sem cruzar IP/porta de origem):

| Serviço | Como aparece em `sys.dm_exec_sessions` | Onde é definido |
|---|---|---|
| `api` | `program_name = WebRadio.Api` | `NomesDeAplicacao.Api` (código) |
| `worker` | `program_name = WebRadio.Worker` | `NomesDeAplicacao.Worker` (código) |
| `worker` (liderança) | `program_name = WebRadio.Worker.Lideranca` | `LiderancaDoWorker.NomeDaAplicacao`: conexão dedicada, sem pool, que segura o `sp_getapplock` de sessão |
| `migrator` | `program_name = WebRadio.Migrator` | `infra/migrator/entrypoint.sh` |
| `db-init` | `host_name = WebRadio.DbInit` (o `program_name` é `SQLCMD`) | `sqlcmd -H` no `infra/db-init/entrypoint.sh` |

O `sqlcmd` fixa `program_name = SQLCMD` e não deixa trocá-lo (confirmado na execução registrada no
commit `cb32deb`: `APP_NAME()=SQLCMD` com `-H WebRadio.DbInit`); por isso o db-init se identifica pelo
`host_name`. Os demais serviços se filtram por `program_name`; o `host_name` deles não é definido por
este projeto. O db-init é one-shot: a sessão só existe enquanto ele roda.

Consultas de diagnóstico:

```sql
SELECT program_name, COUNT(*) FROM sys.dm_exec_sessions
WHERE program_name LIKE N'WebRadio.%' GROUP BY program_name;

SELECT session_id, login_name, status FROM sys.dm_exec_sessions
WHERE host_name = N'WebRadio.DbInit';
```

## 4. Pool: `Max Pool Size` / `Min Pool Size` / `Connect Timeout`

| Serviço | Max Pool Size | Min Pool Size | Connect Timeout | Por quê |
|---|---|---|---|---|
| `api` | 50 | 0 | 10 | Web app com concorrência variável; 50 evita exaustão de conexões no `sqlserver` de container (recursos limitados) sem sufocar picos normais do Épico 1 |
| `worker` | 20 | 0 | 10 | Um processo, laço `PeriodicTimer` de 10 s + jobs — não precisa de pool grande |
| `migrator` | 5 | 0 | 30 | One-shot; `Connect Timeout` maior tolera o `sqlserver` ainda inicializando logo após o healthcheck virar `healthy` |
| `worker` (liderança) | — (`Pooling=false`) | — | 10 | Uma conexão dedicada e fixa por processo: o `sp_getapplock` de sessão morre com a conexão, então ela não pode voltar ao pool |
| `db-init` | — (o `sqlcmd` não usa pool) | — | 30 (`-l 30`) | One-shot, uma conexão por execução, primeira conexão do stack; o `Connect Timeout` maior tolera o `sqlserver` ainda inicializando |

`Min Pool Size=0` em todos: nenhum serviço precisa manter conexões ociosas abertas fora
de uso — o Épico 1 não tem carga constante que justifique aquecer o pool.

Não configurar `ConnectRetryCount`/`ConnectRetryInterval` do `Microsoft.Data.SqlClient`
além do padrão do driver: isso é resiliência de **conexão ociosa** (nível de socket) e é
ortogonal ao `EnableRetryOnFailure` do EF Core (E1-F04), que retenta **comando**/transação
após falha transitória. Empilhar os dois sem necessidade mascara problemas reais de rede
em vez de corrigi-los.

## 5. Valores não-secretos para `.env.example` (Forge, E1-F16)

Segredos (senhas) seguem o padrão `__GERAR__` de `docs/specs/05-seguranca-auditoria.md §5.2`
e não entram aqui. Valores fixos/não-secretos que a connection string de cada serviço
consome:

```
DB_SERVER=sqlserver
DB_NAME=WebRadio
DB_ENCRYPT=True
# TrustServerCertificate: True em dev e, por enquanto, em produção (risco aceito com gatilhos, §2).
# Api e Worker: no ConnectionStrings__RadioDb do compose. Migrator: DB_TRUST_SERVER_CERTIFICATE (obrigatória).
```

## 6. Checklist de aceite (E1-A05)

- [ ] `Encrypt=True` em toda connection string e no `sqlcmd` do db-init, em todo ambiente (gatilho iii).
- [ ] `TrustServerCertificate` com o **mesmo** valor em api, worker e migrator, sempre explícito; `True` em
      produção só enquanto valerem os três gatilhos da §2 (o `smoke-compose.sh` confere o valor igual e o
      gatilho ii).
- [ ] Gatilhos conferidos a cada mudança de compose: (i) `dados` `internal` e host único; (ii) nenhum
      contêiner da `dados` com `NET_RAW`/`NET_ADMIN`/`privileged`/`network_mode: host`/`pid: host`;
      (iii) sem `Encrypt=False`.
- [ ] Se um gatilho cair: certificado confiável (§2) **antes** do merge, e `TrustServerCertificate=False`.
- [ ] Identificação de sessão por serviço conforme a tabela da §3 (inclui a liderança do Worker; o db-init por `host_name`).
- [ ] Pool dimensionado por serviço conforme §4 (não usar o mesmo valor para `api` e `migrator`).
- [ ] Senha montada em runtime a partir de `/run/secrets` — nunca literal na connection string versionada.
