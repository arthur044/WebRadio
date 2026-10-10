using System.Buffers.Text;

namespace WebRadio.Infrastructure.Opcoes;

/// <summary>
/// S-C02: recusa subir com segredo vazio, de exemplo (__GERAR__, CHANGE_ME) ou abaixo do mínimo. As mensagens citam só o
/// nome da configuração, nunca o valor.
/// </summary>
public static class ValidadorDeSegredos
{
    private static readonly string[] Marcadores = ["__GERAR__", "CHANGE_ME", "CHANGEME"];

    public static bool EhPlaceholder(string? valor) =>
        string.IsNullOrWhiteSpace(valor)
        || Marcadores.Any(m => valor.Contains(m, StringComparison.OrdinalIgnoreCase));

    public static void Texto(string nome, string? valor, int minimoCaracteres, ICollection<string> falhas)
    {
        if (EhPlaceholder(valor))
        {
            falhas.Add($"{nome} está vazio ou com valor de exemplo (__GERAR__). Rode infra/scripts/gerar-segredos.");
        }
        else if (valor!.Length < minimoCaracteres)
        {
            falhas.Add($"{nome} precisa ter ao menos {minimoCaracteres} caracteres.");
        }
    }

    /// <summary>Chave binária em base64url (sem padding). O tamanho é medido depois de decodificar (05 §5.1).</summary>
    public static void ChaveBinaria(string nome, string? valor, int minimoBytes, ICollection<string> falhas)
    {
        if (EhPlaceholder(valor))
        {
            falhas.Add($"{nome} está vazio ou com valor de exemplo (__GERAR__). Rode infra/scripts/gerar-segredos.");
            return;
        }

        if (!TentarDecodificar(valor!, out var bytes))
        {
            falhas.Add($"{nome} precisa estar em base64url.");
        }
        else if (bytes.Length < minimoBytes)
        {
            falhas.Add($"{nome} precisa ter ao menos {minimoBytes} bytes decodificados.");
        }
    }

    /// <summary>Base64Url.DecodeFromChars, nunca Convert.FromBase64String (que rejeita '-', '_' e a falta de padding).</summary>
    public static bool TentarDecodificar(string valor, out byte[] bytes)
    {
        try
        {
            bytes = Base64Url.DecodeFromChars(valor);
            return true;
        }
        catch (FormatException)
        {
            bytes = [];
            return false;
        }
    }
}
