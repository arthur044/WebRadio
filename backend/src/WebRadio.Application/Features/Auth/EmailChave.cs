using System.Globalization;
using System.Text;

namespace WebRadio.Application.Features.Auth;

/// <summary>
/// Chave de conta para o throttle. ATENÇÃO: <c>EmailNormalizado</c> usa a collation <c>Latin1_General_100_BIN2</c>
/// (comparação binária), então "jose@x.com" e "josé@x.com" são contas DISTINTAS no banco. Esta chave é de propósito
/// MAIS GROSSA que a conta (NFKD, sem diacríticos, maiúsculas): é defesa em profundidade, para que variantes
/// visualmente idênticas ou de largura total não ganhem 5 tentativas cada. O custo é inofensivo: duas contas
/// "parecidas" compartilham o mesmo contador de falhas.
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
