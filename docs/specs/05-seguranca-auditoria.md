# WebRadio — Especificação v1: Auditoria de Segurança

> Autor: Sentinel (DevSecOps/AppSec) · 2026-09-27 · Status: **normativo** (v1.1, ver `00-arquitetura.md` §7)
>
> **v1.1 (2026-09-27, Sentinel):** (1) S-A12 alinhado ao D21: sempre 401, sem 423. (2) **Correção do S-A06**: o `RequireHost` compara o cabeçalho `Host`, que o cliente controla, e não a porta local; o isolamento passa a ser por `Connection.LocalPort` + bind do Kestrel. (3) S-A03: o GET pré-assinado através de `/storage/` exige assinar sem o prefixo. (4) S-A02: o antivírus roda sobre o **original**, antes do sanitizer. (5) Achados novos da revisão da v1.1: S-M15, S-B09 a S-B11.
> Escopo: auditoria de `00-arquitetura.md` (§3 Fluxo C, §4, §5), `01-dominio.md` (ArquivoMidia), `02-api-rest.md` (§1, §6, §9, §10), `03-schema-sqlserver.sql` (§10) e `04-epico-1-tarefas.md` (serviços do compose).
> Este arquivo **não altera** os arquivos 00–04. Os itens marcados com **[CONTRATO]** mudam DTO, rota, coluna ou topologia e precisam passar pelo Nexus antes do código.

## Sumário

| Nível | Qtde | IDs |
|---|---|---|
| CRÍTICO | 2 | S-C01, S-C02 |
| ALTO | 12 | S-A01 … S-A12 |
| MÉDIO | 15 | S-M01 … S-M15 |
| BAIXO | 11 | S-B01 … S-B11 |

**O que já está bom** (manter): chave de objeto gerada pelo servidor (D11) e `NomeOriginal` nunca usado como caminho; POST policy com `content-length-range` (D10); detecção de tipo real + recodificação com `-map_metadata -1`; refresh opaco, gravado como hash, rotativo, com revogação por família; access token só em memória; cookie `HttpOnly; Secure; SameSite=Strict` com `Path` restrito; IP e `listenerId` só como HMAC (LGPD); stream independente da API (fallback do Liquidsoap), o que isola o áudio de um DDoS na API; logins SQL separados e sem sysadmin; gitleaks na CI.

Tarefas do Épico 1 são citadas pelo ID (E1-Fxx/Pxx/Axx). O pipeline de sanitização é do **Épico 4** (citado como `E4-sanitização`), mas as decisões abaixo precisam entrar na spec **agora**, porque mudam a topologia do compose (E1-F16) e a abstração de storage (E1-F09).

---

## 1. CRÍTICOS

### S-C01 — ffprobe/ffmpeg sobre arquivo não confiável sem restrição de protocolo e demuxer (SSRF, leitura de arquivo local, RCE em demuxer)
**Afeta:** Fluxo C passos 3.1 e 3.5 · `E4-sanitização` · topologia do compose em **E1-F16** · imagem do Worker em **E1-F13**

**Problema.** O ffmpeg reconhece o formato pelo **conteúdo**, não pela extensão. Um "áudio" que na verdade é uma playlist HLS (`#EXTM3U`), um script do demuxer `concat` ou um arquivo `ffconcat` faz o ffprobe/ffmpeg **abrir outras URLs e arquivos**: `http://api:8080/...`, `http://minio:9000/...`, `file:///proc/self/environ` (que contém **todos os segredos do Worker**). Esse é o vetor de "SSRF via URL" da auditoria, e ele vem de dentro de um arquivo de upload, não de um campo da API. Além disso, os demuxers e decoders do ffmpeg têm histórico constante de CVEs de corrupção de memória. Hoje o processo que os executa é o Worker, que tem a connection string do SQL, as credenciais do MinIO e acesso à rede interna inteira.

A ordem do Fluxo C (magic bytes **+** ffprobe) não basta se o ffprobe rodar antes da checagem de magic bytes ou sem o formato forçado.

**Recomendação (obrigatória antes de o Épico 4 começar):**
1. **Magic bytes primeiro, e só eles decidem o demuxer.** `ID3`/frame sync `0xFFE` → `mp3`; `OggS` → `ogg`; `RIFF....WAVE` → `wav`; `fLaC` → `flac`. Qualquer outro início → `Rejeitado`, **sem** chamar o ffprobe.
2. Sempre forçar o demuxer e bloquear protocolos:
   ```
   ffprobe -v error -protocol_whitelist file -f <fmt> -show_streams -show_format -of json in.bin
   ffmpeg  -nostdin -hide_banner -protocol_whitelist file -f <fmt> -i in.bin \
           -map 0:a:0 -vn -sn -dn -map_metadata -1 -map_chapters -1 \
           -fflags +bitexact -flags:a +bitexact \
           -c:a libmp3lame -b:a 192k -ar 44100 -ac 2 \
           -t <duracaoMax+1> -fs <tamanhoMaxSaida> -f mp3 out.mp3
   ```
3. **Whitelist de codec**, além do contêiner: `mp3`, `vorbis`, `opus`, `flac`, `pcm_s16le|s24le|s32le|f32le`. OGG pode carregar Theora e WAV pode carregar codecs exóticos, com decoders pouco testados. Exigir exatamente **um** stream de áudio (a capa em MP3 é ignorada pelo `-map 0:a:0`).
4. **Isolar a execução num contêiner `sanitizer` próprio**, sem segredos: sem connection string, sem pepper, sem JWT. O Worker baixa o objeto, passa o arquivo ao sanitizer por volume `tmpfs` compartilhado (ou stdin/stdout) e recebe o resultado. O sanitizer roda com `network_mode: none`, `read_only: true`, `cap_drop: [ALL]`, `security_opt: [no-new-privileges:true]`, usuário não root, `mem_limit`, `pids_limit` e `cpus`.
5. Preferir um **build mínimo do ffmpeg** na imagem do sanitizer (`--disable-everything --enable-demuxer=mp3,ogg,wav,flac --enable-decoder=mp3*,vorbis,opus,flac,pcm_* --enable-encoder=libmp3lame --enable-muxer=mp3 --enable-protocol=file,pipe`). Isso reduz a superfície de ataque em ordens de grandeza.
6. **Teste de regressão (E4):** um upload `.mp3` cujo conteúdo é `#EXTM3U\nhttp://api:8080/health/live` e outro `ffconcat version 1.0\nfile /etc/passwd` → ambos `Rejeitado`, e o log do sanitizer não mostra nenhuma conexão de saída.

**[CONTRATO]** Novo serviço `sanitizer` no compose (E1-F16 reserva o serviço; a imagem vem no Épico 4).

---

### S-C02 — Segredos com valor de exemplo funcional permitem forjar tokens de Admin
**Afeta:** **E1-F16** (`.env.example`), **E1-F06/F07** (validação na inicialização), **E1-F12**, **E1-F14**, **E1-F15**

**Problema.** A Definição de Pronto pede `docker compose up` numa máquina limpa em menos de 3 min. O caminho natural é `cp .env.example .env`. Se o `.env.example` trouxer uma `Jwt__SigningKey` válida (≥ 32 bytes), essa chave vai parar num ambiente real, e **qualquer pessoa que leu o repositório** assina um JWT com `role=Admin`. O mesmo vale para `Playout__Token` (controla a fila e o "tocando agora"), as senhas do Icecast e do harbor (sequestro da transmissão) e `Seed__AdminSenha`.

