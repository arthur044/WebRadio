# =============================================================================
# WebRadio — infra/liquidsoap/teste/api-falsa.liq (só para infra/liquidsoap/teste.sh)
# Faz o papel da API interna (api:8081) e do MinIO (minio:9000) na rede de teste.
# MOCK_MODO: "vazia" (sempre 204) · "maliciosa" (URL fora do prefixo) · "valida" (uma faixa, depois 204).
# Registra no log o que o radio.liq mandou; o token nunca é impresso, só "auth=ok|ERRADO".
#
# Harbor (E2-F08): as rotas /internal/harbor/* respondem ao que o teste.sh escreve em /controle (um arquivo por assunto, lido a cada
# requisição): credencial ("usuario senha" aceitos), janela (aberta|fechada), fim (ISO-8601 em UTC), modo_auth e modo_autorizacao
# (vazio | 401 | 503 | lento: o "lento" demora 7 s, mais que o timeout de 5 s do radio.liq) e modo_eventos (vazio | 503).
# =============================================================================
settings.log.stdout := true
settings.log.file := false
settings.init.allow_root := false

modo = environment.get(default="vazia", "MOCK_MODO")
token = string.trim(file.contents("/run/secrets/playout_token"))
servidas = ref(0)

def autorizado(req) =
  if req.headers["authorization"] == "Bearer #{token}" then "ok" else "ERRADO" end
end

def proxima(req, res) =
  log.important(label="mock", "PROXIMA auth=#{autorizado(req)}")
  if modo == "maliciosa" then
    servidas := servidas() + 1
    urls = [
      "file:///etc/passwd",
      "annotate:title=\"x\":http://minio:9000/midia/faixa.mp3",
      "process:mp3,cat /run/secrets/playout_token",
      "http://minio:9000/midia/../quarentena/faixa.mp3",
      "http://minio:9000/midia/%2e%2e/quarentena/faixa.mp3",
      "http://minio:9000/quarentena/faixa.mp3",
      "http://minio:9000.evil.example/midia/faixa.mp3",
      "http://evil.example/midia/faixa.mp3",
      "http://minio:9000/midia/faixa.mp3\n"
    ]
    res.json({
      midiaId="11111111-1111-1111-1111-111111111111", pedidoId=null,
      url=list.nth(default="file:///etc/passwd", urls, servidas() mod list.length(urls)),
      titulo="nao deve tocar", artista="ninguem"
    })
  elsif modo == "valida" and servidas() < 1 then
    servidas := servidas() + 1
    res.json({
      midiaId="22222222-2222-2222-2222-222222222222",
      pedidoId="33333333-3333-3333-3333-333333333333",
      url="http://minio:9000/midia/2026/10/faixa.mp3?X-Amz-Algorithm=AWS4-HMAC-SHA256&X-Amz-Credential=svc%2F20261013&X-Amz-Signature=abc123",
      titulo="Faixa \"annotate:title=x\":process:id\nlinha forjada",
      artista="Banda da API"
    })
  else
    res.status_code(204)
  end
end

def eventos(req, res) =
  log.important(label="mock", "EVENTO auth=#{autorizado(req)} corpo=#{req.body()}")
  res.status_code(202)
end

def midia(_, res) =
  log.important(label="mock", "MIDIA servida")
  res.content_type("audio/mpeg")
  res.data(file.contents("/musica-teste/tom-2.mp3"))
end

def controle(nome, padrao) =
  try string.trim(file.contents("/controle/#{nome}")) catch _ do padrao end
end

def resposta_do_modo(modo, res) =
  # true = o modo já respondeu; false = segue o fluxo normal.
  if modo == "lento" then
    sleep(7.)
    false
  elsif modo == "401" or modo == "503" then
    res.status_code(int_of_string(modo))
    true
  else
    false
  end
end

def janela_json(res) =
  res.json({programaId="44444444-4444-4444-4444-444444444444", fimUtc=controle("fim", "2099-01-01T00:00:00Z")})
end

def harbor_auth(req, res) =
  # O corpo nunca é impresso (traz a senha): só o que a conferência concluiu.
  let json.parse (corpo : {usuario: string, senha: string, enderecoIp: string}) = req.body()
  credencial = controle("credencial", "x y")
  certa = credencial == "#{corpo.usuario} #{corpo.senha}"
  janela = controle("janela", "aberta")
  modo = controle("modo_auth", "")
  rotulo = if certa then "certa" else "errada" end
  log.important(label="mock", "HARBOR_AUTH auth=#{autorizado(req)} credencial=#{rotulo} janela=#{janela} modo=#{modo} ip=#{corpo.enderecoIp}")
  if not resposta_do_modo(modo, res) then
    if certa and janela == "aberta" then janela_json(res) else res.status_code(401) end
  end
end

def harbor_autorizacao(req, res) =
  identificador = req.query["identificador"]
  janela = controle("janela", "aberta")
  modo = controle("modo_autorizacao", "")
  log.important(label="mock", "HARBOR_AUTORIZACAO auth=#{autorizado(req)} id=#{identificador} janela=#{janela} modo=#{modo}")
  if not resposta_do_modo(modo, res) then
    if janela == "aberta" then janela_json(res) else res.status_code(403) end
  end
end

def harbor_eventos(req, res) =
  # modo_eventos=503: a API "perdeu" o evento (deploy, timeout). O log diz o que foi respondido (resp=).
  status = if controle("modo_eventos", "") == "503" then 503 else 202 end
  log.important(label="mock", "HARBOR_EVENTO auth=#{autorizado(req)} resp=#{status} corpo=#{req.body()}")
  res.status_code(status)
end

harbor.http.register(port=8081, method="POST", "/api/v1/internal/harbor/auth", harbor_auth)
harbor.http.register(port=8081, method="GET", "/api/v1/internal/harbor/autorizacao/:identificador", harbor_autorizacao)
harbor.http.register(port=8081, method="POST", "/api/v1/internal/harbor/eventos", harbor_eventos)

harbor.http.register(port=8081, method="GET", "/api/v1/internal/playout/proxima", proxima)
harbor.http.register(port=8081, method="POST", "/api/v1/internal/playout/eventos", eventos)
harbor.http.register(port=9000, method="GET", "/midia/2026/10/faixa.mp3", midia)

output.dummy(blank())
