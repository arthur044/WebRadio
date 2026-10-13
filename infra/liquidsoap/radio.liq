# =============================================================================
# WebRadio — infra/liquidsoap/radio.liq (E1-F15, esqueleto do Fluxo D)
#
#   fallback(track_sensitive=false, [ao vivo (harbor), fila da API, playlist local, tom])
#     -> output.icecast(%mp3 128k, mount=radio.mp3)
#
# Segurança (S-A08, S-A09):
#   - senhas e token SÓ de /run/secrets (file.contents), nunca de variável de ambiente nem do script;
#   - a URL vinda da API só é aceita com o prefixo http://minio:9000/midia/ (nada de file://, annotate:, etc.);
#   - metadados aplicados com metadata.map, NUNCA concatenados num "annotate:" (injeção de URI);
#   - o log do harbor registra conexão/desconexão, nunca a senha.
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
senha_harbor = segredo("liquidsoap_harbor_password", 24)

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

# --------------------------------------------------------------- ao vivo
ao_vivo = input.harbor(
  "live",
  port=8005,
  password=senha_harbor,
  on_connect=fun (_) -> log.important(label="harbor", "Source client conectado."),
  on_disconnect=fun () -> log.important(label="harbor", "Source client desconectado.")
)

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
