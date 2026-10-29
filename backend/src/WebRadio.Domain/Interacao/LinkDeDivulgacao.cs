namespace WebRadio.Domain.Interacao;

/// <summary>
/// S-B03 (07, E3-F05): o link de uma divulgação é mostrado a todo o público e o servidor <b>nunca</b> o busca. Aceita só <c>https</c>, sem
/// <c>userinfo</c>, com host que é NOME DNS (<c>Uri.HostNameType == Dns</c>: nenhum IP literal, porque <c>https://2130706433/</c> e
/// <c>https://[::ffff:7f00:1]/</c> viram loopback depois de canonicalizados), com ponto e fora de <c>localhost</c>, <c>.localhost</c>,
/// <c>.local</c> e <c>.internal</c>, e sem controle nem espaço na string ORIGINAL. Grava <c>uri.AbsoluteUri</c> (a forma validada) com até
/// 2048. O <c>CK_Divulgacao_Link</c> do banco é só o último nível.
/// </summary>
public static class LinkDeDivulgacao
{
    public const int TamanhoMaximo = 2048;

    private static readonly string[] SufixosProibidos = [".local", ".internal", ".localhost"];

    /// <summary>A forma canônica do link, ou <c>null</c> se ele não passa em alguma regra.</summary>
    public static string? Normalizar(string? link)
    {
        if (string.IsNullOrEmpty(link) || link.Length > TamanhoMaximo) return null;
        // Controle (inclusive tab e quebra de linha) e espaço na string original: o Uri os "limparia" e o link validado seria outro.
        if (link.Any(c => char.IsControl(c) || char.IsWhiteSpace(c))) return null;
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttps) return null;
        if (uri.UserInfo.Length > 0) return null;
        if (uri.HostNameType != UriHostNameType.Dns) return null;

        var host = uri.IdnHost.TrimEnd('.').ToLowerInvariant();
        if (!host.Contains('.') || host == "localhost") return null;
        if (SufixosProibidos.Any(host.EndsWith)) return null;

        var canonico = uri.AbsoluteUri;
        return canonico.Length <= TamanhoMaximo ? canonico : null;
    }
}
