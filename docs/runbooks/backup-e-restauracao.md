# Backup e restauração (restic)

Runbook do E5-F03 (decisões E5-D3, D4, D10, D11 e D13 do [`09-epico-5-tarefas.md`](../specs/09-epico-5-tarefas.md)). Vale para o Compose de produção. As identidades e permissões estão em [`05-seguranca-auditoria.md`](../specs/05-seguranca-auditoria.md) (S-A04 e §5.1).

## O que roda

O serviço `backup` (perfil `backup`, **não** sobe no `up` comum) faz, uma vez por dia, em ordem, parando na primeira falha:

| Etapa | O que faz | Código se falhar |
|---|---|---|
| `sql` | `BACKUP DATABASE ... WITH CHECKSUM` pelo login `svc_backup` (`infra/sql/backup.sql`) | 10 |
| `midia` | `mc mirror` dos buckets `midia` e `publico` para `/backup/espelho` (a **quarentena fica de fora**, de propósito) | 20 |
| `envio` | `restic backup` do `.bak` e do espelho para o repositório **local** (criptografado, no volume `backup-local`) e, **se a cópia externa estiver ligada**, `restic copy` para o repositório externo (S3) | 30 |
| `retencao` | `restic forget --keep-daily 7 --keep-weekly 4 --keep-monthly 6 --prune` (local e, se ligado, externo) | 40 |
| `check` | `restic check` aos domingos (local e, se ligado, externo) | 50 |

O resultado é uma linha no log: `BACKUP_OK duracao_s=... repo_local_kib=... externo=sim|nao`, ou `BACKUP_FALHOU etapa=<...> [destino=local|externo] motivo=<token>`. O `motivo` é um token fixo (`credencial_ou_permissao_recusada`, `sem_conexao`, `sem_espaco`, `destino_inexistente`, `repositorio_travado`, `dado_inconsistente`, `desconhecido`); a saída crua dos programas **não** vai para o log (pode ter host, caminho e pedaço de segredo). Para investigar, rode à mão com `-e BACKUP_DEBUG=1` e **não cole a saída em lugar nenhum**.

Códigos 2 (segredo ou configuração ausente, `etapa=config`) e 3 (outro backup rodando) também existem.

**Perda máxima: 1 dia** (o banco está em `RECOVERY SIMPLE`; E5-D3). A troca por `FULL` com `BACKUP LOG` de hora em hora é uma decisão do dono do produto (E5-N01).

## Ligar o backup diário

```bash
sudo cp infra/backup/systemd/webradio-backup.* /etc/systemd/system/    # ajuste WorkingDirectory no .service (a raiz do projeto)
sudo systemctl daemon-reload && sudo systemctl enable --now webradio-backup.timer
systemctl list-timers webradio-backup.timer                            # próximo disparo: 03:30, no horário do servidor
```

Rode uma vez à mão antes de confiar no timer: `docker compose --profile backup run --rm backup`, e **depois pela unidade**: `sudo systemctl start webradio-backup.service` e confira `journalctl -u webradio-backup -n 30` com `BACKUP_OK`. O endurecimento da unidade (`ProtectHome`, `PrivateTmp`, `ProtectSystem`) só vale quando o backup roda por ela: um erro de permissão aparece ali e não na execução à mão. Sem a cópia externa, o fim do log traz `BACKUP_EXTERNO_NAO_CONFIGURADO` (**aviso, não erro**; código 0): só há cópia local, e a perda do servidor perde o backup junto.

## Ligar a cópia externa (depois, sem mudar código)

1. No provedor (Backblaze B2, Wasabi, AWS S3 ou um MinIO em outro servidor): um **bucket só para isso**, com **versionamento ligado** e uma regra de ciclo de vida que **mantém versões apagadas ou sobrescritas por 30 dias** (E5-D11). A chave do backup **não** pode alterar versionamento, ciclo de vida nem object lock: com isso, quem invadir o servidor e apagar tudo deixa as versões recuperáveis pela conta do provedor.
2. No `.env`: `BACKUP_S3_ENDPOINT=https://s3.exemplo.com` (**só `https://`**: com `http://` o backup e o restaurar recusam, `etapa=config`, para as chaves e os dados não irem em claro; sem o bucket no fim) e `BACKUP_S3_BUCKET=nome-do-bucket`.
3. Cole as chaves nos arquivos `secrets/backup_s3_access_key` e `secrets/backup_s3_secret_key` (nascem **vazios**; sem newline no fim: `printf '%s' 'CHAVE' > secrets/backup_s3_access_key`).
4. `docker compose --profile backup run --rm backup`: o log passa a ter `BACKUP_ENVIO_OK destino=externo` e o aviso some. O repositório externo nasce com os mesmos parâmetros do local (`--copy-chunker-params`) e usa a **mesma** `restic_senha`.