**Recomendação:**
1. `.env.example` só com valores **não funcionais**: `Jwt__SigningKey=__GERAR__` e o comando de geração no comentário.
2. Script `infra/scripts/gerar-segredos.sh` (e um `.ps1` equivalente para o DJ no Windows) que cria `.env` (dev) ou `secrets/*` (produção) com `openssl rand`, e **se recusa a sobrescrever** o que já existe.
3. **Fail-fast** em toda aplicação .NET (`IValidateOptions` + `ValidateOnStart`): recusar o boot se o segredo estiver vazio, contiver `__GERAR__`/`CHANGE_ME`, ou estiver abaixo do mínimo (tabela da §5). Para Icecast, Liquidsoap e MinIO, o entrypoint faz a mesma checagem em shell.
4. No compose, usar `${VAR:?defina VAR no .env}` para segredos obrigatórios. Sem `.env`, o `compose up` falha com mensagem clara, em vez de subir com o segredo vazio.
5. A CI (E1-F17) roda o gitleaks **e** um grep que reprova qualquer valor de segredo em `.env.example` diferente de `__GERAR__`.

---

## 2. ALTOS

### S-A01 — Bombas de descompressão e arquivos "patológicos" de áudio
**Afeta:** `E4-sanitização`, E1-F16 (limites do contêiner)

Um FLAC ou WAV de 250 MB pode declarar uma duração pequena no cabeçalho e decodificar para horas de PCM. Um OGG pode encadear milhares de streams lógicos, e um MP3 pode ter um tag ID3 de centenas de MB. A duração lida no `ffprobe` vem do **cabeçalho** e não é confiável.
- A duração que conta é a do **arquivo de saída**: rodar `ffprobe` no `out.mp3` e aplicar os limites por tipo sobre ele. O `-t <max+1>` e o `-fs` do S-C01 limitam o trabalho, mesmo que o cabeçalho minta.
- Timeout duro por job (ex.: `max(60 s, 2 × duraçãoMáx do tipo)`), com kill do processo; `mem_limit` de 512 MB e `pids_limit` no sanitizer.
- Espaço temporário em `tmpfs` com tamanho fixo (ex.: 1 GB), para que um arquivo não encha o disco do host.
- Conferir o `Content-Length` do objeto (`stat`) **antes** de baixar e abortar se passar do limite do tipo.

### S-A02 — ClamAV como stub `NoOp` no Épico 1: aceitável **com condições**
**Afeta:** E1-F09 (registro de DI), `E4-sanitização`

**Veredito: aceitável**, porque o Épico 1 não aprova mídia nenhuma (upload é Épico 4) e porque, para áudio, a defesa principal é a recodificação (S-C01), não o antivírus. O risco é o stub **escapar para produção** e aprovar arquivos em silêncio. Condições:
1. O `NoOpMalwareScanner` fica num assembly/namespace de dev e é registrado **só** com `IHostEnvironment.IsDevelopment()` **e** `Seguranca:PermitirScannerNoOp=true`. As duas condições precisam valer.
2. Fora de Development, sem scanner real configurado, o **Worker não sobe** (fail-closed), em vez de pular a etapa.
3. O resultado do NoOp é registrado no log como `SCAN_IGNORADO` e o `MotivoRejeicao`/auditoria indica que a mídia não foi escaneada. Quando o ClamAV chegar, um job reprocessa as mídias aprovadas sem scan.
4. As imagens de produção devem definir `ASPNETCORE_ENVIRONMENT=Production` explicitamente (o override de dev define `Development`; ver S-M12).
5. **Ordem (v1.1):** o antivírus roda sobre o **arquivo original** (`in.bin`), logo depois dos magic bytes e **antes** de entregar o job ao sanitizer. Escanear o MP3 recodificado não serve para nada: ele foi gerado pelo nosso encoder. Malware detectado → `Quarentena`, sem passar pelo ffmpeg.
6. No Épico 4: `clamd` em contêiner próprio, sem rede externa (as assinaturas entram por um `freshclam` separado), com `StreamMaxLength` ≥ 250 MB. **Falha ou timeout do ClamAV = arquivo continua `Pendente`** (retry), nunca `Aprovado`.

### S-A03 — Porta 9000 do MinIO publicada na borda (API S3 e admin expostas)
**Afeta:** E1-F16, E1-P06, E1-F09 (`Storage__PublicEndpoint`) · **[CONTRATO]** topologia

A tabela de serviços publica a porta 9000 porque o browser precisa dela. Isso expõe a API S3 inteira, incluindo `/minio/admin/*` e as métricas, à internet. Também contradiz o D9: um POST do browser para `:9000` é **cross-origin** e exige CORS no MinIO. No Linux, uma porta publicada pelo Docker ainda passa por fora do UFW.
- **Recomendação:** servir o storage pela **mesma origem**, com `location /storage/` no nginx → `minio:9000` e `Storage__PublicEndpoint=https://<host>/storage`. A assinatura da POST policy não cobre host nem caminho, então o proxy não a quebra.
- **GET pré-assinado (correção v1.1):** a SigV4 assina o **caminho** e o **host**. O MinIO não serve com prefixo de caminho, e o nginx remove o `/storage` antes de repassar. Então, se o cliente público assinar `https://<host>/storage/midia/k`, o MinIO recalcula a assinatura sobre `/midia/k` e responde `SignatureDoesNotMatch`. Como fazer:
  1. assinar a URL **sem** o prefixo, com o host público: `https://<host>/midia/k?X-Amz-...`;
  2. inserir o `/storage` no caminho **depois** de assinar: `https://<host>/storage/midia/k?X-Amz-...`;
  3. o nginx remove o `/storage` (`proxy_pass http://minio:9000/;`) e envia `proxy_set_header Host "${PUBLIC_AUTHORITY}";` (o host:porta canônico fixo, igual ao assinado; ver S-A06). O MinIO passa a ver exatamente o host e o caminho assinados.

  Encapsular isso no `IObjectStorage.UrlGetPreAssinada` (cliente público). **Teste de integração pelo nginx real** (E1-F09/P06): o preview responde 200 e uma URL com a query adulterada responde 403.
- Nesse `location`, permitir **só** `POST` em `/storage/quarentena` e `GET/HEAD` em `/storage/midia/*` (com query de assinatura) e `/storage/publico/*`. Todo o resto responde 404: `/storage/minio/*`, listagem de bucket, `PUT`, `DELETE`. Usar `client_max_body_size 250m` só nesse `location`.
- A porta 9000 **não é publicada** em nenhum perfil. A 9001 (console) só no override de dev, em `127.0.0.1`.

### S-A04 — Credencial root do MinIO nas aplicações
**Afeta:** E1-F09, E1-F16 (`minio-init`)

A spec não diz com que credencial a API e o Worker falam com o MinIO. Se for `MINIO_ROOT_USER`, invadir a API dá controle total do storage (apagar a biblioteca, tornar a quarentena pública). O `minio-init` deve criar **usuários de serviço com política mínima**:

| Principal | Permissões |
|---|---|
| `svc-api` | `s3:PutObject` em `quarentena/*` e `publico/*` (quem assina a POST policy precisa desse direito); `s3:GetObject` em `midia/*` (preview); `s3:GetObject`/`s3:HeadObject` em `quarentena/*` (o `HEAD` do `concluir`) |
| `svc-worker` | `s3:GetObject` e `s3:DeleteObject` em `quarentena/*`; `s3:PutObject` e `s3:GetObject` em `midia/*` e `publico/*`; `s3:DeleteObject` em `midia/*` |
| root | só no `minio-init` e na operação manual; nunca no `.env` da API ou do Worker |

O Liquidsoap **não** recebe credencial: ele só consome URLs pré-assinadas.

