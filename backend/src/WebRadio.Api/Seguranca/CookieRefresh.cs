using Microsoft.Extensions.Options;
using WebRadio.Infrastructure.Opcoes;

namespace WebRadio.Api.Seguranca;

/// <summary>
/// Cookie __Secure-wr_refresh (HttpOnly; Secure; SameSite=Strict; Path=/api/v1/auth) e checagem de Origin
/// (00-arquitetura §6, 02-api-rest §2).
/// </summary>
public static class CookieRefresh
{
    public const string Nome = "__Secure-wr_refresh";
    public const string Caminho = "/api/v1/auth";

    public static string? Ler(HttpContext contexto) =>
        contexto.Request.Cookies.TryGetValue(Nome, out var valor) && !string.IsNullOrEmpty(valor) ? valor : null;

    public static void Escrever(HttpContext contexto, string token, DateTime expiraEmUtc) =>
        contexto.Response.Cookies.Append(Nome, token, Opcoes(expiraEmUtc));

    public static void Apagar(HttpContext contexto) =>
        contexto.Response.Cookies.Delete(Nome, Opcoes(null));

    private static CookieOptions Opcoes(DateTime? expiraEmUtc) => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Strict,
        Path = Caminho,
        Expires = expiraEmUtc is null ? null : new DateTimeOffset(expiraEmUtc.Value, TimeSpan.Zero),
        IsEssential = true,
    };

    /// <summary>
    /// Refresh e logout só aceitam requisição da mesma origem. Origin ausente é recusado (POST de navegador sempre envia);
    /// Sec-Fetch-Site, quando existe, precisa ser same-origin.
    /// </summary>
    public static bool OrigemPermitida(HttpContext contexto)
    {
        var origem = contexto.Request.Headers.Origin.ToString();
        if (string.IsNullOrEmpty(origem))
        {
            return false;
        }

        var fetchSite = contexto.Request.Headers["Sec-Fetch-Site"].ToString();
        var propria = $"{contexto.Request.Scheme}://{contexto.Request.Host}";

        if (string.Equals(origem, propria, StringComparison.OrdinalIgnoreCase))
        {
            return fetchSite.Length == 0 || string.Equals(fetchSite, "same-origin", StringComparison.OrdinalIgnoreCase);
        }

        // Origem extra da allowlist (ex.: SPA em outro subdominio): o navegador marca same-site, nunca cross-site.
        var extras = contexto.RequestServices.GetRequiredService<IOptions<SegurancaOptions>>().Value.OrigensPermitidas;
        return extras.Contains(origem, StringComparer.OrdinalIgnoreCase)
            && (fetchSite.Length == 0
                || fetchSite.Equals("same-origin", StringComparison.OrdinalIgnoreCase)
                || fetchSite.Equals("same-site", StringComparison.OrdinalIgnoreCase));
    }
}
