using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using WebRadio.Application.Abstracoes;
using WebRadio.Domain.Usuarios;
using WebRadio.Infrastructure.Opcoes;

namespace WebRadio.Infrastructure.Seguranca;

/// <summary>
/// Claims: sub, name, role, jti, iat, exp (iss = webradio-api, aud = webradio). Extra: "dts" = true só enquanto o usuário
/// precisa trocar a senha (DeveTrocarSenha); a Api restringe esse token a /auth/trocar-senha e /auth/me.
/// </summary>
public sealed class JwtService(IOptions<JwtOptions> opcoes, IClock clock) : IJwtService
{
    public const string ClaimDeveTrocarSenha = "dts";

    private readonly JsonWebTokenHandler _handler = new();
    private readonly SigningCredentials _credenciais = new(new SymmetricSecurityKey(opcoes.Value.ChaveBytes()), SecurityAlgorithms.HmacSha256);

    public AccessTokenEmitido Emitir(Usuario usuario)
    {
        var o = opcoes.Value;
        var agora = clock.UtcAgora;
        var expira = agora.AddMinutes(o.AccessTokenMinutos);

        var claims = new Dictionary<string, object>
        {
            [JwtRegisteredClaimNames.Sub] = usuario.Id.ToString(),
            [JwtRegisteredClaimNames.Name] = usuario.Nome,
            ["role"] = usuario.Role.ToString(),
            [JwtRegisteredClaimNames.Jti] = Guid.NewGuid().ToString("N"),
        };

        if (usuario.DeveTrocarSenha)
        {
            claims[ClaimDeveTrocarSenha] = true;
        }

        var descritor = new SecurityTokenDescriptor
        {
            Issuer = o.Issuer,
            Audience = o.Audience,
            IssuedAt = agora,
            NotBefore = agora,
            Expires = expira,
            Claims = claims,
            SigningCredentials = _credenciais,
        };

        return new AccessTokenEmitido(_handler.CreateToken(descritor), expira);
    }
}