### S-A05 — Bucket `publico` listável e imagens de divulgação sem sanitização
**Afeta:** E1-F16 (`minio-init`), `02-api-rest.md §7` (`POST /divulgacoes/imagens`) · **[CONTRATO]**

1. **Listagem:** a política canned `mc anonymous set download` inclui `s3:ListBucket` e `s3:GetBucketLocation`, e o bucket fica listável. Isso expõe imagens de divulgações ainda não publicadas. Use uma política JSON explícita só com `s3:GetObject` em `arn:aws:s3:::publico/*` e confira com `mc anonymous get-json`. **Teste:** `GET /storage/publico/` anônimo → 403/404.
2. **Sanitização:** a imagem vai do browser direto para um bucket **público e anônimo** sem passar por nada. A condição `Content-Type` da policy é o que o cliente declara. Uma conta de Admin comprometida, ou um bug no endpoint, transforma o bucket em hospedagem de malware, SVG/HTML com script ou polyglots JPEG+ZIP. A imagem também mantém o EXIF (GPS do celular de quem tirou a foto).
   **Recomendação:** as imagens têm no máximo 2 MB, então o upload deve passar **pela API** (`multipart/form-data`, `RequestSizeLimit` de 2 MB). A API decodifica e **recodifica** a imagem (NetVips ou SkiaSharp), descarta os metadados, limita as dimensões (ex.: 4096 px, contra bombas de pixel) e grava no `publico` com `Content-Type` definido pelo servidor. Isso muda o contrato de `POST /divulgacoes/imagens` (sai a POST policy, entra o multipart) e dá para decidir com o Nexus.
3. No nginx, `location /storage/publico/` responde com `X-Content-Type-Options: nosniff` e `Content-Security-Policy: default-src 'none'; sandbox`.

### S-A06 — Bloqueio de `/api/v1/internal` no nginx contornável por maiúsculas
**Afeta:** E1-P06, E1-F12 · **[CONTRATO]** (porta interna)

`location /api/v1/internal/ { return 404; }` compara com diferenciação de maiúsculas, mas o roteamento do ASP.NET Core **não diferencia**. `GET /api/v1/INTERNAL/playout/proxima` passa pelo nginx e chega ao endpoint. Hoje o Bearer ainda protege, mas o bloqueio de rede, que era a segunda camada, deixa de existir. `/api/v1/internal` sem barra final também escapa do bloqueio.
- **Correção principal:** os endpoints internos ficam num **listener Kestrel separado** (`:8081`), que o nginx nunca encaminha. O Liquidsoap chama `http://api:8081/api/v1/internal/...`.
- **⚠ Não usar `RequireHost("*:8081")` como controle (correção v1.1).** O `RequireHost` compara o **cabeçalho `Host`**, não a porta em que a conexão chegou. Uma requisição na `:8080` pública com `Host: x:8081` passa no filtro sempre que o nginx repassa o Host do cliente com a porta (`proxy_set_header Host $http_host`). A checagem certa usa a porta **local** da conexão:
  ```csharp
  var interno = app.MapGroup("/api/v1/internal")
      .AddEndpointFilter(async (ctx, next) =>
          ctx.HttpContext.Connection.LocalPort == 8081 ? await next(ctx) : Results.NotFound());
  ```
  O mesmo filtro vale para `/health/*`. Criar um helper `.SomentePortaInterna()` e aplicá-lo nos dois grupos.
