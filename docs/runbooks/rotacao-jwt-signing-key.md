# Rotação da `Jwt__SigningKey` (chave de assinatura do access token)

Runbook da #10. Vale para o Compose de produção (`docker-compose.yml`, segredos em `secrets/<nome>`). A tabela 5.1 do [`05-seguranca-auditoria.md`](../specs/05-seguranca-auditoria.md) é a fonte normativa (rotação a cada 90 dias, ou na hora em caso de vazamento). O rascunho antigo, em [`rotacao-de-segredos.md`](rotacao-de-segredos.md), **não vale** para esta chave.

## O que a rotação faz (conferido no código)

- A API **assina** o access token só com `Jwt__SigningKey` e **valida** com ela e, se existir, com `Jwt__SigningKeyAnterior`. Cada chave tem um `kid` próprio (8 hex do SHA-256 dela), então um token antigo é verificado pela chave certa.
- O access token vale **15 minutos** (`EmissorDeTokens.DuracaoDoAccess`) e a validação tem tolerância de relógio de **30 s** (`ClockSkew`). Depois da troca, o último token assinado com a chave antiga expira em, no máximo, **15 min 30 s**.
- O **refresh é opaco** (aleatório, guardado como hash no banco): não depende da chave JWT. Quem perde o access token recebe um novo pelo refresh, e a sessão não cai. Com a chave anterior ligada, nem o 401 intermediário acontece durante a janela.
- A API recusa subir se `Jwt__SigningKey` não for base64url com 32 bytes ou mais, ou se `Jwt__SigningKeyAnterior` for igual à atual. Falha de boot aparece no log e o contêiner fica sem ficar `healthy`.

O Compose **não** tem o segredo da chave anterior por padrão. Durante a rotação ele entra por um arquivo de override **fora do git** (o mesmo padrão do override de limite do [`worker-degradado.md`](worker-degradado.md)).

## Antes de começar

- Decida o motivo: **rotação programada** (com chave anterior, sem derrubar ninguém) ou **vazamento** (sem chave anterior; ver a seção própria).
- Tenha acesso ao host, na raiz do projeto, com permissão de escrever em `secrets/`.
- Não imprima, cole em chat nem registre o conteúdo de `secrets/jwt_signing_key*`.

## Por que `--force-recreate` em todo `up` desta rotação

O Compose decide recriar um contêiner comparando a **configuração** do serviço, não o **conteúdo** do arquivo do segredo. Trocar o conteúdo de `secrets/jwt_signing_key` sem mudar o YAML não muda a configuração: `docker compose up -d api` responde `Running` e a API **continua lendo a chave antiga** (reproduzido pelo Sentinel no Compose v5.8.0). Por isso todo `up` abaixo leva `--force-recreate`, e cada um tem uma conferência de que o contêiner nasceu depois da troca:

```bash
# hora de início do contêiner da API (UTC): precisa ser POSTERIOR à hora anotada no passo da troca
docker inspect -f '{{.State.StartedAt}}' "$(docker compose ps -q api)"
```

## Rotação programada (com chave anterior)

1. **Guarde a chave atual como anterior** (mover, não copiar: não sobra duas cópias da mesma chave com nomes diferentes):

   ```bash
   mv secrets/jwt_signing_key secrets/jwt_signing_key_anterior
   ```

   O `mv` mantém o modo do arquivo (644, por desenho do `gerar-segredos.sh`: os serviços sem root precisam ler). Quem protege é o diretório `secrets/`, que é 700.

2. **Gere a chave nova.** O script nunca sobrescreve: para cada segredo que já existe ele imprime `secrets/<nome> já existe — preservado.` e só cria o que falta. Num ambiente completo, **só a `jwt_signing_key`** é criada:

   ```bash
   infra/scripts/gerar-segredos.sh prod
   ```

   Se ele criar **outro** segredo além da `jwt_signing_key`, é porque esse arquivo faltava no ambiente: pare e descubra por quê antes de seguir. Confira o resultado sem ler o conteúdo: `ls -l secrets/jwt_signing_key secrets/jwt_signing_key_anterior`.

