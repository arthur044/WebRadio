using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using WebRadio.Api.Seguranca;
using WebRadio.Infrastructure.Seguranca;
using Xunit;

namespace WebRadio.Api.IntegrationTests;

/// <summary>05 §6: alg none, outra chave, role adulterada e token expirado → 401; token válido passa pela autenticação.</summary>
public class TokensEAutorizacaoTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _fabrica;

    public TokensEAutorizacaoTests(ApiFactory fabrica) => _fabrica = fabrica;

    private string Emitir(
        string? chave = null,
        string role = "Locutor",
        DateTime? expira = null,
        string issuer = "webradio-api",
        string audience = "webradio",
        string algoritmo = SecurityAlgorithms.HmacSha256)
    {
        var agora = DateTime.UtcNow;
        var bytes = Base64UrlDecode(chave ?? _fabrica.ChaveJwt);
        var descritor = new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            IssuedAt = expira is null ? agora : expira.Value.AddHours(-1),
            NotBefore = expira is null ? agora : expira.Value.AddHours(-1),
            Expires = expira ?? agora.AddMinutes(15),
            Claims = new Dictionary<string, object>
            {
                ["sub"] = Guid.NewGuid().ToString(),
                ["name"] = "Ana",
                ["role"] = role,
                ["jti"] = Guid.NewGuid().ToString("N"),
            },
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(bytes), algoritmo),
        };

        return new JsonWebTokenHandler().CreateToken(descritor);
    }

    private async Task<HttpStatusCode> ChamarAsync(string? token)
    {
        var cliente = _fabrica.CreateClient();

        // Um IP por chamada: o limite de 5/min do /auth/trocar-senha é por IP e não pode contaminar os casos entre si.
        cliente.DefaultRequestHeaders.Add(ApiFactory.HeaderIpRemoto, $"198.51.{Random.Shared.Next(1, 255)}.{Random.Shared.Next(1, 255)}");
        if (token is not null)
        {
            cliente.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        // /trocar-senha com corpo vazio: se a autenticação passa, a validação responde 400 sem tocar no banco; senão, 401.
        return (await cliente.PostAsJsonAsync("/api/v1/auth/trocar-senha", new { })).StatusCode;
    }

    [Fact]
    public async Task Token_valido_passa_pela_autenticacao() =>
        Assert.Equal(HttpStatusCode.BadRequest, await ChamarAsync(Emitir()));

    [Fact]
    public async Task Sem_token_401() => Assert.Equal(HttpStatusCode.Unauthorized, await ChamarAsync(null));

    [Fact]
    public async Task Token_alg_none_401()
    {
        static string B64(string json) => Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var exp = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds();
        var token = $"{B64("{\"alg\":\"none\",\"typ\":\"JWT\"}")}.{B64($"{{\"sub\":\"{Guid.NewGuid()}\",\"role\":\"Admin\",\"iss\":\"webradio-api\",\"aud\":\"webradio\",\"exp\":{exp}}}")}.";

        Assert.Equal(HttpStatusCode.Unauthorized, await ChamarAsync(token));
    }

    [Fact]
    public async Task Token_assinado_com_outra_chave_401() =>
        Assert.Equal(HttpStatusCode.Unauthorized, await ChamarAsync(Emitir(chave: ApiFactory.Chave())));

    [Fact]
    public async Task Role_adulterada_no_payload_401()
    {
        var partes = Emitir(role: "Locutor").Split('.');
        var payload = Encoding.UTF8.GetString(Base64UrlDecode(partes[1])).Replace("\"Locutor\"", "\"Admin\"");
        var adulterado = $"{partes[0]}.{Convert.ToBase64String(Encoding.UTF8.GetBytes(payload)).TrimEnd('=').Replace('+', '-').Replace('/', '_')}.{partes[2]}";

        Assert.Contains("Admin", payload, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Unauthorized, await ChamarAsync(adulterado));
    }

    [Fact]
    public async Task Token_expirado_401() =>
        Assert.Equal(HttpStatusCode.Unauthorized, await ChamarAsync(Emitir(expira: DateTime.UtcNow.AddMinutes(-5))));

    [Fact]
    public async Task Token_com_issuer_ou_audience_errados_401()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, await ChamarAsync(Emitir(issuer: "outro")));
        Assert.Equal(HttpStatusCode.Unauthorized, await ChamarAsync(Emitir(audience: "outro")));
    }

    [Fact]
    public async Task Refresh_nao_aceita_Bearer_no_lugar_do_cookie()
    {
        var cliente = _fabrica.CreateClient();
        cliente.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Emitir());
        var requisicao = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/refresh");
        requisicao.Headers.Add("Origin", cliente.BaseAddress!.GetLeftPart(UriPartial.Authority));

        Assert.Equal(HttpStatusCode.Unauthorized, (await cliente.SendAsync(requisicao)).StatusCode);
    }

    private static byte[] Base64UrlDecode(string valor) => System.Buffers.Text.Base64Url.DecodeFromChars(valor);
}

