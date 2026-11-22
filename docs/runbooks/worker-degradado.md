# Worker degradado: o que fazer com cada `WORKER_DEGRADADO`

Runbook do alerta do Worker (spec [`08-epico-4-tarefas.md`](../specs/08-epico-4-tarefas.md), E4-D11 e E4-D13).

## Como o alerta aparece

O Worker não reinicia e o healthcheck dele continua verde (o healthcheck é o heartbeat): "degradado" se vê só no log e no arquivo-marcador.

- **Log `Error`**: `WORKER_DEGRADADO motivo=<código>`, a cada ciclo enquanto a condição durar (não só na transição). Para ver:

  ```bash
  docker compose logs --since 1h worker | grep -E "WORKER_DEGRADADO|WORKER_RECUPERADO"
  ```

- **Log `Information`**: `WORKER_RECUPERADO motivo=<código>`, uma vez, quando a condição sai.
- **Arquivo-marcador**: `/tmp/worker-degradado` dentro do Worker (`Worker:ArquivoDeDegradacao`), com os motivos ativos separados por vírgula. Some quando o último motivo é recuperado. É o que o smoke lê.

Há cinco motivos (lista fechada): `scanner`, `assinatura_velha`, `sanitizer`, `trava_apagao`, `trava_apagao_quarentena`. Cada um tem a sua seção abaixo. Vários podem estar ativos ao mesmo tempo.

> Em desenvolvimento o arquivo `docker-compose.dev.yml` precisa de `-f` explícito (`docker compose -f docker-compose.dev.yml ...`). Os comandos abaixo usam o Compose de produção.

## `scanner`: o antivírus não responde

**O que significa.** O `clamd` recusa a conexão ou não responde ao `PING`. O Worker não captura nenhum item nem baixa nenhum arquivo enquanto isso durar.

**O que se perde.** Nada. Os envios ficam `Pendente` e o item não perde tentativa (um antivírus parado nunca rejeita arquivo legítimo nem aprova sem scan).

**Passos.**

1. `docker compose ps clamd freshclam`: o `clamd` está `healthy`?
2. `docker compose logs --since 30m clamd`: erro de configuração, base de assinaturas ausente ou falta de memória?
3. `docker compose restart clamd` e espere o healthcheck.
4. No log do Worker, confirme `WORKER_RECUPERADO motivo=scanner` no ciclo seguinte.

## `assinatura_velha`: a base do antivírus passou da idade

**O que significa.** A base de assinaturas do ClamAV tem mais de `Seguranca__IdadeMaximaAssinaturaDias` dias (padrão do Compose: 3) ou não traz data legível. Mesma regra do `scanner`: nada é capturado, nada se perde.

**Passos.**

1. `docker compose logs --since 1h freshclam`: o `freshclam` está baixando? O erro típico é de rede (sem saída para os espelhos do ClamAV).
2. Corrija a causa (rede, volume `clamav-db` cheio ou sem permissão) e reinicie o `freshclam`: `docker compose restart freshclam`.
3. Confirme `WORKER_RECUPERADO motivo=assinatura_velha`.

**Não faça.** Subir `Seguranca__IdadeMaximaAssinaturaDias` para o alerta sair: isso aprova arquivos com uma base velha.

## `sanitizer`: o sanitizer fora, ou o diretório de jobs inacessível

**O que significa.** Uma de duas coisas: o sanitizer não pegou o job no prazo (30 s), ou o diretório de jobs (`/jobs`, volume `sanitizer-jobs`, compartilhado entre o Worker e o sanitizer) está inacessível ao Worker.

**O que se perde.** Nada. O item volta a `Pendente` e não perde tentativa.

**Passos.**

1. `docker compose ps sanitizer`: está de pé e `healthy`?
2. `docker compose logs --since 30m sanitizer worker`: procure `PIPELINE_MIDIA_SANITIZER_NAO_INICIOU` (o sanitizer não pegou o job) ou `PIPELINE_MIDIA_DIRETORIO_DE_JOBS` (o Worker não consegue criar o job em `/jobs`).
3. Se for o sanitizer: `docker compose restart sanitizer`.
4. Se for o diretório: confira que o volume `sanitizer-jobs` está montado nos dois contêineres e tem espaço e permissão de escrita para o Worker, e reinicie o Worker.
5. Confirme `WORKER_RECUPERADO motivo=sanitizer` quando o próximo job for pego.

## `trava_apagao`: a limpeza diária de `midia/` abortou

**O que significa.** A rodada diária de órfãos de `midia/` abortou **sem apagar nada**: a consulta ao banco falhou, ou ela apagaria mais que o teto `min(LimiteExclusao, max(PisoExclusao, 10% do que listou))`. Padrão: `LimiteExclusao` = 20 e `PisoExclusao` = 5. Um Worker apontando para um banco vazio ou restaurado veria a biblioteca inteira como órfã; é para isso que a trava existe.

