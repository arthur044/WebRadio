# Rotação de Segredos

> Procedimento operacional para renovar segredos em produção, minimizando downtime e evitando perda de acesso.

## Visão geral

WebRadio armazena segredos (chaves de criptografia, senhas de banco de dados, tokens) em:

- **Docker Secrets** (produção): arquivo `/run/secrets/<nome>` (read-only, permissões 0400)
- **`.env` (desenvolvimento)**: arquivo de configuração local (nunca versionado)

Cada serviço recebe apenas seus segredos. Nunca compartilhe um segredo entre serviços ou reutilize em produção.

## Segredos principais e rotação

| Segredo | Serviço | Tipo | Mínimo | Rotação |
|---------|---------|------|--------|---------|
| `Jwt__SigningKey` | API | chave HS256 | 32 bytes | **anual ou se exposto** |
| `Jwt__SigningKeyAnterior` | API | chave anterior | 32 bytes | manter 1 anterior durante rotação |
| `Playout__Token` | Liquidsoap + API | bearer token | 24 bytes | **anual ou se exposto** |
| `Playout__TokenAnterior` | API | token anterior | 24 bytes | manter 1 anterior durante rotação |
| `DB_APP_PASSWORD` | API + Worker | senha SQL | 24 bytes | **a cada 90 dias ou trienal** |
| `DB_MIGRATOR_PASSWORD` | migrator + db-init | senha SQL | 24 bytes | idem |
| `DB_RELATORIO_PASSWORD` | futuro (relatório) | senha SQL | 24 bytes | idem |
| `REDIS_PASSWORD` | Redis + API + Worker | senha Redis | 24 bytes | **anual ou se exposto** |
| `ICECAST_SOURCE_PASSWORD` | Icecast + Liquidsoap | senha mount | 24 bytes | **anual** |
| `ICECAST_ADMIN_PASSWORD` | Icecast | senha admin | 24 bytes | **trimestral** |
| `LIQUIDSOAP_HARBOR_PASSWORD` | Liquidsoap | senha harbor | 24 bytes | **anual** |
| `Seguranca__Pepper` | API | HMAC salt | 32 bytes | **nunca** (quebra todos os HMACs registrados) |

## Geração de novo segredo

### Contrato de alfabeto

Todo segredo gerado é **base64url sem padding**:
- Caracteres válidos: `[A-Za-z0-9_-]`
- Sem: apóstrofos, aspas, barras invertidas, dólar, backticks, ponto e vírgula, equals
- Comprimento: **≥ 24 caracteres** (F18 do Épico 1, §5 do `05-seguranca-auditoria.md`)
- Requisitos de composição: maiúscula + minúscula + dígito (aplicado em laço até sucesso)

### Usando `gerar-segredos.sh` ou `.ps1`

```bash
# Linux/macOS
./infra/scripts/gerar-segredos.sh

# Windows PowerShell
./infra/scripts/gerar-segredos.ps1
```

Os scripts:
- Criam um novo segredo com `openssl rand` e conversão base64url
- **Se recusam a sobrescrever** `.env` ou arquivo de secrets existente (segurança; deve-se editar manualmente)
- Registram um log com a data/hora da geração

**Não copie secredos de `.env.example`** — aquele arquivo contém apenas `__GERAR__` como placeholder.

## Rotação de senha SQL (exemplo: `DB_APP_PASSWORD`)

### Importante: `db-init` não reseta senhas

O serviço `db-init` cria os logins com as senhas do ambiente **na primeira execução**. Ao reexecutar `db-init`, ele **não reseta as senhas** dos logins já existentes. Portanto, para trocar a senha em produção, você deve:

1. Conectar ao SQL Server como **SA** (sysadmin) ou proprietário do login
2. Executar `ALTER LOGIN` **manualmente**
3. Atualizar o segredo no orquestrador (Docker Secrets, Kubernetes, etc.)
4. Reiniciar os serviços que dependem desse segredo

### Passo 1: Gerar nova senha

```bash
# Gere a nova senha
./infra/scripts/gerar-segredos.sh
# ou no PowerShell:
./infra/scripts/gerar-segredos.ps1

# Copie o valor da nova senha (ex.: aB-cD_123456789012345678)
# Confirme que tem >= 24 caracteres, maiúscula, minúscula e dígito
```

### Passo 2: Atualizar o login SQL

Conecte-se como **SA** ao SQL Server:

