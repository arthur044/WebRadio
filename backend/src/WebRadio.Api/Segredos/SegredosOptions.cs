using System.Buffers.Text;
using Microsoft.Extensions.Options;

namespace WebRadio.Api.Segredos;

public sealed class JwtOptions
{
    public const string Secao = "Jwt";
    public string? SigningKey { get; set; }
}

public sealed class SegurancaOptions
{
    public const string Secao = "Seguranca";
    public string? Pepper { get; set; }
}

public sealed class PlayoutOptions
{
    public const string Secao = "Playout";
    public string? Token { get; set; }
}

/// <summary>
/// Regras comuns do fail-fast de segredos (S-C02, 05-seguranca-auditoria.md §5.1): recusa vazio, marcador
/// de exemplo (<c>__GERAR__</c>, <c>CHANGE_ME</c>) e tamanho abaixo do mínimo. As mensagens dizem
/// QUAL configuração falhou e por quê, nunca o valor.
/// </summary>
internal static class RegraSegredo
{
    private static readonly string[] Marcadores = ["__GERAR__", "CHANGE_ME", "CHANGEME"];

    public static string? Verificar(string chave, string? valor, int minimoBytes, bool base64Url)
    {
        if (string.IsNullOrWhiteSpace(valor))
            return $"{chave} está vazio. Rode infra/scripts/gerar-segredos.sh.";
        if (Marcadores.Any(m => valor.Contains(m, StringComparison.OrdinalIgnoreCase)))
            return $"{chave} contém um valor de exemplo (__GERAR__/CHANGE_ME). Gere um segredo real.";

        if (!base64Url)
            return valor.Length < minimoBytes ? $"{chave} tem menos de {minimoBytes} caracteres." : null;

        // base64url sem padding; mede os bytes DECODIFICADOS (Convert.FromBase64String rejeitaria '-' e '_').
        if (!Base64Url.IsValid(valor, out var bytes))
            return $"{chave} não é base64url válido (alfabeto [A-Za-z0-9_-], sem padding).";
        return bytes < minimoBytes ? $"{chave} tem {bytes} bytes decodificados; o mínimo é {minimoBytes}." : null;
    }

    public static ValidateOptionsResult Resultado(string? erro) => erro is null ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(erro);
}

public sealed class JwtOptionsValidator : IValidateOptions<JwtOptions>
{
    public ValidateOptionsResult Validate(string? name, JwtOptions options)
        => RegraSegredo.Resultado(RegraSegredo.Verificar("Jwt:SigningKey", options.SigningKey, 32, base64Url: true));
}

public sealed class SegurancaOptionsValidator : IValidateOptions<SegurancaOptions>
{
    public ValidateOptionsResult Validate(string? name, SegurancaOptions options)
        => RegraSegredo.Resultado(RegraSegredo.Verificar("Seguranca:Pepper", options.Pepper, 32, base64Url: true));
}

public sealed class PlayoutOptionsValidator : IValidateOptions<PlayoutOptions>
{
    // `openssl rand -hex 32` gera 64 caracteres; 32 caracteres é o piso aceito.
    public ValidateOptionsResult Validate(string? name, PlayoutOptions options)
        => RegraSegredo.Resultado(RegraSegredo.Verificar("Playout:Token", options.Token, 32, base64Url: false));
}
