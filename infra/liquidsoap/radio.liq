# =============================================================================
# WebRadio — infra/liquidsoap/radio.liq (E1-F15, esqueleto do Fluxo D)
#
#   fallback(track_sensitive=false, [ao vivo (harbor), fila da API, playlist local, tom])
#     -> output.icecast(%mp3 128k, mount=radio.mp3)
#
# Segurança (S-A08, S-A09):
#   - senhas e token SÓ de /run/secrets (file.contents), nunca de variável de ambiente nem do script;
#   - o ao vivo não tem senha própria: cada locutor entra com a credencial de transmissão dele, conferida pela API a cada
#     conexão (POST /internal/harbor/auth) e a cada 10 s (GET /internal/harbor/autorizacao/{identificador});
#   - a URL vinda da API só é aceita com o prefixo http://minio:9000/midia/ (nada de file://, annotate:, etc.);
#   - metadados aplicados com metadata.map, NUNCA concatenados num "annotate:" (injeção de URI);
#   - o log do harbor registra conexão/desconexão, nunca a senha; o identificador só aparece depois de passar na regex.
# A API é chamada no listener interno: http://api:8081 (rede playout). Se a API cair, o stream continua
# (a fila só fica vazia e o fallback toca a playlist local).
# =============================================================================

settings.log.stdout := true
settings.log.file := false
settings.log.level := 3
settings.init.allow_root := false

# S-C02: o boot recusa segredo vazio, de exemplo ou curto (a mensagem nunca traz o valor). Harbor com senha vazia seria o pior caso (S-A08).
def segredo(nome, minimo) =
  valor = string.trim(file.contents("/run/secrets/#{nome}"))
  if valor == "" then
    error.raise(error.failure, "segredo #{nome} está vazio; rode infra/scripts/gerar-segredos.sh")
  end
  minusculo = string.case(lower=true, valor)
  if string.contains(substring="__gerar__", minusculo) or string.contains(substring="change_me", minusculo) or string.contains(substring="changeme", minusculo) then
    error.raise(error.failure, "segredo #{nome} é um valor de exemplo; rode infra/scripts/gerar-segredos.sh")
  end
  if string.length(valor) < minimo then
    error.raise(error.failure, "segredo #{nome} tem menos de #{minimo} caracteres; rode infra/scripts/gerar-segredos.sh")
  end
  valor
end

token_playout = segredo("playout_token", 32)
senha_icecast = segredo("icecast_source_password", 24)

api = environment.get(default="http://api:8081", "API_INTERNA")
prefixo_midia = "http://minio:9000/midia/"
autorizacao = [("Authorization", "Bearer #{token_playout}")]

# Metadado que vai para a API e para o Icecast: até 200 caracteres e sem caractere de controle (S-A09).
def limpar(texto) =
  t = string.replace(pattern="[\x00-\x1f\x7f]", fun (_) -> "", texto)
  if string.length(t) > 200 then string.sub(t, start=0, length=200) else t end
end

# --------------------------------------------------------------- fila da API
# S-A09: a URL da API só é aceita se for EXATAMENTE um objeto do bucket midia no MinIO interno: prefixo fixo,
# alfabeto de URL pré-assinada, sem ".." e sem ponto/barra codificados no caminho (não troca de bucket nem de host).
def url_aceita(endereco) =
  caminho = list.hd(default="", string.split(separator="\\?", endereco))
  string.contains(prefix=prefixo_midia, endereco)
    and string.match(pattern="^http://minio:9000/midia/[A-Za-z0-9/_.~%-]+(\\?[A-Za-z0-9=&%_.~+-]+)?$", endereco)
    and not string.contains(substring="..", endereco)
    and not string.contains(substring="\n", endereco)
    and not string.match(pattern="%(2[eEfF]|5[cC])", caminho)
end

def uuid_ou_vazio(texto) =
  if string.match(pattern="^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$", texto) then texto else "" end
end

# Metadados das faixas que a API mandou, guardados POR URL e aplicados por metadata.map quando a faixa começa
# (o request.dynamic busca a próxima enquanto a atual ainda toca: um valor único trocaria os títulos).
# Nunca viram "annotate:": título e artista são dados, não fazem parte do URI.
metas_da_api = ref([])
# O log de "API fora do ar" sai só na transição: com a API caída são 12 tentativas por minuto.
api_no_ar = ref(true)

