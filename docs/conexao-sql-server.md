# Guia de conexão e pool — SQL Server (WebRadio)

> Autor: Atlas (DBA) · tarefa E1-A05 · 2026-09-27
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

- `Encrypt=True` sempre, em todo ambiente — sem exceção.
- `TrustServerCertificate=True` **só** no `docker-compose.dev.yml`: a imagem do SQL
  Server no contêiner usa certificado autoassinado, e sem essa flag a conexão falha em
  dev. Em produção fica **`False`** (padrão) — um certificado não confiável em produção
  deve derrubar a conexão (fail-closed), não silenciar um possível MITM entre `api`/
  `worker` e o `sqlserver` dentro da rede `dados`.
- Isso é obrigatório em ambos os arquivos de compose de forma **explícita** (nunca
  confiar no default do driver, que pode mudar entre versões do `Microsoft.Data.SqlClient`).

## 3. `Application Name` (identificação de sessão)

Cada serviço usa um `Application Name` distinto, para diferenciar sessões em
`sys.dm_exec_sessions.program_name` (diagnóstico de bloqueio/performance sem precisar
cruzar com IP/porta de origem):

| Serviço | `Application Name` |
|---|---|
| `api` | `WebRadio.Api` |
| `worker` | `WebRadio.Worker` |
| `migrator` | `WebRadio.Migrator` |
| `db-init` | `WebRadio.DbInit` |

Consulta de diagnóstico típica: `SELECT program_name, COUNT(*) FROM sys.dm_exec_sessions GROUP BY program_name;`

## 4. Pool: `Max Pool Size` / `Min Pool Size` / `Connect Timeout`

| Serviço | Max Pool Size | Min Pool Size | Connect Timeout | Por quê |
|---|---|---|---|---|
| `api` | 50 | 0 | 10 | Web app com concorrência variável; 50 evita exaustão de conexões no `sqlserver` de container (recursos limitados) sem sufocar picos normais do Épico 1 |
| `worker` | 20 | 0 | 10 | Um processo, laço `PeriodicTimer` de 10 s + jobs — não precisa de pool grande |
| `migrator` | 5 | 0 | 30 | One-shot; `Connect Timeout` maior tolera o `sqlserver` ainda inicializando logo após o healthcheck virar `healthy` |
| `db-init` | 5 | 0 | 30 | Idem, one-shot, primeira conexão do stack |

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
# TrustServerCertificate: True só existe no docker-compose.dev.yml (nunca no .yml base/prod)
```

## 6. Checklist de aceite (E1-A05)

- [ ] `Encrypt=True` em toda connection string, todo ambiente.
- [ ] `TrustServerCertificate=True` presente **só** no override de dev.
- [ ] `Application Name` distinto por serviço (tabela §3), visível em `sys.dm_exec_sessions`.
- [ ] Pool dimensionado por serviço conforme §4 (não usar o mesmo valor para `api` e `migrator`).
- [ ] Senha montada em runtime a partir de `/run/secrets` — nunca literal na connection string versionada.
