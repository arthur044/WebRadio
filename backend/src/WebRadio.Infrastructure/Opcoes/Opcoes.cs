using Microsoft.Extensions.Options;

namespace WebRadio.Infrastructure.Opcoes;

public sealed class JwtOptions
{
    public const string Secao = "Jwt";

    /// <summary>base64url, ao menos 32 bytes decodificados.</summary>
    public string SigningKey { get; set; } = string.Empty;

    public string Issuer { get; set; } = "webradio-api";

    public string Audience { get; set; } = "webradio";

    public int AccessTokenMinutos { get; set; } = 15;

    public int RefreshTokenDias { get; set; } = 7;

    public byte[] ChaveBytes() =>
        ValidadorDeSegredos.TentarDecodificar(SigningKey, out var bytes)
            ? bytes
            : throw new InvalidOperationException("Jwt:SigningKey não está em base64url.");
}

public sealed class SegurancaOptions
{
    public const string Secao = "Seguranca";

    /// <summary>base64url, ao menos 32 bytes decodificados. Pepper do hash de senha e do HMAC de IP/dispositivo.</summary>
    public string Pepper { get; set; } = string.Empty;

    /// <summary>S-A02: o NoOpMalwareScanner só existe com Development e este valor em true.</summary>
    public bool PermitirScannerNoOp { get; set; }

    /// <summary>Origens extras aceitas em /auth/refresh e /auth/logout, além da própria origem da requisição.</summary>
    public string[] OrigensPermitidas { get; set; } = [];

    public byte[] PepperBytes() =>
        ValidadorDeSegredos.TentarDecodificar(Pepper, out var bytes)
            ? bytes
            : throw new InvalidOperationException("Seguranca:Pepper não está em base64url.");
}

public sealed class RedisOptions
{
    public const string Secao = "Redis";

    public string Host { get; set; } = "redis";

    public int Porta { get; set; } = 6379;

    public string Password { get; set; } = string.Empty;
}

public sealed class StorageOptions
{
    public const string Secao = "Storage";

    /// <summary>Endpoint interno (rede dos contêineres), host:porta. Ex.: minio:9000.</summary>
    public string Endpoint { get; set; } = "minio:9000";

    public bool UsarSsl { get; set; }

    /// <summary>Origem pública com o prefixo do proxy (D18). Ex.: http://localhost:8080/storage.</summary>
    public string PublicEndpoint { get; set; } = "http://localhost:8080/storage";

    /// <summary>Credencial do svc-api. Nunca a root (S-A04).</summary>
    public string AccessKey { get; set; } = string.Empty;

    public string SecretKey { get; set; } = string.Empty;

    public string Regiao { get; set; } = "us-east-1";
}

public sealed class SeedOptions
{
    public const string Secao = "Seed";

    public string AdminEmail { get; set; } = string.Empty;

    public string AdminSenha { get; set; } = string.Empty;

    /// <summary>Só com Development: cria os 2 locutores fictícios que o infra/sql/seed-dev.sql espera.</summary>
    public bool DevData { get; set; }

    public string DevSenha { get; set; } = string.Empty;
}

public sealed class JwtOptionsValidator : IValidateOptions<JwtOptions>
{
    public ValidateOptionsResult Validate(string? name, JwtOptions options)
    {
        var falhas = new List<string>();
        ValidadorDeSegredos.ChaveBinaria("Jwt:SigningKey", options.SigningKey, 32, falhas);

        if (options.AccessTokenMinutos is < 1 or > 60)
        {
            falhas.Add("Jwt:AccessTokenMinutos precisa estar entre 1 e 60.");
        }

        if (options.RefreshTokenDias is < 1 or > 90)
        {
            falhas.Add("Jwt:RefreshTokenDias precisa estar entre 1 e 90.");
        }

        return falhas.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(falhas);
    }
}

public sealed class SegurancaOptionsValidator : IValidateOptions<SegurancaOptions>
{
    public ValidateOptionsResult Validate(string? name, SegurancaOptions options)
    {
        var falhas = new List<string>();
        ValidadorDeSegredos.ChaveBinaria("Seguranca:Pepper", options.Pepper, 32, falhas);
        return falhas.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(falhas);
    }
}

public sealed class RedisOptionsValidator : IValidateOptions<RedisOptions>
{
    public ValidateOptionsResult Validate(string? name, RedisOptions options)
    {
        var falhas = new List<string>();
        ValidadorDeSegredos.Texto("Redis:Password", options.Password, 16, falhas);

        if (string.IsNullOrWhiteSpace(options.Host))
        {
            falhas.Add("Redis:Host é obrigatório.");
        }

        return falhas.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(falhas);
    }
}

public sealed class StorageOptionsValidator : IValidateOptions<StorageOptions>
{
    public ValidateOptionsResult Validate(string? name, StorageOptions options)
    {
        var falhas = new List<string>();
        ValidadorDeSegredos.Texto("Storage:AccessKey", options.AccessKey, 8, falhas);
        ValidadorDeSegredos.Texto("Storage:SecretKey", options.SecretKey, 16, falhas);

        if (!Uri.TryCreate(options.PublicEndpoint, UriKind.Absolute, out _))
        {
            falhas.Add("Storage:PublicEndpoint precisa ser uma URL absoluta.");
        }

        if (string.IsNullOrWhiteSpace(options.Endpoint))
        {
            falhas.Add("Storage:Endpoint é obrigatório.");
        }

        return falhas.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(falhas);
    }
}

/// <summary>Seed é opcional, mas se vier pela metade ou com valor de exemplo o boot é recusado.</summary>
public sealed class SeedOptionsValidator : IValidateOptions<SeedOptions>
{
    public ValidateOptionsResult Validate(string? name, SeedOptions options)
    {
        var falhas = new List<string>();
        var temEmail = !string.IsNullOrWhiteSpace(options.AdminEmail);
        var temSenha = !string.IsNullOrWhiteSpace(options.AdminSenha);

        if (temEmail || temSenha)
        {
            if (!temEmail || options.AdminEmail.Contains("__GERAR__", StringComparison.Ordinal))
            {
                falhas.Add("Seed:AdminEmail está vazio ou com valor de exemplo.");
            }

            ValidadorDeSegredos.Texto("Seed:AdminSenha", options.AdminSenha, 12, falhas);
        }

        if (options.DevData)
        {
            ValidadorDeSegredos.Texto("Seed:DevSenha", options.DevSenha, 12, falhas);
        }

        return falhas.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(falhas);
    }
}