```sql
-- Conecte como SA com sqlcmd ou Management Studio
ALTER LOGIN webradio_app WITH PASSWORD = 'aB-cD_123456789012345678';

-- Verificar (SQL Server Management Studio; sqlcmd não retorna mensagem de sucesso)
-- SELECT name FROM sys.sql_logins WHERE name = 'webradio_app';
```

**Validações**:
- A senha deve obedecer a política do SQL (≥ 8 chars, 3 das 4 classes: maiúscula, minúscula, dígito, símbolo). A geração base64url atende isso.
- O caractere apóstrofo `'` é **proibido** no alfabeto gerado para evitar injeção SQL em scripts.
- Use `SQLCMDPASSWORD` ou `-P` com cuidado; prefira stdin se possível.

### Passo 3: Atualizar o segredo em produção

Você tem dois modos:

**a) Docker Secrets (recomendado, produção)**

```bash
# Remova o secret antigo
docker secret rm db_app_password

# Crie o novo secret
echo 'aB-cD_123456789012345678' | docker secret create db_app_password -

# Restart dos serviços que usam esse secret
docker service update --force api
docker service update --force worker
```

**b) `.env` (apenas desenvolvimento)**

Edite `.env`:

```env
DB_APP_PASSWORD=aB-cD_123456789012345678
```

Depois, reinicie os serviços:

```bash
docker compose restart api worker
```

### Passo 4: Validar acesso

Verifique que os serviços conseguem conectar ao banco:

```bash
# Dentro do contêiner da API
docker compose exec api curl -s http://localhost:8081/health/ready | jq .
# Deve retornar: { "status": "Healthy", ... }

# Ou via healthcheck
docker compose ps
# Status da `api` deve ser "healthy"
```

Se falhar, verifique os logs:

```bash
docker compose logs api | tail -50
```

Procure por:
- `Login failed for user 'webradio_app'` → senha incorreta no SQL ou no segredo
- `Connection timeout` → SQL Server inacessível ou firewall bloqueando
- `Named Pipes Provider` → protocolo desabilitado
- `Segurança SQL: A senha não atende à política` → senha fraca (improvável com base64url)

## Rotação de chave JWT (`Jwt__SigningKey`)

### Impacto

Todos os access tokens emitidos com a chave antiga se tornam inválidos. Usuários precisam fazer login novamente.

### Procedimento

1. **Gere a nova chave** (32 bytes = 256 bits):
   ```bash
   ./infra/scripts/gerar-segredos.sh
   # Novo valor: ex., aB-cD_123456789012345678901234567890...
   ```

2. **Mude a chave nova para `Jwt__SigningKey` e salve a antiga em `Jwt__SigningKeyAnterior`**:

   **Docker Secrets:**
   ```bash
   # Backup da chave atual (antes de remover)
   docker secret inspect jwt_signingkey > jwt_signingkey_backup.txt

   # Crie o novo secret com a chave anterior
   echo 'aB-cD_...(antigo)' | docker secret create jwt_signingkeyante -

   # Remova os antigos
   docker secret rm jwt_signingkey jwt_signingkeyante

   # Crie os novos (novo como signing, antigo como fallback)
   echo 'aB-cD_...(novo)' | docker secret create jwt_signingkey -
   echo 'aB-cD_...(antigo)' | docker secret create jwt_signingkeyante -
   ```

   **`.env` (desenvolvimento):**
   ```env
   Jwt__SigningKey=aB-cD_...(novo)
   Jwt__SigningKeyAnterior=aB-cD_...(antigo)
   ```

3. **Restart da API**:
   ```bash
   docker service update --force api
   # ou em dev:
   docker compose restart api
   ```

4. **Aguarde 15 minutos** para que todos os tokens antigos expirem (TTL do access token = 15 min).

5. **Depois, se quiser fazer outra rotação**, copie a chave nova para `Jwt__SigningKeyAnterior` e gere uma chave ainda mais nova:
   ```
   Jwt__SigningKey=aB-cD_...(NOVO novo)
   Jwt__SigningKeyAnterior=aB-cD_...(novo antigo)
   ```
   Isso permite que qualquer token emitido na transição ainda seja aceito.

## Rotação de token Playout (`Playout__Token`)

Similar à JWT, mas com impacto menor: afeta apenas o Liquidsoap e a fila de áudio.

1. Gere novo token (24+ bytes)
2. Defina `Playout__TokenAnterior = <token_antigo>`
3. Defina `Playout__Token = <token_novo>`
4. Restart do Liquidsoap e API
5. Aguarde 10 minutos, depois remova `Playout__TokenAnterior`

## Rotação de senha Redis