**O que se perde.** Nada: os órfãos só ficam ocupando espaço até a trava sair.

**Passos.**

1. `docker compose logs --since 24h worker | grep WORKER_LIMPEZA_MIDIA_ABORTADA`. A linha diz qual foi a causa (consulta ao banco falhou, ou "N órfão(s) em M objeto(s) passam do teto de T").
2. **Confira que o banco é o certo.** Esta é a verificação que importa: o Worker está apontando para o banco de produção, ele não foi restaurado de um backup antigo, e a biblioteca está lá?
   - Banco errado, vazio ou restaurado: **não suba o limite.** Corrija a conexão ou o banco; a trava está fazendo o trabalho dela.
   - Consulta falhou: resolva o SQL (conexão, credencial `webradio_app`); a próxima rodada tenta de novo.
3. Se o banco está certo e os N órfãos são legítimos (por exemplo, uma limpeza em lote de mídias rejeitadas), **suba o limite por uma execução**. O teto é `min(LimiteExclusao, max(PisoExclusao, 10%))`, então suba **os dois**, `Manutencao__Midia__LimiteExclusao` e `Manutencao__Midia__PisoExclusao`, para `N` ou mais. O Worker recusa subir com `LimiteExclusao` acima de 500.

   Num arquivo de override fora do git (por exemplo `docker-compose.limite.yml`):

   ```yaml
   services:
     worker:
       environment:
         Manutencao__Midia__LimiteExclusao: "60"
         Manutencao__Midia__PisoExclusao: "60"
   ```

   ```bash
   docker compose -f docker-compose.yml -f docker-compose.limite.yml up -d worker
   ```

4. Espere a rodada diária seguinte e confirme que passou (`WORKER_LIMPEZA_MIDIA` com a contagem apagada e `WORKER_RECUPERADO motivo=trava_apagao`).
5. **Volte ao padrão**: apague o override e rode `docker compose up -d worker`. Um limite esquecido enfraquece a trava (por isso o teto de 500). Confira que voltou: `docker compose config | grep -E 'LimiteExclusao|PisoExclusao'` não deve imprimir nada.

**Não faça.** Subir o limite sem ter conferido o banco: com o banco errado, a rodada apaga a biblioteca que a trava existe para proteger.

**Custo aceito do piso.** Com o banco vazio, a trava só pega depois de apagar até 5 objetos.

## `trava_apagao_quarentena`: a varredura de 30 minutos da quarentena abortou

**O que significa.** A varredura de órfãos do prefixo `quarentena/` (a cada 30 min, `Manutencao:Quarentena:IntervaloOrfaosMinutos`) abortou **sem apagar nada**: a consulta ao banco falhou, ou ela apagaria mais de `Manutencao:Quarentena:LimiteExclusao` objetos (padrão 50, limite absoluto, sem percentual; o Worker recusa subir com esse valor acima de 500). A trava é própria da quarentena e só a própria varredura recupera o motivo (a rodada diária de `midia/` não toca nele).

**O que se perde.** Nada, mas a quarentena não é limpa enquanto a trava estiver ativa, e a quota do bucket pode encher.

**Passos.**

1. `docker compose logs --since 3h worker | grep WORKER_LIMPEZA_QUARENTENA_ABORTADA`: a causa e a contagem aparecem na linha.
2. **Confira que o banco é o certo** (mesma verificação do `trava_apagao`). A quarentena guarda os originais de arquivos em `Quarentena` para análise (S-B06): num banco errado, passar do limite apagaria esses originais.
3. Consulta falhou: resolva o SQL; a próxima varredura (até 30 min) tenta de novo.
4. Banco certo e mais de 50 órfãos legítimos (um dia de muitas desistências de envio, por exemplo): suba **só** `Manutencao__Quarentena__LimiteExclusao` (o piso não existe na quarentena) por uma execução, no mesmo arquivo de override e com o mesmo `up -d worker` do `trava_apagao`:

   ```yaml
   services:
     worker:
       environment:
         Manutencao__Quarentena__LimiteExclusao: "200"
   ```

5. Espere a varredura seguinte (até 30 min), confirme `WORKER_RECUPERADO motivo=trava_apagao_quarentena` e **volte ao padrão** (apague o override, rode `docker compose up -d worker` e confira com `docker compose config | grep -E 'LimiteExclusao|PisoExclusao'`, que não deve imprimir nada).

**Não faça.** Subir o limite sem ter conferido o banco: com o banco errado, a varredura apaga até os originais em `Quarentena` guardados para análise.

**Custo aceito.** Com o banco vazio, a trava só pega depois de apagar até 50 objetos da quarentena.