def proxima() =
  try
    resp = http.get(headers=autorizacao, timeout=5., "#{api}/api/v1/internal/playout/proxima")
    # Sem rota/DNS o http.get não lança: devolve um status de erro (ex.: 523). Só 200 e 204 contam como "no ar".
    if resp.status_code == 200 or resp.status_code == 204 then
      if not api_no_ar() then log.important(label="playout", "API de volta.") end
      api_no_ar := true
    else
      if api_no_ar() then log.important(label="playout", "API indisponível (status #{resp.status_code}). Seguindo com o fallback.") end
      api_no_ar := false
    end
    if resp.status_code == 200 then
      let json.parse (item :
        {url: string, titulo: string?, artista: string?, midiaId: string?, pedidoId: string?}) = resp
      if url_aceita(item.url) then
        meta = [
          ("title", limpar(null.get(default="", item.titulo))),
          ("artist", limpar(null.get(default="", item.artista))),
          ("wr_midia_id", uuid_ou_vazio(null.get(default="", item.midiaId))),
          ("wr_pedido_id", uuid_ou_vazio(null.get(default="", item.pedidoId)))
        ]
        # No máximo as 8 últimas: uma faixa que nunca chegou a tocar não fica guardada para sempre.
        resto = list.filter(fun (par) -> fst(par) != item.url, metas_da_api())
        metas_da_api := list.prefix(8, [(item.url, meta), ...resto])
        request.create(item.url)
      else
        # Sem a URL no log: ela pode trazer a assinatura.
        log.important(label="playout", "URL da API recusada (fora de #{prefixo_midia}).")
        null
      end
    else
      # 204 = nada na fila; qualquer outro status também cai para o fallback.
      null
    end
  catch err do
    if api_no_ar() then log.important(label="playout", "API indisponível: #{err.kind}. Seguindo com o fallback.") end
    api_no_ar := false
    null
  end
end

fila_api = request.dynamic(retry_delay=5., proxima)

def meta_da_api(m) =
  list.assoc(default=[], m["initial_uri"], metas_da_api())
end

fila_api = metadata.map(update=true, meta_da_api, fila_api)

# Contrato em docs/specs/06-epico-2-tarefas.md ("Credencial de transmissão", "Endpoints internos"):
#   - formato estrito de usuário (UUID minúsculo) e senha (43 caracteres de base64url) ANTES de qualquer uso: fora disso a conexão é
#     recusada sem chamar a API e sem o valor no log (o locutor pode trocar os campos e mandar o segredo no lugar do usuário);
#   - qualquer falha do auth (API fora, timeout de 5 s, resposta ilegível, 5xx) recusa: conexão nova com a API fora nunca entra;
#   - a cada 10 s o locutor no ar é reconferido. Qualquer 4xx derruba na hora. Só erro de TRANSPORTE (timeout, conexão recusada, 5xx)
#     mantém o locutor (falha aberta), até o teto min(fimUtc + 1 min, último 200 + 15 min); depois disso derruba mesmo sem resposta.
re_usuario = "^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$"
re_senha = "^[A-Za-z0-9_-]{43}$"

def formato_valido(padrao, texto) =
  # O Re trata ^ e $ por linha: o \n explícito fecha "valor-valido\nlixo".
  string.match(pattern=padrao, texto) and not string.contains(substring="\n", texto)
end

# Tamanho só para o log; bytes inválidos em UTF-8 não podem derrubar o auth.
def tamanho_para_log(texto) =
  try string.length(texto) catch _ do -1 end
end

# IP anonimizado como no nginx: /24 para IPv4, /64 para IPv6. O que não for IP não vai para o log.
def ip_anonimo(ip) =
  v4 = string.extract(pattern="^([0-9]{1,3}\\.[0-9]{1,3}\\.[0-9]{1,3})\\.[0-9]{1,3}$", ip)
  if v4 != [] then
    rede = list.assoc(default="", 1, v4)
    "#{rede}.0/24"
  elsif string.match(pattern="^[0-9a-fA-F:.]+$", ip) and string.contains(substring=":", ip) then
    cabeca = list.hd(default="", string.split(separator="::", ip))
    grupos = list.filter(fun (g) -> g != "", string.split(separator=":", cabeca))
    rede = string.concat(separator=":", list.prefix(4, grupos))
    "#{rede}::/64"
  else
    "ip_invalido"
  end
end

# "2026-10-20T18:30:15Z" (o formato da API) para segundos desde 1970. O contêiner roda em UTC (TZ=UTC no compose).
def epoch_de(iso) =
  g = string.extract(pattern="^([0-9]{4})-([0-9]{2})-([0-9]{2})T([0-9]{2}):([0-9]{2}):([0-9]{2})", iso)
  if list.length(g) < 7 then
    error.raise(error.invalid, "data inválida")
  end
  n = fun (i) -> int_of_string(list.assoc(default="0", i, g))
  time.make({year=n(1), month=n(2), day=n(3), hour=n(4), min=n(5), sec=n(6), dst=null})
