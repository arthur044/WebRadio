namespace WebRadio.Api.Infra;

/// <summary>
/// Refresh e logout só aceitam requisição da mesma origem (00 §6, 02 §2). Origin ausente é recusado (POST de navegador
/// sempre envia); Sec-Fetch-Site, quando existe, precisa ser same-origin. A "própria origem" é esquema://host da
/// requisição já corrigido pelo ForwardedHeaders (X-Forwarded-Proto/Host só da rede <c>app</c>): atrás do nginx o host
/// vem com a porta quando ela é a pública (ex.: localhost:8080).
/// </summary>
public static class OrigemDaRequisicao
{
    public static bool EhPermitida(HttpContext http, IConfiguration config)
    {
        var origem = http.Request.Headers.Origin.ToString();
        if (string.IsNullOrEmpty(origem)) return false;

        var fetchSite = http.Request.Headers["Sec-Fetch-Site"].ToString();
        var propria = $"{http.Request.Scheme}://{http.Request.Host}";

        if (string.Equals(origem, propria, StringComparison.OrdinalIgnoreCase))
            return fetchSite.Length == 0 || fetchSite.Equals("same-origin", StringComparison.OrdinalIgnoreCase);

        // Origem extra da allowlist (ex.: SPA em outro subdomínio): o navegador marca same-site, nunca cross-site.
        var extras = config.GetSection("Seguranca:OrigensPermitidas").Get<string[]>() ?? [];
        return extras.Contains(origem, StringComparer.OrdinalIgnoreCase)
            && (fetchSite.Length == 0
                || fetchSite.Equals("same-origin", StringComparison.OrdinalIgnoreCase)
                || fetchSite.Equals("same-site", StringComparison.OrdinalIgnoreCase));
    }
}