## Guardar fora do servidor (obrigatório, antes da primeira subida)

- **`secrets/restic_senha`**: sem ela **nenhum** backup abre, nem o local nem o externo. Guarde-a no gerenciador de senhas do dono do produto.
- `secrets/seguranca_pepper` (trocá-lo zera hashes de IP, pseudônimos de dispositivo e sessões) e, se a cópia externa estiver ligada, as chaves `backup_s3_*`, o endpoint e o bucket.
- Recomendado: a pasta `secrets/` inteira. **Regeneráveis** na restauração (todo mundo loga de novo): senhas dos logins do SQL, chaves do MinIO, chave do JWT.

## Restaurar (servidor novo ou banco/biblioteca perdidos)

**Num servidor novo, deixe o `webradio-backup.timer` DESLIGADO (e não rode o `backup`) até a restauração terminar.** Um backup antes dela grava um snapshot de banco VAZIO, que vira o `latest` e a fonte padrão: o restaurar mostra o snapshot (id, data, host) antes de pedir o nome do banco; confira que ele é o que você espera. Se o `latest` for o errado, passe o id: `RESTAURAR_SNAPSHOT=<id>`.

Pré-requisitos: o Compose no servidor, os segredos de volta em `secrets/` (a `restic_senha` é a mesma) e, se for um servidor novo, o repositório local **não existe**: a restauração vai ao externo (precisa da cópia externa ligada e das chaves `backup_s3_*`).

```bash
docker compose up -d --wait sqlserver minio                    # (primeira vez em servidor novo: o db-init e o minio-init rodam como dependência)
docker compose stop api worker                                  # OBRIGATÓRIO: o restaurar.sh recusa se houver sessão da api ou do worker no SQL Server
docker compose --profile restore run --rm restaurar             # pergunta o nome do banco; digite WebRadio
docker compose up -d --wait                                     # migrator, api e worker, nessa ordem
```

O `restaurar` faz, nesta ordem (E5-D13): **trava** (aborta se a api ou o worker estiverem no ar, código 21) → confirmação do nome do banco (22 se diferente) → baixa o `.bak` e o espelho do restic (`RESTAURAR_FONTE=local|externo`, padrão: local se existe, senão externo; `RESTAURAR_SNAPSHOT=<id>` para um ponto específico, padrão `latest`) → `restore.sql` com o `sa` (`VERIFYONLY`, `SINGLE_USER`, `RESTORE ... WITH CHECKSUM, REPLACE`, `MULTI_USER`) → `pos-restore.sql` (religa cada usuário do banco ao login: num servidor novo os logins nascem com outro SID) → `mc mirror` da mídia de volta, com a credencial root (o banco vem **antes** da mídia). Ele é o **único** serviço, além do `db-init` e do `minio-init`, com o `sa` e a credencial root do MinIO (exceção aceita no `05` S-A04).

Depois, confira: login de um Admin, a grade, a biblioteca com pré-escuta e uma música da rotação tocando (DoD 5).

## Depois de um incidente (invasão suspeita): restaure o snapshot ANTERIOR à suspeita

Se a restauração é por causa de uma invasão ou de uma suspeita dela, **não** use o `latest`. Um `.bak` feito **depois** da invasão pode trazer papel, permissão ou usuário plantados dentro do banco, e o `pos-restore.sql` só barra o que ele conhece (usuário órfão sem login; ele não audita papéis nem permissões). Escolha o último snapshot **de antes** da data em que a suspeita começa:

```bash
docker compose --profile restore run --rm --no-deps --entrypoint restic restaurar -r /backup/repo --password-file /run/secrets/restic_senha snapshots    # ou o repositório externo
docker compose --profile restore run --rm -e RESTAURAR_SNAPSHOT=<id-do-snapshot-anterior> restaurar
```