end

# Metadado do source client (S-A09): até 200 caracteres e nenhum caractere de Cc, Cf, Cs, U+2028 ou U+2029. O regex do Liquidsoap
# não tem \p{..}, então as categorias entram como sequências de bytes UTF-8 (Unicode 16). Exceções, iguais às do MetadadoSeguro do
# servidor: o ZWJ (U+200D, fora da lista) e as tags U+E0020–E007E só numa bandeira (U+1F3F4, tags, U+E007F), que sai antes da conferência.
# >>> metadado_seguro (o teste.sh extrai este trecho e o confere contra uma tabela de casos)
re_bandeira = "\xF0\x9F\x8F\xB4(\xF3\xA0\x80[\xA0-\xBF]|\xF3\xA0\x81[\x80-\xBE])+\xF3\xA0\x81\xBF"
re_proibido = "[\x00-\x1F]|\x7F|\xC2[\x80-\x9F]|\xE2\x80[\xA8-\xA9]|\xC2\xAD|\xD8[\x80-\x85]|\xD8\x9C|\xDB\x9D|\xDC\x8F|\xE0\xA2[\x90-\x91]|\xE0\xA3\xA2|\xE1\xA0\x8E|\xE2\x80[\x8B-\x8C]|\xE2\x80[\x8E-\x8F]|\xE2\x80[\xAA-\xAE]|\xE2\x81[\xA0-\xA4]|\xE2\x81[\xA6-\xAF]|\xEF\xBB\xBF|\xEF\xBF[\xB9-\xBB]|\xF0\x91\x82\xBD|\xF0\x91\x83\x8D|\xF0\x93\x90[\xB0-\xBF]|\xF0\x9B\xB2[\xA0-\xA3]|\xF0\x9D\x85[\xB3-\xBA]|\xF3\xA0\x80\x81|\xF3\xA0\x80[\xA0-\xBF]|\xF3\xA0\x81[\x80-\xBF]|\xED[\xA0-\xBF][\x80-\xBF]"

def metadado_seguro(texto) =
  # string.length em UTF-8 lança para bytes inválidos: o que não é UTF-8 também é recusado.
  try
    string.length(texto) <= 200 and not string.match(pattern=re_proibido, string.replace(pattern=re_bandeira, fun (_) -> "", texto))
  catch _ do
    false
  end
end
# <<< metadado_seguro

# Quem está no ar: o identificador sai do Authorization da PRÓPRIA conexão no on_connect (só a parte do usuário; a senha nunca é
# decodificada para variável nenhuma). Não passa do auth para o on_connect por uma variável global: com duas credenciais válidas
# ao mesmo tempo o auth de B sobrescreveria o de A e a conexão de A seria conferida (e avisada à API) como B. O que o auth deixa
# para o on_connect é só o fim do programa de CADA usuário autorizado (janelas_autorizadas), indexado pelo próprio usuário.
# fim_utc e ultimo_200 alimentam o teto da falha aberta.
janelas_autorizadas = ref([])
ident_ao_vivo = ref("")
ao_vivo_conectado = ref(false)
fim_utc = ref(0.)
ultimo_200 = ref(0.)
conectado_avisado = ref(false)
# Teto "último 200 + N s" da falha aberta: 900 s (15 min) em produção. A variável existe só para o teste.sh exercitar esse ramo.
teto_sem_resposta = float_of_string(environment.get(default="900", "HARBOR_TETO_SEM_RESPOSTA_S"))

# Instante em ISO-8601 UTC com milissegundos: a reconexão rápida do mesmo locutor manda o Desconectado da conexão antiga e o
# Conectado da nova no mesmo segundo, e o servidor ordena os eventos por este valor.
def agora_iso() =
  t = time()
  ms = int_of_float((t - floor(t)) * 1000.)
  base = time.string(time=t, "%Y-%m-%dT%H:%M:%S")
  "#{base}.#{string.of_int(digits=3, ms)}Z"
end