```bash
# 1. Gere a nova senha
./infra/scripts/gerar-segredos.sh

# 2. Atualize o secret
echo 'aB-cD_...' | docker secret create redis_password_new -
docker secret rm redis_password
docker secret create redis_password < redis_password_new.txt
rm redis_password_new.txt

# 3. Restart do Redis (ele lerá a nova senha do arquivo secret)
docker service update --force redis

# 4. Aguarde a reconexão dos clientes (API, Worker)
# Pode levar alguns segundos; observe os logs
docker compose logs api | tail -20
docker compose logs worker | tail -20
```

## Rotação de senhas Icecast e Liquidsoap

Estes são menos críticos, mas devem ser rotacionados regularmente:

```bash
# Gere novo segredo
./infra/scripts/gerar-segredos.sh

# Atualize o secret
echo 'aB-cD_...' | docker secret create icecast_source_password_new -
docker secret rm icecast_source_password
docker secret create icecast_source_password < icecast_source_password_new.txt

# Restart dos serviços (eles relerem o secret do arquivo)
docker service update --force icecast
docker service update --force liquidsoap

# Verifique o stream
curl -I http://localhost:8080/stream/radio.mp3
# Deve retornar: HTTP/1.1 200 OK
```

## Checklist de rotação

- [ ] Novo segredo gerado com `gerar-segredos.sh` ou `.ps1`
- [ ] Novo segredo segue o contrato (base64url, ≥ 24 bytes, mixto maiúscula/minúscula/dígito)
- [ ] Segredo antigo salvo em `*Anterior` (para chaves assimétricas como JWT/Playout)
- [ ] Secret/`.env` atualizado
- [ ] (SQL) `ALTER LOGIN` executado manualmente como SA se for senha de DB
- [ ] Serviço afetado reiniciado
- [ ] Health check confirmado (`/health/ready` = Healthy, stream toca, etc.)
- [ ] Logs verificados (sem erros de autenticação)
- [ ] Timeouts respeitados (JWT: 15 min, Playout: 10 min, Redis: imediato)
- [ ] Documento de auditoria atualizado com data/hora/resultado

## Troubleshooting

| Sintoma | Causa provável | Solução |
|---------|-----------------|---------|
| `Login failed for user 'webradio_app'` | Senha SQL incorreta no banco ou no secret | Verifique a cópia da nova senha; execute `ALTER LOGIN` novamente como SA |
| `Connection timeout` | SQL Server inacessível ou firewall | Verifique conectividade da rede `dados`; confira `sp_who2` no SQL para conexões ativas |
| `REDIS_ERROR: WRONGPASS` | Senha Redis incorreta | Atualize o secret e restart do Redis; aguarde reconexão |
| `Invalid token` após rotação JWT | Token emitido com chave antigo, e `Jwt__SigningKeyAnterior` não está preenchido | Preencha `Jwt__SigningKeyAnterior` com a chave velha; restart da API |
| Liquidsoap desconecta de Icecast | Senha `Playout__Token` ou `ICECAST_SOURCE_PASSWORD` errada | Verifique nos logs da API: `Invalid bearer token`; em Icecast: `auth failed` |
| `Failed to parse password alphabet` (entrypoint) | Segredo não segue base64url ou tem caracteres inválidos | Gere um novo segredo com os scripts; não copie valores não-base64url |

## Auditoria

Registre toda rotação em um log de operações:

- Data/hora (UTC) da mudança
- Qual segredo foi rotacionado
- Quem fez a mudança (seu nome/agente)
- Motivo (rotação periódica, vazamento, expiração, etc.)
- Resultado (sucesso, parcial com fallback, falha)

Exemplo:
```
2026-10-15T14:30:00Z | DB_APP_PASSWORD | Forge | Rotação trimestral | ✅ Sucesso, ambos serviços Healthy
2026-10-16T09:15:00Z | Jwt__SigningKey | Sentinel | Renovação anual | ✅ Sucesso, 15 min de espera concluído
```

---

**Referências:**
- [`../specs/04-epico-1-tarefas.md`](../specs/04-epico-1-tarefas.md) — Tarefas do Épico 1 (A02b)
- [`../specs/05-seguranca-auditoria.md`](../specs/05-seguranca-auditoria.md) — Política de segurança (§5)
- [`../scripts/gerar-segredos.sh`](../scripts/gerar-segredos.sh) e [`../scripts/gerar-segredos.ps1`](../scripts/gerar-segredos.ps1) — Scripts de geração

**Versão**: v1.0  
**Última atualização**: 2026-09-28
