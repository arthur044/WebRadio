using System.Buffers.Text;
using Microsoft.Extensions.Options;

namespace WebRadio.Api.Segredos;

public sealed class JwtOptions
{
    // Nunca imprimir segredos (a máscara do Serilog só vale para {@Obj}).
    public override string ToString() => nameof(JwtOptions);

    public const string Secao = "Jwt";
    public string? SigningKey { get; set; }

    /// <summary>Opcional: chave anterior aceita só na VALIDAÇÃO durante a rotação (05 §5.1). Vazia = sem rotação.</summary>
    public string? SigningKeyAnterior { get; set; }
}

public sealed class SegurancaOptions
{
    // Nunca imprimir segredos (a máscara do Serilog só vale para {@Obj}).
    public override string ToString() => nameof(SegurancaOptions);

    public const string Secao = "Seguranca";
    public string? Pepper { get; set; }
}

public sealed class RedisOptions
{
    // Nunca imprimir segredos (a máscara do Serilog só vale para {@Obj}).
    public override string ToString() => nameof(RedisOptions);

    public const string Secao = "Redis";
    public string? Host { get; set; }
    public int? Porta { get; set; }
    public string? Password { get; set; }
}

public sealed class StorageOptions
{
    // Nunca imprimir segredos (a máscara do Serilog só vale para {@Obj}).
    public override string ToString() => nameof(StorageOptions);

    public const string Secao = "Storage";

    /// <summary>Endpoint interno (rede dos contêineres), host:porta. Ex.: minio:9000.</summary>
    public string? Endpoint { get; set; }

    /// <summary>Origem pública com o prefixo do proxy (D18). Ex.: http://localhost:8080/storage.</summary>
    public string? PublicEndpoint { get; set; }

    /// <summary>Credencial do svc-api. Nunca a root (S-A04).</summary>
    public string? AccessKey { get; set; }
    public string? SecretKey { get; set; }
}

public sealed class SeedOptions
{
    public override string ToString() => nameof(SeedOptions);

    public const string Secao = "Seed";
    public string? AdminEmail { get; set; }
    public string? AdminSenha { get; set; }
    public bool DevData { get; set; }
    public string? DevSenha { get; set; }
}

public sealed class PlayoutOptions
{
    // Nunca imprimir segredos (a máscara do Serilog só vale para {@Obj}).
    public override string ToString() => nameof(PlayoutOptions);

    public const string Secao = "Playout";
    public string? Token { get; set; }

    /// <summary>Opcional: token anterior, aceito durante a rotação (S-A06). Vazio = sem rotação em curso.</summary>
    public string? TokenAnterior { get; set; }
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
        => RegraSegredo.Resultado(
            RegraSegredo.Verificar("Jwt:SigningKey", options.SigningKey, 32, base64Url: true)
            ?? (string.IsNullOrWhiteSpace(options.SigningKeyAnterior)
                ? null
                : options.SigningKeyAnterior == options.SigningKey
                    ? "Jwt:SigningKeyAnterior é igual à Jwt:SigningKey: remova a anterior ou use a chave antiga de verdade."
                    : RegraSegredo.Verificar("Jwt:SigningKeyAnterior", options.SigningKeyAnterior, 32, base64Url: true)));
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
        => RegraSegredo.Resultado(
            RegraSegredo.Verificar("Playout:Token", options.Token, 32, base64Url: false)
            ?? (string.IsNullOrWhiteSpace(options.TokenAnterior)
                ? null
                : options.TokenAnterior == options.Token
                    ? "Playout:TokenAnterior é igual ao Playout:Token: remova o anterior ou use o token antigo de verdade."
                    : RegraSegredo.Verificar("Playout:TokenAnterior", options.TokenAnterior, 32, base64Url: false)));
}

/// <summary>Redis é opcional em Development (cai para a memória); configurado, a senha segue o fail-fast do S-C02.</summary>
public sealed class RedisOptionsValidator : IValidateOptions<RedisOptions>
{
    public ValidateOptionsResult Validate(string? name, RedisOptions options)
        => string.IsNullOrWhiteSpace(options.Host)
            ? ValidateOptionsResult.Success
            : RegraSegredo.Resultado(RegraSegredo.Verificar("Redis:Password", options.Password, 16, base64Url: false));
}

/// <summary>Storage é opcional em Development; configurado, a credencial segue o fail-fast do S-C02 e a URL pública precisa ser absoluta.</summary>
public sealed class StorageOptionsValidator : IValidateOptions<StorageOptions>
{
    public ValidateOptionsResult Validate(string? name, StorageOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Endpoint)) return ValidateOptionsResult.Success;

        return RegraSegredo.Resultado(
            RegraSegredo.Verificar("Storage:AccessKey", options.AccessKey, 8, base64Url: false)
            ?? RegraSegredo.Verificar("Storage:SecretKey", options.SecretKey, 16, base64Url: false)
            ?? (Uri.TryCreate(options.PublicEndpoint, UriKind.Absolute, out var url) && (url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeHttps)
                ? null
                : "Storage:PublicEndpoint precisa ser uma URL http(s) absoluta (ex.: https://radio.example.com/storage)."));
    }
}

/// <summary>Seed é opcional, mas se vier pela metade ou com valor de exemplo o boot é recusado.</summary>
public sealed class SeedOptionsValidator : IValidateOptions<SeedOptions>
{
    public ValidateOptionsResult Validate(string? name, SeedOptions options)
    {
        var temEmail = !string.IsNullOrWhiteSpace(options.AdminEmail);
        var temSenha = !string.IsNullOrWhiteSpace(options.AdminSenha);
        string? erro = null;

        if (temEmail || temSenha)
        {
            erro = !temEmail || options.AdminEmail!.Contains("__GERAR__", StringComparison.Ordinal)
                    || !new System.ComponentModel.DataAnnotations.EmailAddressAttribute().IsValid(options.AdminEmail)
                ? "Seed:AdminEmail está vazio, com valor de exemplo ou não é um e-mail válido."
                : RegraSegredo.Verificar("Seed:AdminSenha", options.AdminSenha, 12, base64Url: false);
        }

        if (erro is null && options.DevData)
            erro = RegraSegredo.Verificar("Seed:DevSenha", options.DevSenha, 12, base64Url: false);

        return RegraSegredo.Resultado(erro);
    }
}
