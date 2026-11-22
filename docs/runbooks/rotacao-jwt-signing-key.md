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

## Rotação programada (com chave anterior)

1. **Guarde a chave atual como anterior** (mover, não copiar: não sobra duas cópias da mesma chave com nomes diferentes):

   ```bash
   umask 077
   mv secrets/jwt_signing_key secrets/jwt_signing_key_anterior
   ```

2. **Gere a chave nova.** O script só cria o que falta e nunca sobrescreve, então só a `jwt_signing_key` é gerada:

   ```bash
   infra/scripts/gerar-segredos.sh prod
   ```

   Confira que o arquivo voltou e tem o modo certo (sem ler o conteúdo): `ls -l secrets/jwt_signing_key secrets/jwt_signing_key_anterior`.

3. **Crie o override da rotação**, `docker-compose.rotacao-jwt.yml`, na raiz (fora do git):

   ```yaml
   services:
     api:
       secrets:
         - { source: jwt_signing_key_anterior, target: Jwt__SigningKeyAnterior }
   secrets:
     jwt_signing_key_anterior: { file: ./secrets/jwt_signing_key_anterior }
   ```

   Confira que o Compose juntou os dois: `docker compose -f docker-compose.yml -f docker-compose.rotacao-jwt.yml config | grep -E 'Jwt__SigningKey'` deve listar `Jwt__SigningKey` e `Jwt__SigningKeyAnterior`.

4. **Recrie só a API** com o override e **anote a hora (UTC)**: é daqui que a espera conta.

   ```bash
   docker compose -f docker-compose.yml -f docker-compose.rotacao-jwt.yml up -d --wait api
   ```

5. **Confira que a API subiu** (`docker compose ps api` = `healthy`) e que o login funciona com a chave nova. Uma sessão já aberta no navegador continua funcionando (o token antigo é validado pela anterior).

6. **Espere 16 minutos** a partir do passo 4 (15 min de vida do token + 30 s de tolerância, com 30 s de margem).

7. **Tire a chave anterior.** Suba a API sem o override, e só então apague os dois arquivos da rotação:

   ```bash
   docker compose up -d --wait api
   rm docker-compose.rotacao-jwt.yml secrets/jwt_signing_key_anterior
   ```

   A chave antiga já não valida nada a esta altura, então um `rm` simples basta.

8. **Confira de novo**: `docker compose ps api` = `healthy`, o login funciona e `docker compose config | grep -c SigningKeyAnterior` imprime `0`.

9. **Registre**: data e hora UTC, motivo, quem fez e o resultado.

## Vazamento da chave (rotação sem chave anterior)

Quem tem a chave forja um access token de **qualquer** usuário, inclusive Admin, até ela sair de circulação. Por isso, no vazamento **não se liga a chave anterior**: a chave vazada deixa de valer no momento em que a API sobe com a nova.

1. `rm secrets/jwt_signing_key` (a chave está comprometida; não há o que preservar) e `infra/scripts/gerar-segredos.sh prod`.
2. `docker compose up -d --wait api`.
3. Todos os access tokens somem na hora. Os clientes recebem 401, refazem o refresh (que não depende da chave) e voltam sem novo login. Esse refresh, porém, **não prova** que a sessão é legítima: se o vazamento veio junto com acesso ao banco ou a cookies, avalie também revogar as sessões (troca de senha do usuário ou desativação) e rotacionar o `Seguranca__Pepper` só se o Sentinel mandar (a tabela 5.1 do `05` diz o custo).
4. Acompanhe o log por tokens inválidos nas horas seguintes: `docker compose logs --since 1h api | grep '"StatusCode":401'`.
5. Registre o incidente e avise o Sentinel.

## Voltar atrás

Se a API não subir ou o login falhar logo depois do passo 4, **antes** de apagar a anterior:

```bash
mv secrets/jwt_signing_key_anterior secrets/jwt_signing_key
docker compose up -d --wait api
rm -f docker-compose.rotacao-jwt.yml
```

Isso volta à chave antiga (que ainda assina e valida) e a rotação pode ser refeita depois. Depois do passo 7, a chave antiga já não existe: não há volta, só uma rotação nova.

## Erros comuns

| Sintoma | Causa | O que fazer |
|---|---|---|
| A API não fica `healthy` e o log diz que `Jwt:SigningKeyAnterior` é igual à `Jwt:SigningKey` | O passo 1 copiou em vez de mover, ou o passo 2 não gerou chave nova | Gere de fato uma chave nova (apague `secrets/jwt_signing_key`, rode o `gerar-segredos.sh prod`) |
| A API não fica `healthy` e o log diz que `Jwt:SigningKey` tem menos de 32 bytes ou não é base64url | Arquivo vazio ou editado à mão | Apague o arquivo e gere de novo com o script |
| Muitos 401 logo depois da troca, mesmo com a anterior ligada | O override não entrou (esqueceu o `-f docker-compose.rotacao-jwt.yml`) | Refaça o passo 3 e o `config`; suba a API de novo com os dois `-f` |
| `docker compose` reclama que `secrets/jwt_signing_key_anterior` não existe | Arquivo apagado antes de tirar o override do `up` | Suba sem o override (passo 7) |