# true = a API aceitou (202). O Conectado perdido é reenviado pela consulta periódica (conectado_avisado), senão o /stream/info
# diria noAr:false com o locutor no ar até o fim do programa.
def eventos_harbor(tipo, identificador) =
  corpo = json()
  corpo.add("tipo", tipo)
  corpo.add("identificador", identificador)
  corpo.add("ocorridoEmUtc", agora_iso())
  try
    resp = http.post(headers=[("Content-Type", "application/json"), ...autorizacao], timeout=5.,
                     data=json.stringify(compact=true, corpo), "#{api}/api/v1/internal/harbor/eventos")
    if resp.status_code != 202 then
      log.important(label="harbor", "API respondeu #{resp.status_code} ao evento #{tipo}.")
    end
    resp.status_code == 202
  catch err do
    log.important(label="harbor", "Evento #{tipo} não entregue: #{err.kind}.")
    false
  end
end

def autenticar_harbor(login) =
  usuario = login.user
  senha = login.password
  ip = ip_anonimo(login.address)
  if not formato_valido(re_usuario, usuario) then
    log.important(label="harbor", "Recusado: usuario_invalido (#{tamanho_para_log(usuario)} caracteres) ip #{ip}.")
    false
  elsif not formato_valido(re_senha, senha) then
    log.important(label="harbor", "Recusado: senha_invalida identificador #{usuario} ip #{ip}.")
    false
  else
    try
      corpo = json()
      corpo.add("usuario", usuario)
      corpo.add("senha", senha)
      corpo.add("enderecoIp", login.address)
      resp = http.post(headers=[("Content-Type", "application/json"), ...autorizacao], timeout=5.,
                       data=json.stringify(compact=true, corpo), "#{api}/api/v1/internal/harbor/auth")
      if resp.status_code == 200 then
        let json.parse (janela : {programaId: string, fimUtc: string}) = resp
        # Uma entrada por usuário (as chamadas repetidas do mesmo source client, de metadado, só a renovam).
        resto = list.filter(fun (par) -> fst(par) != usuario, janelas_autorizadas())
        janelas_autorizadas := [(usuario, epoch_de(janela.fimUtc)), ...resto]
        log.important(label="harbor", "Autorizado identificador #{usuario} ip #{ip}.")
        true
      else
        # 401 é a recusa de sempre (credencial errada, revogada ou fora da janela); o resto é a API fora do ar. Nos dois, recusa.
        log.important(label="harbor", "Recusado: API respondeu #{resp.status_code} identificador #{usuario} ip #{ip}.")
        false
      end
    catch err do
      log.important(label="harbor", "Recusado: auth falhou (#{err.kind}) identificador #{usuario} ip #{ip}.")
      false
    end
  end
end

entrada_ao_vivo = input.harbor("live", port=8005, auth=autenticar_harbor)

def derrubar_ao_vivo(motivo) =
  log.important(label="harbor", "Derrubando o ao vivo: #{motivo}.")
  entrada_ao_vivo.stop()
end

# "Basic <base64(usuario:senha)>" do source client. Devolve só o usuário e só se tiver o formato do identificador; senão "".
def usuario_do_authorization(headers) =
  par = list.find(default=("", ""), fun (h) -> string.case(lower=true, fst(h)) == "authorization", headers)
  partes = string.extract(pattern="^Basic ([A-Za-z0-9+/=]+)$", snd(par))
  usuario = if partes == [] then "" else
    try
      list.hd(default="", string.split(separator=":", string.base64.decode(list.assoc(default="", 1, partes))))
    catch _ do
      ""
    end
  end
  if formato_valido(re_usuario, usuario) then usuario else "" end
end

entrada_ao_vivo.on_connect(synchronous=false, fun (headers) -> begin
  ident_ao_vivo := usuario_do_authorization(headers)
  ao_vivo_conectado := true
  fim_utc := list.assoc(default=0., ident_ao_vivo(), janelas_autorizadas())
  janelas_autorizadas := list.filter(fun (par) -> fst(par) != ident_ao_vivo(), janelas_autorizadas())
  ultimo_200 := time()
  if ident_ao_vivo() == "" then
    # Sem identificador autorizado a consulta periódica não teria o que conferir: ninguém fica no ar assim.
    log.severe(label="harbor", "Conexão aceita sem identificador autorizado.")
    derrubar_ao_vivo("sem identificador autorizado")
  else
    log.important(label="harbor", "Source client conectado (identificador #{ident_ao_vivo()}).")
    conectado_avisado := eventos_harbor("Conectado", ident_ao_vivo())
  end
end)

entrada_ao_vivo.on_disconnect(synchronous=false, fun () -> begin
  identificador = ident_ao_vivo()
  ident_ao_vivo := ""
  ao_vivo_conectado := false
  conectado_avisado := false
  log.important(label="harbor", "Source client desconectado.")
  if identificador != "" then ignore(eventos_harbor("Desconectado", identificador)) end
end)

