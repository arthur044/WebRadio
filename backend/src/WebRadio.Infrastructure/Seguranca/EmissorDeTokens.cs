using System.Buffers.Text;
using System.Security.Cryptography;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using WebRadio.Application.Abstracoes;
using WebRadio.Domain.Usuarios;

namespace WebRadio.Infrastructure.Seguranca;

/// <summary>Claims do JWT (02 §2): sub, name, role, jti, iat, exp; iss = webradio-api, aud = webradio.</summary>
public sealed class EmissorDeTokens(byte[] chave, IClock clock) : IEmissorDeTokens
{
    public const string Issuer = "webradio-api";
    public const string Audience = "webradio";
    public const string ClaimDeveTrocarSenha = "deve_trocar_senha";
    public static readonly TimeSpan DuracaoDoAccess = TimeSpan.FromMinutes(15);

    /// <summary>
    /// O <c>kid</c> identifica a chave de verdade (8 hex do SHA-256; não revela o segredo): um token emitido com a
    /// chave antiga continua achando a chave antiga durante a rotação, e não a nova que passou a se chamar "atual".
    /// </summary>
    public static string KidDe(byte[] chave) => Convert.ToHexString(SHA256.HashData(chave))[..8].ToLowerInvariant();

    private readonly SigningCredentials _credenciais = new(new SymmetricSecurityKey(chave) { KeyId = KidDe(chave) }, SecurityAlgorithms.HmacSha256);

    public TimeSpan DuracaoDoRefresh => TimeSpan.FromDays(7);

    public AccessToken EmitirAccessToken(Usuario usuario)
    {
        var agora = clock.UtcNow;
        var expira = agora.Add(DuracaoDoAccess);
        var claims = new Dictionary<string, object>
        {
            ["sub"] = usuario.Id.ToString(),
            ["name"] = usuario.Nome,
            ["role"] = usuario.Role.ToString(),
            ["jti"] = Guid.NewGuid().ToString("N"),
        };
        // Só presente quando verdadeiro: o token restrito só abre trocar-senha e /auth/me.
        if (usuario.DeveTrocarSenha) claims[ClaimDeveTrocarSenha] = "true";

        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Audience,
            IssuedAt = agora,
            NotBefore = agora,
            Expires = expira,
            Claims = claims,
            SigningCredentials = _credenciais,
        });
        return new AccessToken(token, expira);
    }

    public RefreshOpaco NovoRefresh()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return new RefreshOpaco(Base64Url.EncodeToString(bytes), SHA256.HashData(bytes));
    }
}
