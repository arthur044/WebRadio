# Guia de deploy: colocando a WebRadio no ar

Este guia leva a rádio do zero até o ar, num servidor seu, com endereço seguro (`https://`) e backup. Foi escrito para o **dono do produto**, que não precisa ser programador: cada passo diz **o que digitar**, **o que você deve ver** e **o que fazer se não for isso**. Leia tudo uma vez antes de começar: não há pressa para subir, e alguns passos (como guardar a senha do backup) precisam acontecer **antes** de outros.

Os blocos cinza com comandos são digitados no **terminal do servidor** (uma janela de texto que você abre no seu computador e que "fala" com o servidor). Quando o passo for no seu computador ou no site de um provedor, o texto diz isso.

> Este é o guia da tarefa E5-L01 do [`09-epico-5-tarefas.md`](specs/09-epico-5-tarefas.md). O deploy de verdade só acontece **depois** da revisão dos riscos aceitos com você (E5-N01, a lista está no fim daquele arquivo).

## Índice

1. [O que você precisa ter antes](#1-o-que-você-precisa-ter-antes)
2. [Requisitos da VPS](#2-requisitos-da-vps)
3. [Domínio](#3-domínio)
4. [Preparar a VPS](#4-preparar-a-vps)
5. [Baixar o projeto, configurar e gerar os segredos](#5-baixar-o-projeto-configurar-e-gerar-os-segredos)
6. [Guardar os segredos fora do servidor](#6-guardar-os-segredos-fora-do-servidor)
7. [Criar o bucket externo (a cópia do backup fora do servidor)](#7-criar-o-bucket-externo-a-cópia-do-backup-fora-do-servidor)
8. [Primeira subida](#8-primeira-subida)
9. [Conferência manual: a rádio está de pé?](#9-conferência-manual-a-rádio-está-de-pé)
10. [Ligar o backup (cópia externa e timer das 03:30)](#10-ligar-o-backup-cópia-externa-e-timer-das-0330)
11. [Locutor ao vivo (opcional)](#11-locutor-ao-vivo-opcional)
12. [Atualizar a rádio](#12-atualizar-a-rádio)
13. [Servidor novo ou banco perdido: restaurar](#13-servidor-novo-ou-banco-perdido-restaurar)
14. [Depois de uma invasão suspeita](#14-depois-de-uma-invasão-suspeita)
15. [Acompanhar no dia a dia](#15-acompanhar-no-dia-a-dia)
16. [Índice dos runbooks](#16-índice-dos-runbooks)

## 1. O que você precisa ter antes

| Item | Para quê |
|---|---|
| Uma **VPS** (servidor virtual) Linux, com IP público IPv4 | Onde a rádio roda. Requisitos na seção 2 |
| Um **domínio** (ex.: `radio.exemplo.com.br`) | O endereço que os ouvintes digitam. Seção 3 |
| Um **e-mail real**, que você lê | O Let's Encrypt (que emite o certificado do cadeado) avisa se algo der errado com a renovação |
| Uma conta num **storage externo compatível com S3** (este guia usa o Backblaze B2) | Guarda uma cópia do backup **fora** do servidor. Seção 7 |
| Um **gerenciador de senhas** (ou um cofre criptografado) | Guardar a senha do backup e os segredos **fora** do servidor. Seção 6 |
| Um computador com terminal (Windows, macOS ou Linux) e uma chave SSH | Para falar com o servidor |

**Regras de ouro**

1. **A `restic_senha` é a senha do backup. Sem ela, nenhum backup abre, nem o local nem o externo.** Guarde-a fora do servidor **antes** da primeira subida (seção 6).
2. **Nunca** cole segredos (senhas, chaves) em conversa, e-mail, chamado ou captura de tela. Se um comando mostrar um segredo na tela, não compartilhe a tela.
3. **Nunca** digite `docker compose down -v` em produção: apaga os certificados e o backup que está no servidor.
4. Se um passo não der o resultado esperado, **pare** e veja a tabela "O que fazer se" daquele passo ou o runbook indicado. Não improvise comandos.

**Decisões que são suas** (o time explica cada uma na revisão de riscos):

- **Perda máxima de 1 dia.** O backup roda uma vez por dia (03:30). Se o servidor for perdido, voltam os dados até o último backup, no máximo 1 dia atrás. Dá para trocar por backup de hora em hora, com mais trabalho do time: é uma decisão de projeto, não deste guia.
- **A cópia externa não é à prova de invasor.** Quem tomar o servidor tem a chave que o backup usa e pode apagar versões definitivamente (seção 7). A proteção forte (Object Lock) fica para a revisão de riscos.
- **Sem alertas automáticos na v1.** Quem acompanha a saúde da rádio é uma pessoa, olhando os avisos (seção 15) e um monitor externo gratuito.

## 2. Requisitos da VPS

Os serviços da rádio somam cerca de 7,6 GB de memória reservada (o banco SQL Server sozinho usa 2 GB, o antivírus 1,5 GB). A tabela abaixo vem da decisão E5-D5 da spec.

| | Mínimo | Recomendado |
|---|---|---|
| Processadores (vCPU) | 4 | 4 ou mais |
| Memória (RAM) | 10 GB, mais 2 GB de swap | 16 GB |
| Disco (SSD) | 80 GB | 160 GB |
| Sistema | Ubuntu 24.04 LTS ou Debian 12/13 | o mesmo |
| Internet | IPv4 público | 128 kbps por ouvinte: 200 ouvintes ≈ 26 Mbps sustentados |

**Como pensar no disco.** A biblioteca de músicas (cerca de 5 MB por música) existe **três vezes** no servidor: no armazenamento da rádio (MinIO), numa cópia de trabalho do backup (o "espelho") e no repositório do backup (que deduplica, mas ainda ocupa lugar). Some a isso o banco, o sistema e o histórico do backup. **Regra prática: planeje o disco em, no mínimo, 3 vezes o tamanho da biblioteca que você espera ter, mais folga.** Os 80 GB do mínimo cobrem uma biblioteca de poucos milhares de músicas.

Escolha um provedor que deixe você abrir uma **chave SSH** no ato de criar a VPS e que mostre o **IP público** do servidor.

## 3. Domínio

1. No site onde você registrou o domínio (o "painel de DNS"), crie um registro **tipo A** com o nome do endereço da rádio (ex.: `radio`, para `radio.exemplo.com.br`) apontando para o **IP da VPS**.
2. **Não crie registro AAAA** (o equivalente para IPv6). A v1 funciona só com IPv4: com IPv6 ligado, os limites por IP deixariam de enxergar o ouvinte de verdade e a rádio poderia bloquear gente por engano (decisão E5-D12).
3. Espere o DNS "pegar" (de minutos a algumas horas) e confira **no seu computador**:

```bash
nslookup radio.exemplo.com.br
```

Você deve ver o **IP da VPS** na resposta. Se aparecer outro IP, ou nenhum, espere mais e confira o registro no painel. **Só siga quando o IP estiver certo**: sem isso o Let's Encrypt não emite o certificado, e tentar de novo e de novo pode travar a emissão do seu domínio por dias (o Let's Encrypt limita as tentativas).

## 4. Preparar a VPS

Cada passo abaixo existe por um motivo de segurança. Faça na ordem.

### 4.1 Entrar no servidor e criar um usuário sem poderes de root

No seu computador, entre com o usuário e o IP que o provedor mostrou (em geral `root`):

```bash
ssh root@IP-DA-VPS
```

Crie um usuário próprio (aqui, `webradio`; o sistema pede uma senha: guarde-a no gerenciador) e copie para ele a sua chave SSH:

```bash
adduser webradio
usermod -aG sudo webradio
rsync --archive --chown=webradio:webradio ~/.ssh /home/webradio
```

**Abra uma segunda janela** de terminal e confira que consegue entrar com o usuário novo, **sem fechar a primeira**:

```bash
ssh webradio@IP-DA-VPS
```

Você deve entrar sem pedir a senha do SSH (só uma chave). Se não entrou, não feche a primeira janela: corrija ali.

### 4.2 SSH só com chave

Ainda como `webradio`, edite a configuração do SSH (`nano` é um editor de texto simples: salve com Ctrl+O e saia com Ctrl+X):

```bash
sudo nano /etc/ssh/sshd_config
```

Deixe estas três linhas assim (sem `#` no começo):

```
PasswordAuthentication no
KbdInteractiveAuthentication no
PermitRootLogin no
```

Algumas VPS têm arquivos em `/etc/ssh/sshd_config.d/` que mandam o contrário. Confira o resultado final e recarregue:

```bash
sudo sshd -T | grep -E 'passwordauthentication|kbdinteractiveauthentication|permitrootlogin'
sudo sshd -t && sudo systemctl reload ssh
```

Você deve ver `passwordauthentication no`, `kbdinteractiveauthentication no` e `permitrootlogin no`. **Teste numa terceira janela** (`ssh webradio@IP-DA-VPS`) antes de fechar as outras. Se a conta "root" ou a senha ainda entram, volte e procure a linha contrária nos arquivos de `/etc/ssh/sshd_config.d/`.

### 4.3 Atualizações de segurança automáticas

```bash
sudo apt update && sudo apt install -y unattended-upgrades
sudo dpkg-reconfigure -plow unattended-upgrades
```

Responda **Sim** na tela que aparecer. O sistema passa a instalar sozinho as correções de segurança.

### 4.4 Swap de 2 GB (se o provedor não deu)

O swap é um "reforço" de memória no disco. Confira se já existe:

```bash
free -h
```

Se a linha `Swap` mostrar `0B`, crie 2 GB:

```bash
sudo fallocate -l 2G /swapfile && sudo chmod 600 /swapfile
sudo mkswap /swapfile && sudo swapon /swapfile
echo '/swapfile none swap sw 0 0' | sudo tee -a /etc/fstab
```

### 4.5 Firewall: só as portas necessárias

O firewall decide quem consegue bater na porta do servidor. A rádio usa **22** (SSH, o seu acesso), **80** e **443** (o site, em HTTP e HTTPS). Mais a porta do **ao vivo**, se você ligar (seção 11). O **HTTP/3 (443 UDP) fica fechado** de propósito.

```bash
sudo apt install -y ufw
sudo ufw default deny incoming
sudo ufw default allow outgoing
sudo ufw limit from 0.0.0.0/0 to any port 22 proto tcp
sudo ufw allow from 0.0.0.0/0 to any port 80 proto tcp
sudo ufw allow from 0.0.0.0/0 to any port 443 proto tcp
sudo ufw enable
sudo ufw status verbose
```

Escrevendo `0.0.0.0/0` as regras valem só para IPv4, e o resto (inclusive todo o IPv6, desde que o firewall o gerencie, veja abaixo) fica barrado pelo `deny incoming`. A resposta de `ufw status` deve listar só 22, 80 e 443 (a do 22 aparece como `LIMIT`: o firewall passa a recusar quem tenta entrar por SSH muitas vezes seguidas).

**Confira que o firewall cuida também do IPv6**, senão o SSH fica aberto por IPv6:

```bash
grep '^IPV6=' /etc/default/ufw
```

Deve mostrar `IPV6=yes` (é o padrão). Se mostrar `IPV6=no`, edite o arquivo para `yes` e rode `sudo ufw disable && sudo ufw enable`. **Não ligue o IPv6 no Docker** (já vem desligado).

> **Aviso.** O Docker abre as portas que publica **por conta própria**, passando por cima do `ufw`. Aqui isso é aceitável porque o projeto só publica 80 e 443 (e a porta do ao vivo, quando você a liga, que se protege pela lista `HARBOR_PERMITIDOS`, seção 11). Por isso, se o seu provedor oferece um **firewall no painel dele**, ligue-o também, com as mesmas portas: é uma segunda camada fora do servidor.

### 4.6 Instalar o Docker

Siga o guia oficial para o seu sistema, **a opção "Install using the apt repository"**: <https://docs.docker.com/engine/install/ubuntu/> (Ubuntu) ou <https://docs.docker.com/engine/install/debian/> (Debian). Depois:

```bash
sudo usermod -aG docker webradio
```

Saia e entre de novo no servidor (para valer) e confira:

```bash
docker --version
docker compose version
```

Você deve ver as duas versões. **Atenção:** quem está no grupo `docker` tem, na prática, poder de administrador do servidor. Por isso o acesso SSH só com chave (4.2) é importante.

### 4.7 Conferir o relógio

O backup roda às 03:30 **no horário do servidor**. Confira:

```bash
timedatectl
```

Se o fuso não for o seu (`Time zone`), ajuste: `sudo timedatectl set-timezone America/Sao_Paulo`.

## 5. Baixar o projeto, configurar e gerar os segredos

### 5.1 Baixar o projeto

O projeto fica em `/opt/webradio` (o timer do backup, na seção 10, espera esse lugar). O time informa o endereço do repositório e a **versão** (tag) a instalar, algo como `v1.0.0`.

```bash
sudo mkdir -p /opt/webradio && sudo chown webradio:webradio /opt/webradio
git clone <endereço-do-repositório> /opt/webradio
cd /opt/webradio
git checkout <versão>
```

Se o repositório for privado, o time cria uma chave de leitura (deploy key) para o servidor.

### 5.2 O arquivo `.env`: o endereço e o e-mail

O `.env` guarda a configuração que **não é secreta**. Crie-o a partir do modelo:

```bash
cp .env.example .env
chmod 600 .env
nano .env
```

Troque **duas** linhas e salve:

```
DOMINIO=radio.exemplo.com.br
ACME_EMAIL=seu-email-real@exemplo.com.br
```

- `DOMINIO`: o endereço **sem** `https://` e **sem** porta. Ele vira o certificado do cadeado e o nome do site em toda a rádio.
- `ACME_EMAIL`: o e-mail do Let's Encrypt.
- `NOME_RADIO` (já vem como `WebRadio`): o nome da sua rádio, que aparece no título da aba, na descrição e no player. Mude se quiser **antes da primeira subida** (muda o site montado).

> **Quem já tinha um `.env` de antes do Épico 5** precisa **acrescentar** as linhas `DOMINIO` e `ACME_EMAIL` (e conferir as demais contra o `.env.example`): sem elas o certificado não sai. Não apague o `.env` antigo: edite-o.

### 5.3 Gerar os segredos

Os segredos (senhas internas, chaves) ficam na pasta `secrets/`, **um arquivo por segredo**. O script cria todos, com valores aleatórios, e nunca sobrescreve um que já exista. Informe **o seu e-mail**: ele será o login do primeiro Administrador.

```bash
infra/scripts/gerar-segredos.sh prod seu-email@exemplo.com.br
```

Você deve ver linhas `secrets/... já existe — preservado.` só se rodar de novo, e no fim `secrets/: N segredo(s) novo(s)`. Os arquivos `backup_s3_access_key` e `backup_s3_secret_key` nascem **vazios**: serão preenchidos na seção 7.

A **senha do primeiro login do Administrador** está em `secrets/seed_admin_senha`. Para ver na tela (e só aí):

```bash
cat secrets/seed_admin_senha; echo
```

O primeiro login pede para trocar essa senha.

## 6. Guardar os segredos fora do servidor

**Faça isto agora, antes da primeira subida.** Se o servidor for perdido e você não tiver estes itens fora dele, o backup vira lixo ilegível.

**Obrigatórios** (num gerenciador de senhas ou cofre criptografado seu, nunca só no servidor):

| Item | Por quê |
|---|---|
| `secrets/restic_senha` | Abre os backups. **Sem ela, nada se recupera.** |
| `secrets/seguranca_pepper` | Trocá-lo zera os endereços anonimizados, a identificação de dispositivos e as sessões |
| Chaves do storage externo (`backup_s3_*`), o endereço (endpoint) e o nome do bucket | Para alcançar a cópia externa (guarde quando preencher, na seção 7) |

**Recomendado:** guardar a pasta `secrets/` **inteira**. Os demais segredos podem ser regerados na restauração (senhas internas do banco e do MinIO, chave do login; quem estiver logado entra de novo).

Para ver a `restic_senha` e copiá-la para o gerenciador de senhas:

```bash
cat secrets/restic_senha; echo
```

Para copiar a pasta inteira para o **seu computador**, **já criptografada** (assim nunca fica uma cópia em texto aberto no seu disco), rode **no seu computador** (Linux ou macOS, com o `gpg` instalado). Ele pede uma senha para o arquivo: **guarde-a no gerenciador de senhas**.

```bash
ssh webradio@IP-DA-VPS 'tar -C /opt/webradio -cz secrets' | gpg --symmetric --cipher-algo AES256 -o webradio-secrets.tar.gz.gpg
```

Guarde o arquivo `webradio-secrets.tar.gz.gpg` num lugar seguro (um cofre ou um pen drive guardado). Se o `gpg` for difícil para você, tudo bem: **copiar só os três itens obrigatórios** da tabela acima para o gerenciador de senhas já cobre o essencial.

**Teste de que guardou direito:** abra o gerenciador de senhas e confirme que você consegue ler a `restic_senha` de lá, sem acessar o servidor. **Refaça esta cópia a cada vez que um segredo mudar.**

## 7. Criar o bucket externo (a cópia do backup fora do servidor)

**O que é.** Um "bucket" é uma pasta na nuvem de uma empresa de armazenamento. O backup diário da rádio é enviado para lá, criptografado: quem guarda os arquivos não consegue ler o conteúdo. Se o servidor for perdido, quebrar ou for roubado, a cópia continua intacta.

**Sem o bucket**, o backup funciona **só dentro do servidor**: o log avisa `BACKUP_EXTERNO_NAO_CONFIGURADO`. É um aviso, não um erro, mas significa que **perder o servidor é perder o backup junto**. Faça este passo antes de abrir a rádio ao público.

**O que o bucket precisa ter** (E5-D11, vale em qualquer provedor):

1. Um bucket **só para o backup** da rádio (nada mais dentro).
2. **Versionamento ligado**: o provedor guarda as versões antigas de cada arquivo.
3. **Retenção de 30 dias das versões antigas**: um arquivo apagado ou sobrescrito continua recuperável por 30 dias.
4. Uma **chave de acesso só para esse bucket**, com leitura e escrita, e que **não** possa alterar as configurações do bucket (versionamento, ciclo de vida, bloqueio de objetos).

**O que esta proteção cobre e o que ela NÃO cobre.** O versionamento e a retenção de 30 dias protegem contra erro e contra um apagamento comum: se o backup do servidor (ou uma pessoa) "apaga" ou sobrescreve um arquivo, a versão antiga continua guardada por 30 dias e dá para recuperá-la pela **sua conta** no provedor. **Mas a chave de backup precisa poder apagar arquivos** (o backup remove as cópias velhas todo dia). Quem tomar o servidor e usar essa chave **pode apagar uma versão específica de forma definitiva**, e o ciclo de vida não impede isso. Ou seja, **hoje o bucket não garante** que a cópia externa sobreviva a um invasor com a chave. É um **risco conhecido**, levado à revisão de riscos com você (E5-N01). A proteção forte seria o **bloqueio de objetos (Object Lock)** com retenção de 30 dias; ela ainda precisa ser testada com o restic e decidida pelo time, por isso este guia manda deixá-lo desligado.

O passo a passo abaixo usa o **Backblaze B2**. Os nomes de botões vêm da documentação do provedor e podem mudar um pouco; se algo estiver diferente, guie-se pelos quatro requisitos acima e peça ajuda ao time.

### 7.1 Conta

1. Crie a conta em <https://www.backblaze.com/> e ative o **B2 Cloud Storage**.
2. Ative a **verificação em duas etapas** na conta (é a conta que recupera tudo num desastre).

### 7.2 Criar o bucket

1. No painel, menu **B2 Cloud Storage → Buckets**.
2. Botão **Create a Bucket**.
3. **Bucket Unique Name**: um nome só seu, em letras minúsculas e hífens, por exemplo `webradio-backup-seunome`. O nome é único no mundo; se já existir, o painel avisa.
4. **Files in Bucket are**: **Private** (privado).
5. **Object Lock**: deixe **desligado, a menos que a revisão de riscos (E5-N01) decida o contrário** (veja o "limite conhecido" no começo desta seção).
6. Clique em **Create a Bucket**.
7. Na lista de buckets, **copie o valor do campo "Endpoint"** do seu bucket (algo como `s3.us-west-004.backblazeb2.com`) e anote o **nome do bucket**.

O B2 já guarda **todas as versões** dos arquivos por padrão, então o versionamento (requisito 2) já vem ligado.

### 7.3 Retenção de 30 dias das versões antigas

1. Na lista de buckets, no seu bucket, clique em **Lifecycle Settings**.
2. Escolha a opção **Keep prior versions for this number of days** e digite **30**.
3. Salve.

A regra vale para todo o bucket e é aplicada uma vez por dia. Ela mantém os arquivos atuais sem limite e remove as versões antigas (apagadas ou sobrescritas) depois de 30 dias.

### 7.4 Criar a chave de acesso só para o backup

1. Menu **Application Keys** (em "Account", ou "Access Control", conforme a versão do painel) → **Add a New Application Key**.
2. **Name of Key**: `webradio-backup`.
3. **Allow access to Bucket(s)**: escolha **o bucket do backup** (nunca "All").
4. **Type of Access**: **Read and Write**. Não marque nenhuma opção extra de administração do bucket. Se o painel oferecer **Allow List All Bucket Names**, ela só deixa o programa ver os *nomes* dos buckets (não o conteúdo) e pode ser necessária: se o primeiro backup externo (seção 10.2) falhar com `credencial_ou_permissao_recusada`, é a primeira coisa a ativar.
5. Crie a chave.
6. O painel mostra **duas informações** (a segunda **aparece uma única vez**; copie para o gerenciador de senhas agora):
   - **keyID** (é a chave de acesso, a "access key");
   - **applicationKey** (é a chave secreta, a "secret key").

**Nunca use a "Master Application Key" (a chave mestra da conta) no servidor.** Ela tem poder sobre a conta inteira: use só a chave que você criou acima, restrita ao bucket.

**Confira antes de seguir** (o painel é usado com o login da sua *conta*, não com a chave, então a conferência é olhar a própria chave): na lista de **Application Keys**, abra a chave nova e veja as **permissões (capabilities)** que o painel mostra. Ela deve poder listar, ler, gravar e apagar arquivos, e **não** deve ter permissão de **alterar o bucket** (algo como `writeBuckets`) nem de **retenção ou Object Lock** (algo como `writeBucketRetentions` ou `bypassGovernance`; os nomes exatos podem variar). Se tiver, apague a chave e crie de novo, só com leitura e escrita neste bucket.

Se você preferir outro provedor (Wasabi, AWS S3, ou um MinIO em outro servidor), os quatro requisitos do começo desta seção são os mesmos.

### 7.5 Anotar nas configurações do servidor

Você terá, no total, quatro valores: o **endpoint**, o **nome do bucket**, o **keyID** e a **applicationKey**. Eles entram na seção 10. Guarde os quatro no gerenciador de senhas agora.

## 8. Primeira subida

```bash
cd /opt/webradio
docker compose up -d --wait
```

Na **primeira vez**, o servidor monta as imagens da rádio (vários minutos). Espere o comando voltar sozinho. Ao fim, confira:

```bash
docker compose ps
```

Você deve ver os serviços com `healthy` (ou `running`, os que não têm checagem). Se algum ficar `unhealthy` ou ficar reiniciando, olhe o log dele: `docker compose logs --tail 50 <nome-do-serviço>`.

Confira o certificado:

```bash
docker compose logs edge | grep -i 'certificate obtained'
```

Você deve ver uma linha com `certificate obtained`. Se não aparecer, veja `docker compose logs edge | grep -iE 'error|acme'` e a tabela de problemas em [`runbooks/borda-https.md`](runbooks/borda-https.md): as causas mais comuns são o DNS ainda não apontar para o servidor ou a porta 80 fechada.

> **Importante:** daqui em diante, **sempre** que subir a rádio, use o mesmo comando (`docker compose up -d --wait`). Se você ligar o ao vivo (seção 11), todo `up` passa a levar também o arquivo do ao vivo.

## 9. Conferência manual: a rádio está de pé?

Faça este roteiro **inteiro** antes de divulgar o endereço. Os itens 1 a 3 são a "Definição de Pronto" da borda HTTPS (itens 1 a 3 do Épico 5).

**1. O site abre com cadeado e o HTTP redireciona.**
No navegador, abra `https://radio.exemplo.com.br`: a página inicial deve abrir **com o cadeado**, sem aviso de certificado. Depois, no terminal do servidor:

```bash
curl -sI http://radio.exemplo.com.br/ | head -3
curl -sI https://radio.exemplo.com.br/ | grep -i strict-transport-security
```

O primeiro deve mostrar `308` e apontar para `https://`. O segundo deve mostrar **uma linha só** com `max-age=86400`.

**2. Só as portas certas estão abertas.**

```bash
ss -ltn '( sport = :80 or sport = :443 )'
```

Só pode haver linhas com `0.0.0.0`. Se aparecer `[::]` ou `*`, **pare** e fale com o time (é o IPv6, E5-D12).

**3. A rádio enxerga o ouvinte de verdade, não o Caddy.**
Este é o ponto mais traiçoeiro: se a rádio enxergasse todos os ouvintes como um só, o limite de 25 ouvintes por IP derrubaria a audiência no 26º. Conferência:

```bash
docker compose logs --no-color web | grep -v '127.0.0' | tail -5
```

(O `grep -v` tira as linhas do teste de saúde do próprio servidor, que mostram `127.0.0.0`.) Na coluna do IP deve aparecer **o seu IP** com o último número zerado (por exemplo `203.0.113.0`: a rádio anonimiza por privacidade), **não** `172.30.250.x`. Depois, peça a **duas pessoas em redes diferentes** (ou você no Wi-Fi e no 4G do celular) para abrir a rádio e tocar ao mesmo tempo: as duas devem ouvir, sem erro.

**4. Login do Administrador.**
Abra a página de entrada. Entre com o e-mail que você informou na seção 5.3 e a senha de `secrets/seed_admin_senha`. O sistema pede para **trocar a senha**: troque e guarde a nova no gerenciador.

**5. O player toca e não pausa.**
Na página inicial, aperte o play: o som deve começar. Navegue entre as páginas do site (Grade, Mural): **o som não pode parar**. Deixe tocando por **30 minutos** sem cortes (é o teste do Caddy com o stream; o endereço direto é `https://radio.exemplo.com.br/stream/radio.mp3`).

**6. Enviar uma música (biblioteca).**
Entre como Administrador ou Locutor, abra **Biblioteca** e envie um MP3 de **pelo menos 30 segundos**. O status deve ir de "Aguardando" a **Aprovada** em instantes (o antivírus e o conversor trabalham nele). Use a **pré-escuta** para ouvir. Depois de aprovada, a música entra na rotação e toca sozinha em algum momento: acompanhe em "Tocando agora".

**7. Pedido de música (opcional, mas recomendado).**
Como ouvinte, faça um pedido; como Administrador, abra a fila de moderação e escolha **Aprovar com mídia**, apontando a música da biblioteca. O pedido aprovado deve tocar sozinho.

**8. Ao vivo:** só se você ligou (seção 11).

Se algum item falhar, **não divulgue** o endereço: anote o item e o que apareceu e chame o time.

## 10. Ligar o backup (cópia externa e timer das 03:30)

O detalhe técnico de tudo isto está em [`runbooks/backup-e-restauracao.md`](runbooks/backup-e-restauracao.md); aqui vai só o caminho do primeiro dia.

### 10.1 Ligar a cópia externa

Edite o `.env` (`nano .env`) e acrescente as duas linhas com os valores da seção 7 (o endereço **tem que começar com `https://`**: com `http://` o backup recusa, para as chaves não irem em claro; e o endpoint **não leva o nome do bucket no fim**):

```
BACKUP_S3_ENDPOINT=https://s3.us-west-004.backblazeb2.com
BACKUP_S3_BUCKET=webradio-backup-seunome
```

Depois, grave as duas chaves **sem deixar rastro no histórico do terminal** (cada comando pede o valor, que não aparece na tela quando você cola):

```bash
read -rsp 'Cole o keyID e tecle Enter: ' V; echo
printf '%s' "$V" > secrets/backup_s3_access_key
read -rsp 'Cole a applicationKey e tecle Enter: ' V; echo
printf '%s' "$V" > secrets/backup_s3_secret_key
unset V
wc -c secrets/backup_s3_access_key secrets/backup_s3_secret_key
```

O `wc` deve mostrar números **maiores que zero** (o tamanho de cada chave). Não abra os arquivos na tela nem cole o conteúdo em lugar nenhum.

### 10.2 Primeiro backup, à mão

```bash
docker compose --profile backup run --rm backup
```

Pode levar alguns minutos. No fim você deve ver, entre as linhas:

- `BACKUP_ENVIO_OK destino=externo`
- `BACKUP_OK duracao_s=... externo=sim`

Se aparecer `BACKUP_EXTERNO_NAO_CONFIGURADO`, o `.env` ou os arquivos `secrets/backup_s3_*` não estão certos. Se aparecer `BACKUP_FALHOU etapa=... motivo=...`, procure o motivo na tabela de erros do runbook [`backup-e-restauracao.md`](runbooks/backup-e-restauracao.md#erros-comuns). **Não** copie a saída crua do comando em lugar nenhum.

### 10.3 O timer: backup automático todo dia às 03:30

```bash
sudo cp infra/backup/systemd/webradio-backup.* /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable --now webradio-backup.timer
systemctl list-timers webradio-backup.timer
```

A última linha deve mostrar o **próximo disparo às 03:30** (no horário do servidor). O arquivo `webradio-backup.service` assume que o projeto está em `/opt/webradio`; se você o pôs em outro lugar, ajuste a linha `WorkingDirectory=`.

**Primeiro teste do timer** (sem esperar a madrugada):

```bash
sudo systemctl start webradio-backup.service
journalctl -u webradio-backup -n 50 --no-pager
```

Procure `BACKUP_OK` no final. **Todo dia**, o resultado fica no mesmo lugar: `journalctl -u webradio-backup --since "7 days ago" | grep -E "BACKUP_OK|BACKUP_FALHOU"` mostra a semana.

### 10.4 Limpeza semanal dos logs

A rádio guarda no máximo 14 dias de logs com endereço de IP (regra S-B04 da segurança). Isso só vale se um timer semanal apagar os logs velhos. As duas unidades (`.service` e `.timer`) estão prontas para copiar em [`operacao-logs.md`](operacao-logs.md#exemplo-de-systemdtimer-no-host-que-roda-o-compose); crie-as como lá, ligue com `sudo systemctl enable --now webradio-limpar-logs.timer` e teste com `sudo systemctl start webradio-limpar-logs.service`. **Sem esse timer, o prazo de 14 dias não é cumprido.**

## 11. Locutor ao vivo (opcional)

Só faça esta seção se vai ter locutor transmitindo ao vivo com um programa como o BUTT ou o Mixxx. O locutor se conecta à "porta do ao vivo" (o harbor), que **só aceita os endereços que você listar**.

1. No `.env`, acrescente (descomente e preencha as linhas do bloco "Ao vivo" do `.env.example`):

   ```
   HARBOR_BIND_IP=<o IP do servidor por onde os locutores chegam: o da VPN ou do estúdio; nunca 0.0.0.0>
   HARBOR_PERMITIDOS=<IPs ou redes dos locutores, separados por vírgula>
   HARBOR_HOST_PUBLICO=radio.exemplo.com.br
   ```

   Sem VPN, o `HARBOR_BIND_IP` é o **IPv4 público da própria VPS**: a porta do ao vivo fica exposta à internet e só os endereços de `HARBOR_PERMITIDOS` conseguem entrar (mais o firewall do provedor). Isso só funciona se o estúdio ou o locutor tiver **IP fixo**. A conexão do locutor com o harbor **não é criptografada** na v1 (risco aceito pelo dono do produto em 2026-10-15, registrado na spec do Épico 2).

   `HARBOR_PERMITIDOS` é **obrigatório**: vazio, ninguém entra. O `HARBOR_BIND_IP` com `0.0.0.0` é **recusado** pelo sistema, de propósito.

2. Suba com o arquivo do ao vivo (uso **explícito**, os dois `-f`):

   ```bash
   docker compose -f docker-compose.yml -f docker-compose.live.yml up -d --wait
   ```

3. Libere a porta no firewall do **provedor** (o `ufw` não segura portas do Docker; veja o aviso da seção 4.5), só para os IPs dos locutores. A trava de verdade é a lista `HARBOR_PERMITIDOS` da própria rádio.

4. **A partir de agora, todo `up` da rádio leva os dois `-f`.** Subir só com `docker-compose.yml` apaga a porta do ao vivo.

**Conferência:** o locutor entra no painel dele, gera a **credencial de transmissão** (o segredo aparece **uma única vez**), digita no programa o endereço e a porta mostrados e conecta **dentro do horário do programa dele** (fora da janela, a conexão é recusada). A página inicial deve passar a mostrar **Ao vivo**.

## 12. Atualizar a rádio

Atualizar derruba a rádio **por cerca de 1 minuto** (a API e o site); o som continua tocando pela playlist local do servidor de áudio enquanto isso. Escolha um horário calmo.

```bash
cd /opt/webradio
git fetch --tags
git checkout <nova-versão>     # se o time assina as versões: antes, git verify-tag <nova-versão>
docker compose --profile backup run --rm backup     # backup ANTES (obrigatório se a versão mexe no banco; na dúvida, faça sempre)
docker compose --profile backup build   # o perfil 'backup' inclui a imagem do backup, que o build comum não reconstrói
docker compose up -d --wait
```

(Se o ao vivo está ligado, use os dois `-f` no `build` e no `up`. A imagem do backup é a mesma da restauração.) O sistema aplica sozinho as mudanças do banco antes de a API subir. Depois, refaça os itens 1, 4 e 5 da conferência da seção 9. Se algo der errado, **não** faça `down -v`: chame o time.

**HSTS depois de uma semana.** No primeiro deploy o navegador é "preso" ao HTTPS só por 1 dia. Depois de **uma semana** sem problema de certificado, o time sobe esse prazo para 1 ano; o passo está em [`runbooks/borda-https.md`](runbooks/borda-https.md#hsts-de-1-dia-para-1-ano).

## 13. Servidor novo ou banco perdido: restaurar

O passo a passo completo e os comandos estão em [`runbooks/backup-e-restauracao.md`](runbooks/backup-e-restauracao.md#restaurar-servidor-novo-ou-bancobiblioteca-perdidos). O que **você** precisa saber:

- **Num servidor novo, o timer do backup fica DESLIGADO até a restauração terminar** (e não rode o backup à mão). Um backup feito antes da restauração gravaria um banco **vazio** como "mais recente", e a restauração pegaria o banco vazio. Se o timer da seção 10.3 já foi ligado nesse servidor, faça `sudo systemctl disable --now webradio-backup.timer` e só religue (`enable --now`) depois de conferir a rádio restaurada.
- Você precisa, **fora do servidor**: a `restic_senha`, as chaves `backup_s3_*`, o endpoint e o nome do bucket (seções 6 e 7). Num servidor novo a restauração vem **do bucket externo**.
- Antes de a restauração apagar e recriar o banco, ela **mostra o ponto de backup** (data e servidor) e pede o nome do banco. Confira que o ponto é o que você espera.
- Depois, confira: login do Administrador, a grade, a biblioteca com pré-escuta e uma música da rotação tocando.
- Uma vez a cada trimestre, faça a restauração completa num servidor descartável: é o **único teste** de que a senha que você guardou fora funciona. **Backup que nunca foi restaurado não é backup.**

## 14. Depois de uma invasão suspeita

Se há suspeita de que alguém entrou no servidor, **não restaure o backup mais recente**: ele pode ter sido feito **depois** da invasão e carregar dentro do banco usuários, papéis ou permissões plantados. Restaure o **último ponto de backup de ANTES da data em que a suspeita começa** (o runbook mostra como listar os pontos e escolher o certo, em [`backup-e-restauracao.md`](runbooks/backup-e-restauracao.md#depois-de-um-incidente-invasão-suspeita-restaure-o-snapshot-anterior-à-suspeita)).

E **não reaproveite os segredos** do servidor invadido: quem tomou o servidor pôde ler a pasta `secrets/`.

1. Gere de novo as senhas do banco, as chaves do MinIO e a chave do login (JWT): veja [`runbooks/rotacao-jwt-signing-key.md`](runbooks/rotacao-jwt-signing-key.md) (a seção "Vazamento da chave", que também diz o que auditar).
2. A `restic_senha` e as chaves do bucket (`backup_s3_*`) entram numa avaliação do Sentinel (segurança): peça ao time antes de decidir trocá-las.
3. A cópia externa (seção 7) é o que costuma deixar um ponto de backup anterior à invasão, e as versões antigas ficam por 30 dias na **sua conta** no provedor. **Mas isso não é garantia:** a chave do backup (`backup_s3_*`) pode apagar versões de forma definitiva, e quem tomou o servidor leu essa chave. **Se houver suspeita de invasão, entre agora na sua conta do provedor e confira se os pontos de backup anteriores ainda existem; se a chave foi lida, revogue-a ali mesmo**, e peça ao time para criar uma nova.
4. Copie o log do que aconteceu para fora do servidor **antes** de recriar qualquer contêiner (recriar apaga o log): o runbook do JWT explica.

## 15. Acompanhar no dia a dia

A v1 **não tem alertas automáticos**: alguém precisa olhar. Dois hábitos simples:

**1. Um monitor externo gratuito** (por exemplo o UptimeRobot), com duas checagens a cada poucos minutos: `https://radio.exemplo.com.br/` e `https://radio.exemplo.com.br/stream/radio.mp3`. Ele manda e-mail se a rádio cair.

**2. Uma olhada semanal** nos avisos dos logs:

```bash
docker compose logs --since 7d worker api | grep -E "WORKER_DEGRADADO|OUTBOX_ESGOTADO|AUTH_401_TAXA"
journalctl -u webradio-backup --since "7 days ago" | grep -E "BACKUP_OK|BACKUP_FALHOU"
```

| Aviso | O que significa | O que fazer |
|---|---|---|
| `BACKUP_OK` | O backup do dia deu certo | Nada. **Procure ao menos um por semana** |
| `BACKUP_FALHOU etapa=... motivo=...` | O backup falhou | Veja o motivo em [`runbooks/backup-e-restauracao.md`](runbooks/backup-e-restauracao.md#erros-comuns) |
| `WORKER_DEGRADADO motivo=...` | Um trabalho de fundo (antivírus, conversor, limpeza) está com problema | [`runbooks/worker-degradado.md`](runbooks/worker-degradado.md) |
| `AUTH_401_TAXA` | Muitas tentativas de login erradas | [`runbooks/auth-401-taxa.md`](runbooks/auth-401-taxa.md) |
| `OUTBOX_ESGOTADO` | Um aviso em tempo real deixou de ser entregue | Chame o time |

## 16. Índice dos runbooks

Os runbooks são os manuais de operação, com mais detalhe técnico que este guia.

| Quando | Documento |
|---|---|
| Backup, cópia externa, restauração, teste mensal | [`runbooks/backup-e-restauracao.md`](runbooks/backup-e-restauracao.md) |
| Certificado, HTTPS, HSTS, IP real do ouvinte | [`runbooks/borda-https.md`](runbooks/borda-https.md) |
| O Worker avisou `WORKER_DEGRADADO` | [`runbooks/worker-degradado.md`](runbooks/worker-degradado.md) |
| Muitos 401 no login (`AUTH_401_TAXA`) | [`runbooks/auth-401-taxa.md`](runbooks/auth-401-taxa.md) |
| Trocar a chave do login (JWT), por rotina ou vazamento | [`runbooks/rotacao-jwt-signing-key.md`](runbooks/rotacao-jwt-signing-key.md) |
| Rotação dos demais segredos (rascunho com avisos no topo: leia-os antes) | [`runbooks/rotacao-de-segredos.md`](runbooks/rotacao-de-segredos.md) |
| Logs: quanto guardam e como limpar | [`operacao-logs.md`](operacao-logs.md) |
| Conexão com o SQL Server | [`conexao-sql-server.md`](conexao-sql-server.md) |