- **Bind da `:8081`:** o Docker não liga porta por rede; o contêiner escuta em `0.0.0.0` em **todas** as redes a que pertence. Para que a `:8081` exista só na rede `playout`, fixar o IP da api nessa rede (`ipv4_address`) e usar `Kestrel:Endpoints` = `http://0.0.0.0:8080` + `http://<ip-playout>:8081` + `http://127.0.0.1:8081` (o último para o healthcheck).
- Nginx (revisado no PR #3): `proxy_set_header Host "${PUBLIC_AUTHORITY}";`, com um **host:porta canônico fixo** (igual ao do `Storage__PublicEndpoint`), em todos os proxies. **Nunca** `$http_host`: o `server_name` filtra só o nome, e a porta escolhida pelo cliente chegaria ao upstream. O `$host` também não serve para o `/storage/`, porque descarta a porta e quebra a SigV4 em dev (`:8080`).
- **Defesa em profundidade no nginx:** `location ~* ^/api/v1/+internal(/|$) { return 404; }`, declarado **antes** do `location /api/`.
- Comparar o `Playout__Token` com `CryptographicOperations.FixedTimeEquals`. Aceitar dois tokens válidos (`Playout__Token` e `Playout__TokenAnterior`) para permitir rotação sem parar o stream.
- **Teste (E1-F12):** `/api/v1/internal/x`, `/api/v1/INTERNAL/x`, `/api/v1//internal/x`, `/api/v1/%69nternal/x` e `/api/v1/internal` pelo `:8080` → todos 404. **Mais (v1.1):** uma requisição direta na `:8080` da api (sem o nginx, por exemplo de um contêiner na rede `app`) com `Host: api:8081` → 404; e `curl http://api:8081/health/live` a partir do contêiner `web` → conexão recusada.

### S-A07 — Icecast exposto por inteiro no proxy `/stream/` (admin e source)
**Afeta:** E1-P06, E1-F14

O proxy `/stream/` → `icecast:8000/` também publica `/stream/admin/*` (a `listclients` mostra o **IP de cada ouvinte**, dado pessoal) e aceita `PUT`/`SOURCE` em `/stream/radio.mp3`, o que permite a **quem descobrir a senha de source sequestrar a transmissão** pela internet. Tudo isso protegido por Basic auth sem limite de tentativas.
- No nginx: `location = /stream/radio.mp3 { limit_except GET HEAD { deny all; } ... }`; `location = /stream/status-json.xsl` só se o frontend precisar; `location /stream/ { return 404; }` para todo o resto.
- No `icecast.xml`: `<admin-user>` diferente de `admin`, `<hidden>1</hidden>`, `<clients>` e `<sources>` limitados (`sources` = 2) e `<source-timeout>`. As senhas vêm do template (E1-F14), com os mínimos da §5.

### S-A08 — Harbor do Liquidsoap (`:8005`) público, com senha única trafegando em texto claro
**Afeta:** E1-F15, E1-F16 · Épico 2 · **[CONTRATO]** (auth do harbor)

O harbor tem **prioridade máxima** no `fallback`. Quem tiver a senha substitui a programação inteira. O protocolo source do Icecast manda a senha em Basic auth sem TLS, ela é compartilhada por todos os locutores e não tem bloqueio de tentativas.
- **Épico 1:** a senha tem ≥ 24 caracteres aleatórios. A porta 8005 é publicada **só** se houver transmissão ao vivo, e com lista de IPs permitidos no firewall do host (ou atrás de uma VPN como WireGuard/Tailscale). No override de dev, `127.0.0.1:8005`.
- **Épico 2:** usar o `auth` do `input.harbor` com callback para a API (`POST /internal/harbor/auth`, na porta interna do S-A06), que valida **o login do próprio locutor** e só aceita se ele for o dono do programa `AoVivo` naquele momento. Essa rota é nova e precisa do Nexus. Avaliar TLS no harbor (o Liquidsoap 2.x suporta transporte TLS).
- Registrar no log conexão e desconexão do harbor (quem, quando), sem registrar a senha.

### S-A09 — Injeção de URI/metadado no Liquidsoap pela fila de playout
**Afeta:** E1-F15, Fluxo D

O Liquidsoap interpreta URIs com protocolos como `annotate:`, `process:` (que **executa comando**) e `http:`. Se o `radio.liq` montar o request concatenando `titulo`/`artista` (que o usuário controla) numa string `annotate:title="...":<url>`, um título com aspas ou `:` altera o URI.
- O script só aceita `url` que comece com o prefixo interno esperado (`http://minio:9000/midia/`) e rejeita qualquer outra coisa.
- Os metadados entram por `metadata.map`/`insert_metadata`, nunca por concatenação dentro de `annotate:`.
- A API monta a URL **só** a partir de `Bucket`/`ChaveStorage` gerados pelo servidor (isso já vem do D11; manter).
- Os metadados que chegam do harbor (vindos do cliente do locutor) e vão para o `POST /internal/playout/eventos` são validados por tamanho (200) e sem caracteres de controle antes de serem publicados em `TocandoAgora`.

### S-A10 — Segredos compartilhados entre serviços, Redis sem senha e rede plana
**Afeta:** E1-F16, E1-A02, E1-F05, E1-F09/F10 (Redis)

1. **`env_file: .env` em todos os serviços** entrega a senha SA, a chave JWT e o pepper ao Liquidsoap e ao Icecast. Cada serviço recebe **só** os segredos que usa (tabela da §5). A senha SA fica **só** no passo de criação do banco.
2. **Separar o migrator em dois passos:** `db-init` (SA: `CREATE DATABASE`, RCSI, `logins.sql`) e `migrator` (efbundle com `webradio_migrator`). Assim a SA nunca entra numa imagem da aplicação.
3. **Redis sem autenticação:** com o backplane do SignalR, qualquer contêiner comprometido na rede `interna` (Liquidsoap, Icecast) consegue publicar mensagens para **todos** os clientes do hub e apagar as chaves de throttle. Usar `requirepass` (ou ACL com um usuário `webradio` sem `FLUSHALL`/`CONFIG`/`KEYS`), vindo de segredo, e `rename-command` para `CONFIG`, `FLUSHALL` e `DEBUG`.
4. **Segmentar a rede:** `borda` (web); `app` (web, api); `dados` (api, worker, migrator, db-init, sqlserver, redis, minio); `playout` (api:8081, liquidsoap, icecast, minio). Nessa divisão, o Liquidsoap e o Icecast não alcançam o SQL nem o Redis.
5. O healthcheck do SQL (`sqlcmd -Q "SELECT 1"`) não pode ter a senha no `docker-compose.yml`, porque ela aparece em `docker inspect`. Ler a senha do arquivo de segredo dentro do contêiner, ou usar um login `healthcheck` sem permissões.

### S-A11 — ForwardedHeaders/KnownProxies: configuração errada vira DoS ou bypass do anti-spam
**Afeta:** E1-F06, E1-P06, E1-F16

- **Configuração ausente ou IP errado em `KnownProxies`:** o ASP.NET ignora o `X-Forwarded-For` e todo mundo aparece com o IP do nginx. Aí o limite global de 100/min por IP e o throttle de 3 pedidos/5 min por IP passam a valer para **a audiência inteira** (DoS causado por nós mesmos). Os IPs dos contêineres mudam a cada `compose up`, então um IP fixo em `KnownProxies` quebra com facilidade.
- **Configuração permissiva demais:** se qualquer origem for confiável, o cliente manda `X-Forwarded-For: <aleatório>` e cada requisição cai num balde novo, o que contorna o throttle por IP e o rate limit do login.
- **Recomendação:**
  - Fixar a subnet da rede `app` no compose (`ipam.config.subnet`) e usar `KnownNetworks` com ela (não `KnownProxies` com IP); `ForwardLimit = 1`; `ForwardedHeaders = XForwardedFor | XForwardedProto`.
  - No nginx, que é a borda: `proxy_set_header X-Forwarded-For $remote_addr;` (**sobrescreve**; não usar `$proxy_add_x_forwarded_for`). Se houver um CDN/LB na frente em produção, usar `real_ip_header` + `set_real_ip_from` só com as faixas do provedor.
  - A rede `app` do S-A10 impede que o Liquidsoap e outros contêineres alcancem a porta pública da API forjando o XFF.
  - **Teste de integração (E1-F06):** uma requisição com `X-Forwarded-For` falso vinda de fora da `KnownNetworks` → `RemoteIpAddress` continua o IP do socket. Uma requisição do proxy → o IP do cabeçalho.

### S-A12 — Bloqueio de conta permite derrubar o Admin e revela quais e-mails existem
**Afeta:** E1-F07, `01-dominio.md §3`

- Com 5 falhas por conta e bloqueio de 15 min, qualquer pessoa que saiba o e-mail do Admin o mantém **bloqueado para sempre** (5 tentativas a cada 15 min, de IPs diferentes; o limite de 5/min por IP não impede isso).
- O **423 só existe para conta existente**, o que contradiz "não revela se o e-mail existe" (enumeração de contas). O login de e-mail inexistente sem hash de senha também responde mais rápido (enumeração por tempo).
- **Recomendação (v1.1, alinhada ao D21):**
  - Responder **sempre 401 genérico**: credencial errada, usuário inativo, bloqueado ou e-mail inexistente. **Não existe 423.** A variante "423 depois de senha correta" da v1 foi descartada, porque confirma ao atacante que a senha está certa (observação do Nexus, correta).
  - Para e-mail inexistente, rodar `PasswordHasher.VerifyHashedPassword` contra um hash fictício, para igualar o tempo.
  - Contar falhas por **(conta, IpHash)** com o bloqueio de 15 min, e manter um contador por conta com limite mais alto (ex.: 50/h) que só gera **alerta e atraso progressivo**, sem bloquear. Um endpoint de Admin desbloqueia manualmente.
  - O critério de aceite de E1-F07 ("6ª tentativa → 423") muda de acordo. **[CONTRATO]** (`01-dominio.md §3`).
  - **Redis fora do ar (v1.1):** o bloqueio por (conta, IpHash) fica indisponível. O `RateLimiter` em memória de `/auth/login` (5/min por IP) continua valendo, e o health vai para `Degraded`. Esse modo degradado é aceito, mas o evento precisa ser registrado no log.

---

## 3. MÉDIOS

| ID | Achado | Recomendação | Tarefa |
|---|---|---|---|
| **S-M01** | Condições da POST policy não especificadas: se usar `starts-with` na `key`, um locutor sobrescreve o upload de outro na quarentena | Policy com `bucket` **eq**, `key` **eq** (exata, `{yyyy}/{MM}/{id}`), `content-length-range` `[1, limiteDoTipo]`, `Content-Type` **eq** ao declarado, `success_action_status` fixo, sem `acl`, sem `x-amz-meta-*` livre, validade de 15 min. O `tamanhoBytes` declarado é validado ≤ limite do tipo **antes** de gerar a policy. Teste em E1-F09: a key alterada no form → 403 | E1-F09 |
| **S-M02** | TOCTOU: a policy vale 15 min e aceita um novo POST na mesma key **depois** do `concluir` | Registrar o `ETag` no `concluir` e fazer o Worker baixar **uma única vez**, com todas as etapas sobre a cópia local; se o `ETag` mudou, `Rejeitado`. **[CONTRATO]** coluna `EtagUpload` | Épico 4, `01-dominio` |
| **S-M03** | Hash: o Fluxo C deduplica **antes** da recodificação, mas o domínio define `HashSHA256` como hash **do arquivo final** | Duas colunas: `HashOriginalSHA256` (dedupe de reenvio e inteligência de malware) e `HashSHA256` (saída; com `-fflags +bitexact` a saída é determinística). **[CONTRATO]** | `01-dominio`, `03-schema` |
| **S-M04** | Arquivo "veneno" (trava ou derruba o ffmpeg) volta para `Pendente` e cria um laço infinito; `EmAnalise` fica preso se o Worker cair | Lease (`EmAnaliseDesdeUtc`) + `TentativasSanitizacao`; depois de 3 tentativas → `Rejeitado` ("falha de processamento"). **[CONTRATO]** colunas | Épico 4 |
| **S-M05** | Polyglots: o original fica guardado ou é servido; o `-map_metadata -1` **não** remove a capa (APIC é um stream de vídeo) | Mapear só `0:a:0` (S-C01); gravar **só a saída recodificada** em `midia`; apagar o original da quarentena ao aprovar ou rejeitar; nunca gerar GET pré-assinado para `quarentena` | Épico 4 |
| **S-M06** | Esgotamento de storage: um locutor cria N slots de 250 MB | `POST /midias/uploads`: 20/h por usuário e no máximo 5 em `AguardandoUpload`/`Pendente` por usuário; quota do bucket `quarentena` (`mc quota set`) | E1-F09, Épico 4 |
| **S-M07** | `NomeOriginal` usado em `Content-Disposition` ou na UI: CRLF, caracteres de controle, RTL override (`mp3.exe` aparece invertido) | Validador: NFC, sem controles (`\p{C}`), sem bidi (`U+202A–202E`, `U+2066–2069`), sem `/ \ :`, ≤ 255. No preview, assinar com `response-content-type=audio/mpeg` e `response-content-disposition=inline; filename*=UTF-8''<id>.mp3` (usar o id, não o nome) | E1-F09, Épico 4 |
| **S-M08** | SignalR: a conexão autenticada continua no grupo `locutores` depois de o token expirar ou do usuário ser rebaixado ou desativado, e segue recebendo `PedidoCriado` (nome e dedicatória dos ouvintes) | `HttpConnectionDispatcherOptions.CloseOnAuthenticationExpiration = true`; em `AlterarRole`/`Desativar`, desconectar as conexões do usuário; `MaximumReceiveMessageSize` = 1 KB (não há métodos cliente→servidor) | E1-F10 |
| **S-M09** | `access_token` e `listenerId` na query do hub são gravados nos logs do nginx e do ASP.NET | `log_format` do nginx sem `$args` para `/hubs/`; o request logging do Serilog registra o path sem a query; validar `listenerId` como UUID no `OnConnectedAsync` | E1-P06, E1-F06, E1-F10 |
| **S-M10** | Refresh: duas rotações concorrentes do mesmo token podem as duas vencer (criando dois ramos, e a detecção de reuso falha). Duas **abas** fazendo refresh derrubam a sessão (falso positivo de roubo), porque o single-flight do P04 só vale dentro de uma aba | Rotação atômica: `UPDATE ... SET RevogadoEmUtc=@agora OUTPUT ... WHERE TokenHash=@h AND RevogadoEmUtc IS NULL` (0 linhas = reuso). Janela de tolerância de 10 s que devolve o **mesmo sucessor**; no frontend, `navigator.locks.request('wr-refresh', …)` entre abas | E1-F07, E1-P04 |
| **S-M11** | Desativar, rebaixar ou trocar a senha não revoga os refresh tokens; o último Admin pode ser **desativado** (a regra só fala em rebaixar) | Revogar todas as famílias do usuário nesses eventos; bloquear `Desativar` e rebaixamento do último Admin ativo, feitos por qualquer pessoa | E1-F07, `01-dominio §2.1` |
| **S-M12** | O `docker-compose.override.yml` é carregado **automaticamente**: um `compose up` em produção publica o SQL, o console do MinIO e `Development` | Renomear para `docker-compose.dev.yml` (uso explícito com `-f`) ou criar um `docker-compose.prod.yml`; o README de E1 documenta os dois comandos | E1-F16 |
| **S-M13** | DDoS/abuso sem limite na borda: o .NET é quem absorve tudo | nginx: `limit_req` em `/api/` (ex.: 20 r/s por IP, burst 40) e mais restrito em `/api/v1/auth/`; `limit_conn` por IP em `/stream/` (ex.: 5) e `/hubs/` (ex.: 10); `client_max_body_size 1m` em `/api/`; `client_header_timeout`/`client_body_timeout` de 10 s (slowloris); `proxy_read_timeout` longo só em `/stream/` e `/hubs/`. No domínio: teto global de `Pendente` (ex.: 300), porque um IPv6 /48 tem 65 mil /64 | E1-P06, Épico 3 |
| **S-M14** | Headers e CSP pedidos no P06 sem valores definidos; o `add_header` do nginx **não é herdado** quando o `location` filho tem o seu | Snippet `security-headers.conf` incluído em **todo** `location`, com `always`: `Content-Security-Policy: default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; media-src 'self' blob:; connect-src 'self'; font-src 'self'; object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'` (revisado no PR #3: sem `wss:`, que libera WebSocket para qualquer host, e `upgrade-insecure-requests` **só com TLS**, como o HSTS); `X-Content-Type-Options: nosniff`; `Referrer-Policy: strict-origin-when-cross-origin`; `Permissions-Policy: camera=(), microphone=(), geolocation=(), payment=()`; `Cross-Origin-Opener-Policy: same-origin`; HSTS (`max-age=31536000; includeSubDomains`) **só** com TLS. Tirar `'unsafe-inline'` de `style-src` se o build não precisar dele. Teste Playwright: zero violações de CSP no console | E1-P06 |
| **S-M15** | *(v1.1)* O `web` (o contêiner mais exposto) entra na rede `playout` para alcançar o Icecast e o MinIO e, com isso, também alcança `api:8081` e o harbor do Liquidsoap | Criar a rede **`midia-borda`** (web, icecast, minio) no lugar de pôr o `web` na `playout`. A `playout` fica só com api:8081, liquidsoap, icecast e minio. Junto com o bind do S-A06, o `web` não tem rota até a `:8081` | E1-F16, E1-P06 |

---

## 4. BAIXOS

| ID | Achado | Recomendação | Tarefa |
|---|---|---|---|
| **S-B01** | `/auth/refresh` e `/auth/logout` dependem só do `SameSite=Strict` contra CSRF | Checar também o `Origin`/`Sec-Fetch-Site: same-origin`; nome do cookie `__Secure-wr_refresh` (o `__Host-` exige `Path=/`) | E1-F07 |
| **S-B02** | `/auth/registrar` aberto, sem limite próprio | Rate limit de 3/h por IP; validar a senha contra uma lista local de senhas vazadas (top 100 mil); limite máximo de 128 caracteres | E1-F07 |
| **S-B03** | `LinkDestino` validado só com `LIKE 'https://%'` | `Uri.TryCreate(Absolute)` com esquema `https`, sem `userinfo` (`user@host`), host não IP privado; o frontend usa `rel="noopener noreferrer"`. O servidor **nunca** busca essa URL (sem preview/OG) | Épico 3, E1-P08 |
| **S-B04** | Os logs de acesso do nginx e do Icecast guardam IP em claro (dado pessoal) | Retenção de 14 dias; `map` no nginx que zera o último octeto (IPv4) ou usa /64 (IPv6); documentar a base legal | E1-P06, E1-F14 |
| **S-B05** | `/health/ready` exposto publicamente revela a topologia | Servir `/health/*` só na porta interna (S-A06); o healthcheck do compose usa a porta interna | E1-F08 |
| **S-B06** | Objetos em `Quarentena`/`Rejeitado` guardados para sempre | `Rejeitado`: apagar na hora; `Quarentena`: 30 dias e depois apagar (lifecycle do MinIO ou job do Worker) | Épico 4 |
| **S-B07** | `Seed__AdminSenha` fica no `.env` depois do primeiro boot | Validar pela política de senha; documentar a remoção do `.env` depois do seed; opcional: flag `DeveTrocarSenha` no primeiro login (**[CONTRATO]**) | E1-F07 |
| **S-B08** | Contêineres de terceiros rodam como root (a imagem `minio/minio` roda como root por padrão; `savonet/liquidsoap` precisa ser conferida) e não há hardening | `user: "1000:1000"` no MinIO com o volume ajustado; em todos os serviços, `cap_drop: [ALL]` (com `cap_add` só do necessário), `security_opt: [no-new-privileges:true]`, `read_only: true` + `tmpfs` onde der, `mem_limit`/`pids_limit`. A CI (E1-F17) roda `trivy image` (falha em HIGH/CRITICAL corrigível), `dotnet list package --vulnerable` e `npm audit --audit-level=high` | E1-F13, E1-F16, E1-F17 |
| **S-B09** | *(v1.1)* O Worker confia no `result.json` e no `out.mp3` do sanitizer; um sanitizer comprometido pode mentir, ou mexer nos jobs de outros uploads no `tmpfs` compartilhado | Tratar o `result.json` como entrada não confiável (schema estrito, faixas de valor); conferir no Worker o tamanho do `out.mp3` (≤ limite) e os magic bytes de MP3; montar **um job por vez**, ou subdiretório por job montado só durante o job | Épico 4 |
| **S-B10** | *(v1.1)* A recodificação de imagem (S-A05) roda **dentro da API**, que tem JWT e pepper: é a mesma classe de risco de parser do S-C01, só que menor (só Admin, ≤ 2 MB) | Épico 3: aceitável na API com o `RequestSizeLimit` e as dimensões conferidas no **cabeçalho** antes de decodificar (limite de pixels). Quando o sanitizer existir (Épico 4), mover a recodificação de imagem para ele | Épico 3/4 |
| **S-B11** | *(v1.1)* Healthcheck do Redis com `redis-cli -a <senha>` expõe a senha nos argumentos do processo (e o redis-cli avisa isso) | `REDISCLI_AUTH="$(cat /run/secrets/redis_password)" redis-cli ping` | E1-F16 |

---

## 5. Política de segredos

### 5.1 Inventário

| Segredo | Quem usa (só estes) | Geração | Mínimo | Rotação |
|---|---|---|---|---|
| `Jwt__SigningKey` | api | `openssl rand -base64 48` | 32 bytes **decodificados** (validado no boot) | 90 dias. Aceitar `Jwt__SigningKeyAnterior` na validação por 15 min (o tempo de vida do access token), com `kid` no header. O refresh é opaco e não é afetado |
| `Seguranca__Pepper` | api (o Worker **não** precisa) | `openssl rand -base64 32` | 32 bytes | **Só em caso de vazamento.** Rotacionar quebra o `meus pedidos`, o grupo `listener:{hash}` e o anti-spam da janela atual (aceitável). Sensibilidade alta: IPv4 tem só 2³² valores, e **sem o pepper** o `IpHash` é revertido por força bruta em minutos |
| `Playout__Token` | api, liquidsoap | `openssl rand -hex 32` | 32 bytes | 180 dias, com `Playout__TokenAnterior` durante a troca (S-A06) |
| `ICECAST_SOURCE_PASSWORD` | icecast, liquidsoap | `openssl rand -base64 24` | 24 caracteres | 180 dias ou quando houver incidente |
| `ICECAST_ADMIN_PASSWORD` (+ `ICECAST_ADMIN_USER` ≠ `admin`) | icecast | `openssl rand -base64 24` | 24 caracteres | 180 dias |
| `ICECAST_RELAY_PASSWORD` | icecast | `openssl rand -base64 24` | 24 caracteres | junto com a anterior |
| `LIQUIDSOAP_HARBOR_PASSWORD` | liquidsoap (até o Épico 2; depois, login por locutor, S-A08) | `openssl rand -base64 24` | 24 caracteres | a cada saída de locutor da equipe, e a cada 90 dias |
| `MSSQL_SA_PASSWORD` | sqlserver, **db-init** | base64url de 32 bytes (ver a nota abaixo da tabela), **gerando de novo** até ter ao menos uma maiúscula, uma minúscula e um dígito (a política do SQL exige 3 classes) | 24 caracteres | anual; nunca nas aplicações |
| `DB_MIGRATOR_PASSWORD` | db-init (cria), migrator | idem | 24 caracteres | anual |
| `DB_APP_PASSWORD` | db-init (cria), api, worker | idem | 24 caracteres | 180 dias (`ALTER LOGIN`, e depois recriar os contêineres) |
| `DB_RELATORIO_PASSWORD` | db-init (cria), ferramenta de relatório | idem | 24 caracteres | 180 dias |
| `MINIO_ROOT_USER` / `MINIO_ROOT_PASSWORD` | minio, minio-init | `openssl rand -hex 16` / `-base64 32` | 32 caracteres | anual; nunca nas aplicações (S-A04) |
| `MINIO_API_ACCESS_KEY`/`SECRET` | api | criada pelo `minio-init` | 40 caracteres | 180 dias |
| `MINIO_WORKER_ACCESS_KEY`/`SECRET` | worker | criada pelo `minio-init` | 40 caracteres | 180 dias |
| `REDIS_PASSWORD` | redis, api, worker (se usar) | `openssl rand -base64 32` | 32 caracteres | 180 dias |
| `Seed__AdminEmail` / `Seed__AdminSenha` | api (só no primeiro boot) | senha: `openssl rand -base64 18` | política de senha (≥ 12) | tirar do ambiente depois do seed (S-B07) |

**Alfabeto dos segredos gerados (v1.1, contrato com o Atlas no `infra/sql/logins.sql`):** todo segredo gerado pelo `gerar-segredos` usa só `[A-Za-z0-9_-]` (base64url sem padding: `openssl rand -base64 N | tr '+/' '-_' | tr -d '=\n'`, ou `Base64Url` do .NET; o `\n` sai porque o `openssl` quebra a linha a cada 64 caracteres). O motivo: as senhas SQL entram no `CREATE LOGIN` por **substituição de texto** do sqlcmd, e uma aspa vira SQL executado como SA. Além disso, `+ / = ; , ! '` quebram connection strings, URLs de source client e o XML do Icecast. Onde a tabela acima diz `-base64`, leia base64url. **Chaves binárias** (`Jwt__SigningKey`, `Seguranca__Pepper`): a API decodifica com `System.Buffers.Text.Base64Url.DecodeFromChars` (.NET 9+), **nunca** com `Convert.FromBase64String`, que rejeita `-`, `_` e a falta de padding. O fail-fast da F06 mede o tamanho **depois** dessa decodificação, e o teste da F06 usa uma chave com `-` e `_` e sem padding. **Revoga a receita da v1** ("acrescentar `Aa1!`"), que o próprio snippet de checagem do Atlas rejeita. A checagem no entrypoint do `db-init` também precisa recusar valor **vazio** (o `case *[!A-Za-z0-9_-]*` sozinho aceita a string vazia) e valor menor que o mínimo da tabela.

Itens que não estavam na lista do Nexus e foram acrescentados: **Redis**, **credenciais de serviço do MinIO**, **senha de relay do Icecast** e o login do **db-init**.

### 5.2 `.env.example`
- Todo segredo aparece com o valor `__GERAR__` e, na linha anterior, um comentário com o comando de geração e o mínimo. Nenhum valor funcional (S-C02).
- Valores que **não** são segredo podem vir preenchidos (`Storage__PublicEndpoint=http://localhost:8080/storage`, nomes de bucket, `ASPNETCORE_ENVIRONMENT=Development`).
- O cabeçalho do arquivo diz: "rode `infra/scripts/gerar-segredos.sh`; nunca copie este arquivo como está".
- O `.gitignore` inclui `.env`, `.env.*` (exceto `.env.example`) e `secrets/`.

### 5.3 Variável de ambiente × Docker secrets
**Recomendação: variável de ambiente (`.env`) só em dev; Docker/Compose `secrets` (arquivo em `/run/secrets`) em produção.**
Motivo: variáveis de ambiente aparecem em `docker inspect`, em `/proc/<pid>/environ` (legível por qualquer RCE, que é exatamente o vetor do S-C01), em dumps de crash e em ferramentas de diagnóstico. Segredo em arquivo, com `mode: 0400` e o `uid` do serviço, reduz essa exposição.

Como fica cada serviço:
- **.NET (api, worker, migrator):** `builder.Configuration.AddKeyPerFile("/run/secrets", optional: true)`. O arquivo `Jwt__SigningKey` vira a chave `Jwt:SigningKey` (o delimitador `__` é o padrão do KeyPerFile). O código não muda entre dev e prod.
- **MinIO:** suporta `MINIO_ROOT_USER_FILE`/`MINIO_ROOT_PASSWORD_FILE` nativamente.
- **SQL Server:** a imagem não lê `_FILE` (confirmar na tag fixada). Usar um entrypoint wrapper que exporta `MSSQL_SA_PASSWORD` a partir de `/run/secrets` e então chama o entrypoint original.
- **Icecast:** o entrypoint renderiza o `icecast.xml` a partir dos arquivos de segredo (E1-F14) num `tmpfs`, nunca numa camada da imagem.
- **Liquidsoap:** `file.contents("/run/secrets/harbor_password")` no `radio.liq`.
- **Redis:** `redis.conf` com `requirepass`, gerado do segredo no entrypoint.
- **Connection strings:** montar em runtime (`Server=...;User Id=webradio_app;Password=<arquivo>`), sem gravar a string inteira num único segredo copiado entre serviços.

Em nenhum caso o segredo entra em `Dockerfile`, `ARG`, camada de imagem, log ou mensagem de erro. O Serilog tem um destructuring policy que mascara propriedades chamadas `*Password*`, `*Senha*`, `*Token*`, `*Key*` e `*Pepper*`.

### 5.4 Resposta a vazamento
1. Rotacionar o segredo (a tabela 5.1 diz o impacto de cada um).
2. `Jwt__SigningKey` vazada: rotacionar **sem** período de aceitação da anterior e revogar todas as famílias de refresh (`UPDATE seg.RefreshToken SET RevogadoEmUtc = SYSUTCDATETIME() WHERE RevogadoEmUtc IS NULL`).
3. Remover o segredo do histórico do git (`git filter-repo`) **e** rotacionar mesmo assim. Um segredo que foi versionado é considerado comprometido.

---

## 6. Autenticação e JWT: requisitos para E1-F07

- `TokenValidationParameters`: `ValidAlgorithms = [HS256]`, `ValidateIssuer/Audience/Lifetime/IssuerSigningKey = true`, `ClockSkew = 30 s`, `RequireExpirationTime = true`, `RequireSignedTokens = true`; `MapInboundClaims = false`, com `RoleClaimType = "role"` e `NameClaimType = "name"`.
- O token de refresh tem 256 bits de `RandomNumberGenerator`; guardar o SHA-256 (já está na spec) e compará-lo por busca no índice único, nunca por `==` de string em memória.
- A desativação precisa valer no refresh (já está na spec) **e** derrubar o hub (S-M08). O access token de até 15 min continua válido e esse risco é aceito.
- Critérios de teste extras para E1-F07: token com `alg: none` → 401; token assinado com outra chave → 401; `role` alterada no payload → 401; reuso de refresh depois da janela de tolerância → família revogada (S-M10); desativar um usuário → o refresh seguinte → 401.

---

## 7. Itens que mudam contrato (para o Nexus decidir)

| Item | Mudança proposta | Origem |
|---|---|---|
| Serviço `sanitizer` isolado | Novo serviço no compose; o Worker fala com ele por volume ou pipe | S-C01 |
| Endpoints internos em porta própria (`:8081`) | O Liquidsoap usa `http://api:8081/api/v1/internal/*`; `/health/*` também | S-A06, S-B05 |
| Storage na mesma origem (`/storage/`) | `Storage__PublicEndpoint` passa a ser `<origem>/storage`; a porta 9000 deixa de ser publicada | S-A03 |
| `POST /divulgacoes/imagens` | Deixa de devolver POST policy e passa a receber multipart ≤ 2 MB; a API recodifica | S-A05 |
| `POST /internal/harbor/auth` (Épico 2) | Autenticação do ao vivo por locutor | S-A08 |
| `ArquivoMidia` | `HashOriginalSHA256`, `EtagUpload`, `TentativasSanitizacao`, `EmAnaliseDesdeUtc` | S-M02, S-M03, S-M04 |
| Resposta de conta bloqueada | 401 genérico (ou 423 também para e-mail inexistente) | S-A12 |
| `db-init` separado do `migrator` | Dois one-shots; a SA só no primeiro | S-A10 |
| Redes do compose | `borda`, `app`, `dados`, `playout` no lugar de `borda` + `interna` | S-A10 |
| `Usuario.DeveTrocarSenha` (opcional) | Troca obrigatória no primeiro login do Admin semeado | S-B07 |

---

## 8. Checklist do quality gate de segurança (por PR)

- [ ] Nenhum segredo no diff (gitleaks verde); `.env.example` só com `__GERAR__`.
- [ ] Endpoint novo tem política de autorização explícita (`RequireAuthorization`/`AllowAnonymous`); nada anônimo por omissão (`FallbackPolicy` = autenticado).
- [ ] Entrada validada por FluentValidation com tamanho máximo; nenhum dado do usuário concatenado em SQL, URI do Liquidsoap, comando de shell ou cabeçalho HTTP.
- [ ] Nenhum log com e-mail, senha, token, IP em claro ou `listenerId`.
- [ ] Contêiner novo: tag ou digest fixado, usuário não root, `cap_drop: [ALL]`, sem porta publicada sem justificativa escrita.
- [ ] Mudança no nginx: os testes de bypass do S-A06 e do S-A07 continuam passando.
- [ ] `trivy`/`npm audit`/`dotnet list package --vulnerable` sem HIGH/CRITICAL corrigível.

---

## 9. CI como gate de segurança (D23, E1-F17)

> Critérios que o Sentinel aplica no PR do workflow. Runners hospedados pelo GitHub, **nenhum segredo cadastrado** no Épico 1.
> **Atualização (2026-09-27):** o repositório `arthur044/WebRadio` agora é **público**, com a `main` protegida. Com isso: branch protection e required checks passam a valer no plano Free (o item "exige GitHub Pro" da 9.5 caiu); CodeQL, upload de SARIF e *private vulnerability reporting* ficam gratuitos (ver 9.6); PR de fork roda com token só de leitura e sem segredos, e exige aprovação de todo contribuidor externo.

### 9.1 Gatilhos e permissões
- [ ] Gatilhos: só `pull_request` e `push` para `main` (e `workflow_dispatch`, se preciso). **Proibidos:** `pull_request_target`, e `workflow_run` que faça checkout do código do PR. Runner self-hosted também é proibido.
- [ ] `permissions: {}` no topo do workflow e, em cada job, só o necessário (em geral `contents: read`). Nada de `write-all`. `security-events: write` **só** no job que sobe SARIF (CodeQL/trivy), e nunca junto com um passo que execute código do PR fora do analisador.
- [ ] `actions/checkout` com `persist-credentials: false`. `fetch-depth: 0` só no job do gitleaks.
- [ ] Nas configurações do repositório: *Workflow permissions* = **read**; *Allow GitHub Actions to create and approve pull requests* = **off**; *Fork pull request workflows from outside collaborators* = **Require approval for all outside collaborators** (em repositório público, PR de fork sempre pode rodar workflow, com token só de leitura e sem segredos; ver 9.6).
- [ ] `timeout-minutes` em todo job; `concurrency` com `cancel-in-progress` para PR.

### 9.2 Cadeia de suprimentos das Actions e ferramentas
- [ ] Toda `uses:` fixada por **SHA completo de 40 caracteres**, com a tag num comentário (`# v4.2.2`). Nada de `@v4` ou `@main`. Actions de terceiros só se não houver alternativa oficial (`actions/*`) ou o binário oficial.
- [ ] `.github/dependabot.yml` com o ecossistema `github-actions` (além de `nuget`, `npm` e `docker`), para os SHAs não envelhecerem.
- [ ] Ferramenta baixada como binário (gitleaks, trivy) tem **versão fixa e checksum SHA-256 conferido** antes de executar. O hash esperado fica **fixado no próprio workflow** (`echo "<sha256>  arquivo" | sha256sum -c`). Baixar o `checksums.txt` da mesma release só prova que o download não se corrompeu; não protege de uma release adulterada. Nada de `curl ... | sh`.
- [ ] Nas configurações de Actions do repositório: `sha_pinning_required = true` e `allowed_actions = selected` (Actions do GitHub + a lista explícita), para que a plataforma também imponha a fixação por SHA.
- [ ] Gitleaks: o repositório é de conta **pessoal**, então a `gitleaks-action` dispensa licença. Mesmo assim, prefiro o binário com checksum, que não depende de licença. Precisa de um `.gitleaks.toml` com allowlist **só** para o valor `__GERAR__`.

### 9.3 Segredos e logs
- [ ] Os segredos de teste são gerados **por execução** (`infra/scripts/gerar-segredos.sh`). Cada valor é mascarado com `echo "::add-mask::$valor"` **antes** de qualquer outro uso, e o arquivo gerado fica fora do workspace versionado.
- [ ] **`docker compose config` imprime a configuração já interpolada, com os segredos.** Na CI, usar só `docker compose config --quiet` (valida sem imprimir), ou rodá-lo contra o `.env.example`.
- [ ] Nenhum `set -x`, `env`, `printenv`, `cat .env` ou `docker inspect` nos passos. Em caso de falha, o dump de `docker compose logs` roda depois do mascaramento e não inclui o `db-init` (que recebe a SA).
- [ ] **Script injection:** nenhuma expressão `${{ github.event.* }}`, `${{ github.head_ref }}` ou título/corpo de PR interpolada dentro de `run:`. Quando precisar, passar por `env:` e referenciar como `"$VAR"`.
- [ ] Artifacts (traces do Playwright, relatórios) com `retention-days: 7`. Nunca publicar `.env`, `secrets/` nem dumps de contêiner. Traces do Playwright podem conter access tokens: gravar só em falha (`trace: 'retain-on-failure'`).

### 9.4 Cache sem envenenamento
- [ ] Chave de cache derivada **só** de lockfiles: `packages.lock.json` (com `RestorePackagesWithLockFile=true` e `dotnet restore --locked-mode`) e `package-lock.json` (com `npm ci`). Preferir o cache embutido de `actions/setup-dotnet`/`actions/setup-node`.
- [ ] Cachear **só** os downloads dos gerenciadores de pacote (`~/.nuget/packages`, `~/.npm`). **Nunca** cachear `bin/`, `obj/`, `dist/`, `node_modules/` ou ferramentas executáveis: o conteúdo de um restore é conferido pelo hash do lockfile, mas uma saída de build não é conferida por nada.
- [ ] Cache de camadas Docker (`type=gha`): `cache-to` **só** em `push` para `main`; em PR, só `cache-from`. Um PR não grava cache que `main` vá ler.
- [ ] Nenhum job com gatilho privilegiado (`pull_request_target`, `workflow_run`) grava cache. Isso já sai de graça com a proibição da 9.1, mas fica registrado porque é o vetor clássico de envenenamento.

### 9.5 Os jobs de gate
- [ ] **`seguranca`:** gitleaks no histórico completo **e** a checagem do `.env.example`. A checagem tem de falhar quando **qualquer** chave com `PASSWORD|SECRET|SENHA|TOKEN|KEY|PEPPER` no nome tiver valor diferente de `__GERAR__`:
  ```bash
  # Não usar "! grep ...": com o "bash -e" do Actions, um pipeline negado que não é
  # o último comando do passo não derruba o passo.
  # -i é obrigatório: as chaves .NET são em caixa mista (Jwt__SigningKey, Seguranca__Pepper).
  violacoes=$(grep -iE '^[A-Za-z0-9_]*(PASSWORD|SECRET|SENHA|TOKEN|KEY|PEPPER)[A-Za-z0-9_]*=' .env.example \
      | grep -vE '=__GERAR__$' | cut -d= -f1 || true)
  if [ -n "$violacoes" ]; then
    echo "::error::Segredo com valor real no .env.example: $violacoes"   # só os nomes, nunca o valor
    exit 1
  fi
  ```
- [ ] **`vulnerabilidades`:**
  - `dotnet list package --vulnerable --include-transitive` **retorna 0 mesmo com vulnerabilidade**. O job precisa procurar na saída a frase "has the following vulnerable packages" e falhar. Alternativa: `NuGetAudit` com `NuGetAuditMode=all` + `NuGetAuditLevel=high` + `TreatWarningsAsErrors` (NU1903/NU1904), que já vem com o `Directory.Build.props`.
  - `npm audit --audit-level=high` (sai com código diferente de 0 sozinho).
  - `trivy image --exit-code 1 --severity HIGH,CRITICAL --ignore-unfixed` em cada imagem construída, mais `trivy fs` nos lockfiles.
- [x] **O gate só vale se for obrigatório.** Com o repositório público, a branch protection funciona no plano Free: a `main` exige `Segurança (gitleaks)` e `Backend (unit + arquitetura)`. O `vulnerabilidades` vira *required* quando o job existir (F17b). (Até 2026-09-27, com o repositório privado, isso exigia GitHub Pro.)
### 9.6 Repositório público
- [ ] CodeQL (*default setup*) ligado para `actions`, `csharp` e `javascript-typescript`. A linguagem `actions` pega script injection e gatilhos perigosos nos próprios workflows.
- [ ] Dependabot *alerts* e *security updates* ligados; o `dependabot.yml` (Forge) cobre `github-actions`, `nuget`, `npm` e `docker`.
- [ ] *Private vulnerability reporting* ligado + `SECURITY.md` explicando como reportar em privado.
- [ ] Achados **em código já implementado e ainda não corrigido** vão para um *draft security advisory* privado (ou para o `maestri`), não para arquivo versionado. O `05` descreve requisitos de projeto e continua público.

### 9.7 Orçamento
- [ ] ~~Orçamento de 2.000 min/mês~~ (não vale mais: Actions em repositório público não consomem minutos). Mesmo assim, manter o escopo abaixo para o PR não ficar lento.
- [ ] Orçamento (histórico): repositório privado no plano Free tem 2.000 min/mês de Actions. Trivy, Testcontainers e o compose de teste de fumaça consomem bastante. Rodar o `vulnerabilidades` completo em PR que toca lockfile ou Dockerfile e numa agenda diária (`schedule`), não em todo push.
