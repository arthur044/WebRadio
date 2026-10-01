using System.Globalization;
using System.Text;

namespace WebRadio.Application.Features.Auth;

/// <summary>
/// Chave de conta para o throttle. A coluna usa collation <c>Latin1_General_100_CI_AI_SC</c> (sem diferenciar
/// maiúsculas NEM acentos), então "José@x.com", "jose@x.com" e "ｊｏｓｅ@x.com" são a MESMA conta no banco. A chave
/// do throttle precisa colapsar igual (NFKC + sem diacríticos + maiúsculas), senão cada variante ganharia 5 tentativas.
/// </summary>
public static class EmailChave
{
    public static string Normalizar(string email)
    {
        var decomposto = email.Trim().Normalize(NormalizationForm.FormKD);
        var sb = new StringBuilder(decomposto.Length);
        foreach (var c in decomposto)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
        return sb.ToString().Normalize(NormalizationForm.FormC).ToUpperInvariant();
    }
}