# A cada 10 s: o locutor no ar ainda está autorizado? (06 "Autorização do ao vivo": a janela é calculada no SQL.)
def consultar_autorizacao() =
  if ao_vivo_conectado() and ident_ao_vivo() != "" then
    agora = time()
    status = ref(0)
    try
      resp = http.get(headers=autorizacao, timeout=5., "#{api}/api/v1/internal/harbor/autorizacao/#{ident_ao_vivo()}")
      if resp.status_code == 200 then
        let json.parse (janela : {programaId: string, fimUtc: string}) = resp
        fim_utc := epoch_de(janela.fimUtc)
      end
      # Só depois do parse: um 200 com corpo ilegível não renova o último 200 (cai no ramo de falha, com o log.severe abaixo).
      status := resp.status_code
    catch err do
      log.severe(label="harbor", "Consulta de autorização sem resposta útil (#{err.kind}).")
    end
    teto_fim = fim_utc() + 60.
    teto_resposta = ultimo_200() + teto_sem_resposta
    teto = min(teto_fim, teto_resposta)
    if status() == 200 then
      ultimo_200 := agora
      # O Conectado que a API não recebeu na hora (5xx, timeout, deploy): reenvia, o servidor trata o evento repetido.
      if not conectado_avisado() then conectado_avisado := eventos_harbor("Conectado", ident_ao_vivo()) end
    elsif status() >= 300 and status() < 500 then
      # Só timeout, conexão recusada e 5xx são falha de transporte (06, F07 c): qualquer outro status que não seja o 200 derruba.
      derrubar_ao_vivo("autorização negada (HTTP #{status()})")
    else
      log.severe(label="harbor", "Consulta de autorização falhou (status #{status()}). Mantendo o locutor por mais #{max(0., teto - agora)} s, no máximo.")
      if agora >= teto then
        motivo = if teto_fim <= teto_resposta then "fim do programa + 1 min" else "último 200 + #{int_of_float(teto_sem_resposta)} s" end
        derrubar_ao_vivo("teto da falha aberta (#{motivo})")
      end
    end
  end
end

thread.run(fast=false, every=10., consultar_autorizacao)

# Título e artista do source client passam por metadado_seguro; o que não passar some (nada mais do source client segue adiante).
def sanear_ao_vivo(m) =
  titulo = m["title"]
  artista = m["artist"]
  list.append(
    if titulo != "" and metadado_seguro(titulo) then [("title", titulo)] else [] end,
    if artista != "" and metadado_seguro(artista) then [("artist", artista)] else [] end
  )
end

ao_vivo = metadata.map(update=false, sanear_ao_vivo, entrada_ao_vivo)

# --------------------------------------------------------------- rotação local
rotacao = playlist("/musica-teste", mode="randomize", reload_mode="watch")

radio = fallback(track_sensitive=false, [ao_vivo, fila_api, rotacao, sine()])
radio = mksafe(radio)

# --------------------------------------------------------------- eventos para a API
def avisar_api(m) =
  corpo = json()
  corpo.add("tipo", "FaixaIniciada")
  corpo.add("titulo", limpar(m["title"]))
  corpo.add("artista", limpar(m["artist"]))
  # Só as faixas da fila da API têm estes dois; rotação local e ao vivo vão sem (midiaId nulo, 02 §9).
  if uuid_ou_vazio(m["wr_midia_id"]) != "" then corpo.add("midiaId", m["wr_midia_id"]) end
  if uuid_ou_vazio(m["wr_pedido_id"]) != "" then corpo.add("pedidoId", m["wr_pedido_id"]) end
  # O contêiner roda em UTC (TZ=UTC no serviço liquidsoap do compose): o "Z" é verdadeiro.
  corpo.add("ocorridoEmUtc", time.string("%Y-%m-%dT%H:%M:%SZ"))
  try
    resp = http.post(headers=[("Content-Type", "application/json"), ...autorizacao], timeout=5.,
                     data=json.stringify(compact=true, corpo), "#{api}/api/v1/internal/playout/eventos")
    if resp.status_code != 202 then
      log.info(label="playout", "API respondeu #{resp.status_code} ao evento de faixa.")
    end
  catch err do
    log.info(label="playout", "Evento de faixa não entregue: #{err.kind}.")
  end
end

# synchronous=false: o callback roda fora da thread de streaming (uma API lenta não trava o áudio).
radio.on_track(synchronous=false, avisar_api)

output.icecast(
  %mp3(bitrate=128, samplerate=44100, stereo=true),
  host="icecast",
  port=8000,
  password=senha_icecast,
  mount="radio.mp3",
  name="WebRadio",
  radio
)