3. **Crie o override da rotação**, `docker-compose.rotacao-jwt.yml`, na raiz (fora do git; o `.gitignore` já o cobre):

   ```yaml
   services:
     api:
       secrets:
         - { source: jwt_signing_key_anterior, target: Jwt__SigningKeyAnterior }
   secrets:
     jwt_signing_key_anterior: { file: ./secrets/jwt_signing_key_anterior }
   ```

   Confira que o Compose juntou os dois: `docker compose -f docker-compose.yml -f docker-compose.rotacao-jwt.yml config | grep -E 'Jwt__SigningKey'` deve listar `Jwt__SigningKey` e `Jwt__SigningKeyAnterior`.

4. **Anote a hora (UTC)** (`date -u +%FT%TZ`) e **recrie só a API** com o override: é daqui que a espera conta.

   ```bash
   docker compose -f docker-compose.yml -f docker-compose.rotacao-jwt.yml up -d --force-recreate --wait api
   ```

5. **Confira**: `docker compose ps api` = `healthy`; o `StartedAt` (comando acima) é posterior à hora anotada; o login funciona com a chave nova. Uma sessão já aberta no navegador continua funcionando (o token antigo é validado pela anterior).

6. **Espere 16 minutos** a partir do passo 4 (15 min de vida do token + 30 s de tolerância, com 30 s de margem).

7. **Tire a chave anterior.** Suba a API sem o override, e só então apague os dois arquivos da rotação:

   ```bash
   date -u +%FT%TZ   # anote
   docker compose up -d --force-recreate --wait api
   rm docker-compose.rotacao-jwt.yml secrets/jwt_signing_key_anterior
   ```

   A chave antiga já não valida nada a esta altura, então um `rm` simples basta.

8. **Confira de novo**: `healthy`, `StartedAt` posterior à hora do passo 7, o login funciona e `docker compose config | grep -c SigningKeyAnterior` imprime `0`.

9. **Registre**: data e hora UTC, motivo, quem fez e o resultado.

## Vazamento da chave (rotação sem chave anterior)

Quem tem a chave forja um access token de **qualquer** usuário, inclusive Admin, até ela sair de circulação. Por isso, no vazamento **não se liga a chave anterior**: a chave vazada deixa de valer no momento em que a API sobe com a nova. A claim `sv` (versão de segurança) **não protege** aqui: `sub` e `sv` são fáceis de obter, e quem tem a chave assina o que quiser.

1. **Anote a hora (UTC)** (`date -u +%FT%TZ`) e troque a chave:

   ```bash
   rm secrets/jwt_signing_key        # comprometida: não há o que preservar
   infra/scripts/gerar-segredos.sh prod   # só a jwt_signing_key deve ser criada (ver o passo 2 da rotação programada)
   ```

2. **Recrie a API**, com `--force-recreate` (sem ele a API segue com a chave vazada, ver acima):

   ```bash
   docker compose up -d --force-recreate --wait api
   ```