E, no servidor que foi invadido, **não reaproveite os segredos**: gere de novo as senhas dos logins do SQL, as chaves do MinIO e a chave do JWT ([`rotacao-jwt-signing-key.md`](rotacao-jwt-signing-key.md)); a `restic_senha` e as chaves `backup_s3_*` entram na avaliação do Sentinel (quem tomou o servidor leu os arquivos de `secrets/`). A cópia externa com versionamento (E5-D11) é o que garante que ainda existe um snapshot anterior. Este aviso também vale para o guia de deploy (E5-L01).

## Teste de restauração mensal

Uma vez por mês, no servidor em produção, **sem** parar nada: restaura o último `.bak` num banco temporário `WebRadio_verif`, roda `DBCC CHECKDB`, conta as linhas e apaga o temporário:

```bash
docker compose exec -T sqlserver bash -c 'SQLCMDPASSWORD="$(cat /run/secrets/mssql_sa_password)" /opt/mssql-tools18/bin/sqlcmd -C -N -S localhost -U sa -b -I -i /dev/stdin' < infra/sql/verificar-restauracao.sql
```

Procure `VERIFICACAO_OK`. E de tempos em tempos (a cada trimestre), faça a restauração **completa** num servidor descartável: é o único teste de que a `restic_senha` guardada fora funciona.

## Operação

```bash
docker compose --profile restore run --rm --no-deps --entrypoint restic restaurar -r /backup/repo --password-file /run/secrets/restic_senha snapshots   # snapshots locais
docker compose --profile backup run --rm backup                                                                                                         # backup agora (faça antes de toda atualização com migration, E5-D8)
```

O repositório local e o espelho ficam no volume `backup-local`; o `.bak` do dia, no `sql-backup`. **Disco:** a biblioteca existe no MinIO, no espelho e (deduplicada) no repositório local: planeje ~3× o tamanho dela, mais o `.bak` e o histórico. Nunca `docker compose down -v` em produção: apaga o `backup-local` (o backup que existe sem a cópia externa).

## Erros comuns

| Sintoma | Causa | O que fazer |
|---|---|---|
| `BACKUP_FALHOU etapa=sql motivo=credencial_ou_permissao_recusada` | A senha do `svc_backup` do arquivo `secrets/sql_backup_senha` não é a do login no SQL Server | O `db-init` **não** troca a senha de um login que já existe (só cria). Ou volte o segredo antigo, ou ajuste o login com o `sa`: `docker compose exec -T sqlserver bash -c 'SQLCMDPASSWORD="$(cat /run/secrets/mssql_sa_password)" /opt/mssql-tools18/bin/sqlcmd -C -N -S localhost -U sa -b -Q "ALTER LOGIN svc_backup WITH PASSWORD = N'"'"'<a senha do arquivo>'"'"'"'` (a senha só tem `[A-Za-z0-9_-]`) |
| `etapa=midia motivo=credencial_ou_permissao_recusada` | A chave do `svc-backup` no MinIO não bate com `secrets/minio_backup_*` | `docker compose run --rm minio-init` (recria as identidades) |
| `etapa=envio destino=externo motivo=sem_conexao` | Storage externo fora do ar, endpoint errado ou sem saída de rede | Confira `BACKUP_S3_ENDPOINT` (com `https://`) e o DNS do servidor; o backup **local** desta rodada já está gravado |
| `etapa=envio motivo=credencial_ou_permissao_recusada` | Chave do S3 errada, sem permissão no bucket, ou `restic_senha` diferente da do repositório | Confira as chaves e a senha; **nunca** crie um repositório novo por cima de um existente |
| `etapa=check motivo=dado_inconsistente` | Repositório danificado (disco, pack apagado) | Não apague nada; rode `restic check` à mão e `restic repair index`/`snapshots` conforme a saída; a cópia externa é o backup do backup |
| `RESTAURACAO_ABORTADA motivo=api_ou_worker_no_ar` | Há sessão da api ou do worker no SQL Server | `docker compose stop api worker`, espere ~10 s e rode de novo |
| `RESTAURACAO_ABORTADA motivo=backup_em_andamento` (código 23) | O backup (timer das 03:30 ou manual) está rodando e segura o mesmo lock do volume `backup-local` | Espere o `BACKUP_OK` e rode de novo |