public class SenhaDefinitivaHandlerTests
{
    private static async Task<AuthorizationHandlerContext> Avaliar(bool deveTrocarSenha, bool rotaPermitida)
    {
        var claims = new List<Claim> { new("sub", Guid.NewGuid().ToString()) };
        if (deveTrocarSenha)
        {
            claims.Add(new Claim(JwtService.ClaimDeveTrocarSenha, "true"));
        }

        var metadados = rotaPermitida ? new object[] { new PermiteSenhaPendenteAttribute() } : [];
        var http = new DefaultHttpContext();
        http.SetEndpoint(new Endpoint(null, new EndpointMetadataCollection(metadados), "teste"));

        var requisito = new SenhaDefinitivaRequirement();
        var contexto = new AuthorizationHandlerContext([requisito], new ClaimsPrincipal(new ClaimsIdentity(claims, "teste")), http);
        await new SenhaDefinitivaHandler().HandleAsync(contexto);
        return contexto;
    }

    [Fact]
    public async Task Token_com_DeveTrocarSenha_so_passa_nas_rotas_marcadas()
    {
        Assert.False((await Avaliar(deveTrocarSenha: true, rotaPermitida: false)).HasSucceeded);
        Assert.True((await Avaliar(deveTrocarSenha: true, rotaPermitida: true)).HasSucceeded);
    }

    [Fact]
    public async Task Token_normal_passa_em_qualquer_rota()
    {
        Assert.True((await Avaliar(deveTrocarSenha: false, rotaPermitida: false)).HasSucceeded);
    }

    [Fact]
    public async Task A_falha_aponta_o_requisito_para_o_403_de_troca_obrigatoria()
    {
        var contexto = await Avaliar(deveTrocarSenha: true, rotaPermitida: false);

        Assert.Contains(contexto.FailureReasons, r => r.Handler is SenhaDefinitivaHandler);
    }
}

public class ConfiguracaoDeAutenticacaoTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _fabrica;

    public ConfiguracaoDeAutenticacaoTests(ApiFactory fabrica) => _fabrica = fabrica;

    [Fact]
    public async Task Politicas_PodeModerar_e_SomenteAdmin_existem_e_exigem_o_requisito_de_senha_definitiva()
    {
        var provedor = (IAuthorizationPolicyProvider)_fabrica.Services.GetService(typeof(IAuthorizationPolicyProvider))!;

        foreach (var nome in new[] { Politicas.PodeModerar, Politicas.SomenteAdmin })
        {
            var politica = await provedor.GetPolicyAsync(nome);
            Assert.NotNull(politica);
            Assert.Contains(politica.Requirements, r => r is SenhaDefinitivaRequirement);
            Assert.Contains(politica.Requirements, r => r is Microsoft.AspNetCore.Authorization.Infrastructure.RolesAuthorizationRequirement);
        }

        var admin = (Microsoft.AspNetCore.Authorization.Infrastructure.RolesAuthorizationRequirement)
            (await provedor.GetPolicyAsync(Politicas.SomenteAdmin))!.Requirements.Single(r => r is Microsoft.AspNetCore.Authorization.Infrastructure.RolesAuthorizationRequirement);
        Assert.Equal(["Admin"], admin.AllowedRoles);

        var moderar = (Microsoft.AspNetCore.Authorization.Infrastructure.RolesAuthorizationRequirement)
            (await provedor.GetPolicyAsync(Politicas.PodeModerar))!.Requirements.Single(r => r is Microsoft.AspNetCore.Authorization.Infrastructure.RolesAuthorizationRequirement);
        Assert.Equal(["Admin", "Locutor"], moderar.AllowedRoles.Order().ToArray());
    }

    [Fact]
    public void Validacao_do_JWT_segue_o_05_secao_6()
    {
        var opcoes = ((Microsoft.Extensions.Options.IOptionsMonitor<Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerOptions>)
            _fabrica.Services.GetService(typeof(Microsoft.Extensions.Options.IOptionsMonitor<Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerOptions>))!)
            .Get("Bearer");
        var p = opcoes.TokenValidationParameters;

        Assert.Equal([SecurityAlgorithms.HmacSha256], p.ValidAlgorithms);
        Assert.True(p.ValidateIssuer && p.ValidateAudience && p.ValidateLifetime && p.ValidateIssuerSigningKey);
        Assert.True(p.RequireExpirationTime && p.RequireSignedTokens);
        Assert.Equal(TimeSpan.FromSeconds(30), p.ClockSkew);
        Assert.Equal("role", p.RoleClaimType);
        Assert.Equal("name", p.NameClaimType);
        Assert.False(opcoes.MapInboundClaims);
    }

    [Fact]
    public async Task A_politica_padrao_exige_usuario_autenticado()
    {
        var provedor = (IAuthorizationPolicyProvider)_fabrica.Services.GetService(typeof(IAuthorizationPolicyProvider))!;

        var fallback = await provedor.GetFallbackPolicyAsync();

        Assert.NotNull(fallback);
        Assert.Contains(fallback.Requirements, r => r is Microsoft.AspNetCore.Authorization.Infrastructure.DenyAnonymousAuthorizationRequirement);
    }

    [Fact]
    public async Task Resposta_de_login_expoe_so_o_contrato_e_o_refresh_vai_no_cookie()
    {
        // Contrato de 02-api-rest §2: o corpo nunca carrega o refresh token (só o cookie HttpOnly).
        var json = JsonSerializer.Serialize(new WebRadio.Application.Autenticacao.SessaoResposta(
            "a", DateTime.UtcNow, false, new WebRadio.Application.Autenticacao.UsuarioDto(Guid.NewGuid(), "n", "e", "Admin", false)));

        Assert.DoesNotContain("refresh", json, StringComparison.OrdinalIgnoreCase);
        await Task.CompletedTask;
    }
}