3. **Confirme que o contêiner novo é o que está no ar**: `StartedAt` posterior à hora do passo 1 (comando da seção anterior) e `healthy`. Só depois disso a chave vazada deixou de valer.
4. Todos os access tokens somem na hora. Os clientes recebem 401, refazem o refresh (que não depende da chave) e voltam sem novo login. Os 401 dessa hora são **clientes legítimos**: o pico é esperado e **não** indica token forjado.
5. **Audite o que um token forjado pode ter feito** entre a hora do vazamento (ou a mais antiga que se suspeite; na dúvida, desde a emissão da chave) e o passo 2. Um token forjado de Admin ou Locutor faz tudo o que o papel permite; procure e **revogue ou desfaça o que não reconhecer**:
   - **Contas e papéis**: usuários criados (`POST /api/v1/usuarios`), papel alterado ou conta ativada/desativada (`PATCH /api/v1/usuarios/{id}`), desbloqueios de login (`POST /api/v1/usuarios/{id}/desbloquear-login`). Confira no banco os Admins e Locutores existentes e o `CriadoEmUtc` deles.
   - **Credenciais de transmissão**: geradas ou revogadas (`/api/v1/locutores/me/credencial-transmissao` e `/api/v1/usuarios/{id}/credencial-transmissao`). Credencial que ninguém reconhece: revogue (quem transmite ao vivo é quem tem uma).
   - **Conteúdo**: mídias (`/api/v1/midias`), divulgações (`/api/v1/divulgacoes`) e programas (`/api/v1/programas`) criados, editados ou apagados; pedidos aprovados ou rejeitados (`/api/v1/pedidos/...`).
   - **Como achar**: o log da Api registra método, caminho (sem a query) e status de cada requisição, mas **não** quem a fez. Liste as escritas na janela e compare com o que a equipe fez: `docker compose logs --since <hora> api | grep -E '"RequestMethod":"(POST|PUT|PATCH|DELETE)"'`. O log guarda **até 14 dias, mas o teto real é de 30 MB por serviço** (`json-file` com 10 MB × 3 arquivos; ver `docs/operacao-logs.md`): com tráfego, a janela do incidente pode já ter saído do log. **Copie o log da Api para fora do contêiner logo no início da resposta** (`docker compose logs --no-color api > api-incidente.log`, em arquivo de acesso restrito) e, passado o que o log guarda, só o banco sobra.
6. Se o vazamento veio junto com acesso ao banco ou a cookies, avalie também revogar as sessões (troca de senha do usuário ou desativação) e rotacionar o `Seguranca__Pepper` só se o Sentinel mandar (a tabela 5.1 do `05` diz o custo).
7. Registre o incidente (hora do vazamento, hora da troca, o que foi encontrado na auditoria) e avise o Sentinel.

## Voltar atrás

Se a API não subir ou o login falhar logo depois do passo 4, **antes** de apagar a anterior:

```bash
date -u +%FT%TZ   # anote
mv secrets/jwt_signing_key_anterior secrets/jwt_signing_key
docker compose up -d --force-recreate --wait api
rm -f docker-compose.rotacao-jwt.yml
```

Confira o `StartedAt` posterior à hora anotada. Isso volta à chave antiga (que ainda assina e valida) e a rotação pode ser refeita depois. Depois do passo 7, a chave antiga já não existe: não há volta, só uma rotação nova.

## Erros comuns

| Sintoma | Causa | O que fazer |
|---|---|---|
| A API não fica `healthy` e o log diz que `Jwt:SigningKeyAnterior` é igual à `Jwt:SigningKey` | O passo 1 copiou em vez de mover, ou o passo 2 não gerou chave nova | Gere de fato uma chave nova (apague `secrets/jwt_signing_key`, rode o `gerar-segredos.sh prod`) |
| A API não fica `healthy` e o log diz que `Jwt:SigningKey` tem menos de 32 bytes ou não é base64url | Arquivo vazio ou editado à mão | Apague o arquivo e gere de novo com o script |
| Muitos 401 logo depois da troca, mesmo com a anterior ligada | O override não entrou (esqueceu o `-f docker-compose.rotacao-jwt.yml`) | Refaça o passo 3 e o `config`; suba a API de novo com os dois `-f` |
| `docker compose` reclama que `secrets/jwt_signing_key_anterior` não existe | Arquivo apagado antes de tirar o override do `up` | Suba sem o override (passo 7) |
| Troquei o arquivo e a API continua aceitando a chave antiga (ou o login não muda) | O `up` sem `--force-recreate` não recria o contêiner: o Compose não vê mudança no conteúdo do segredo | `docker compose up -d --force-recreate --wait api` e confira o `StartedAt` |
