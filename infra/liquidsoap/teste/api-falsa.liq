# =============================================================================
# WebRadio — infra/liquidsoap/teste/api-falsa.liq (só para infra/liquidsoap/teste.sh)
# Faz o papel da API interna (api:8081) e do MinIO (minio:9000) na rede de teste.
# MOCK_MODO: "vazia" (sempre 204) · "maliciosa" (URL fora do prefixo) · "valida" (uma faixa, depois 204).
# Registra no log o que o radio.liq mandou; o token nunca é impresso, só "auth=ok|ERRADO".
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

harbor.http.register(port=8081, method="GET", "/api/v1/internal/playout/proxima", proxima)
harbor.http.register(port=8081, method="POST", "/api/v1/internal/playout/eventos", eventos)
harbor.http.register(port=9000, method="GET", "/midia/2026/10/faixa.mp3", midia)

output.dummy(blank())
