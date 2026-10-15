namespace WebRadio.Api.Infra;

/// <summary>
/// Refresh e logout só aceitam requisição da mesma origem (00 §6, 02 §2). Origin ausente é recusado (POST de navegador
/// sempre envia); Sec-Fetch-Site, quando existe, precisa ser same-origin. A "própria origem" é esquema://host da
/// requisição já corrigido pelo ForwardedHeaders (X-Forwarded-Proto/Host só da rede <c>app</c>): atrás do nginx o host
/// vem com a porta quando ela é a pública (ex.: localhost:8080).
/// </summary>
public static class OrigemDaRequisicao
{
    public static bool EhPermitida(HttpContext http, OrigensPermitidas extras)
    {
        var origem = http.Request.Headers.Origin.ToString();
        if (string.IsNullOrEmpty(origem)) return false;

        var fetchSite = http.Request.Headers["Sec-Fetch-Site"].ToString();
        var propria = $"{http.Request.Scheme}://{http.Request.Host}";

        if (string.Equals(origem, propria, StringComparison.OrdinalIgnoreCase))
            return fetchSite.Length == 0 || fetchSite.Equals("same-origin", StringComparison.OrdinalIgnoreCase);

        // Origem extra da allowlist (ex.: SPA em outro subdomínio): o navegador marca same-site, nunca cross-site.
        return extras.Contem(origem)
            && (fetchSite.Length == 0
                || fetchSite.Equals("same-origin", StringComparison.OrdinalIgnoreCase)
                || fetchSite.Equals("same-site", StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// <c>Seguranca:OrigensPermitidas</c> normalizada UMA vez (singleton): esquema://host[:porta] em minúsculas, sem barra final e
/// sem a porta padrão. Entrada que não é uma origem http(s) (esquema estranho, caminho, texto solto) é ignorada, em vez de
/// virar uma regra que nunca casa ou, pior, uma que casa por engano. Antes a configuração era ligada a cada requisição.
/// </summary>
public sealed class OrigensPermitidas
{
    private readonly HashSet<string> _origens;

    public OrigensPermitidas(IConfiguration config)
        => _origens = (config.GetSection("Seguranca:OrigensPermitidas").Get<string[]>() ?? [])
            .Select(Normalizar).OfType<string>().ToHashSet(StringComparer.Ordinal);

    public bool Contem(string? origem) => Normalizar(origem) is { } n && _origens.Contains(n);

    private static string? Normalizar(string? origem)
    {
        if (string.IsNullOrWhiteSpace(origem)) return null;
        if (!Uri.TryCreate(origem.Trim(), UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;
        // Origem não tem caminho, consulta nem fragmento ("/" sozinho é a barra final).
        if (uri.AbsolutePath != "/" || uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.UserInfo.Length > 0) return null;
        return uri.GetLeftPart(UriPartial.Authority).ToLowerInvariant();
    }
}
