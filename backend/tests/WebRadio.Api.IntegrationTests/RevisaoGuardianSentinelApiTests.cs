using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using WebRadio.Api.Configuracao;
using WebRadio.Api.Erros;
using WebRadio.Api.Seguranca;
using Xunit;

namespace WebRadio.Api.IntegrationTests;

public class ExcecoesGlobaisTests
{
    [Fact]
    public async Task Cancelamento_pelo_cliente_nao_vira_500_nem_log_de_erro()
    {
        using var cts = new CancellationTokenSource();
        var http = new DefaultHttpContext { RequestAborted = cts.Token };
        await cts.CancelAsync();
        var tratador = new ExcecoesGlobais(null!, NullLogger<ExcecoesGlobais>.Instance);

        var tratado = await tratador.TryHandleAsync(http, new OperationCanceledException(), default);

        Assert.True(tratado);
        Assert.Equal(499, http.Response.StatusCode);
    }
}

public class VarreduraDeAutorizacaoTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _fabrica;

    public VarreduraDeAutorizacaoTests(ApiFactory fabrica) => _fabrica = fabrica;

    /// <summary>
    /// Todo endpoint que nao e AllowAnonymous precisa terminar numa politica com SenhaDefinitivaRequirement. Uma policy nova
    /// sem o requisito deixaria o token com DeveTrocarSenha (dts) contornar a troca obrigatoria.
    /// </summary>
    [Fact]
    public async Task Todo_endpoint_protegido_exige_o_requisito_de_senha_definitiva()
    {
        var fonte = (EndpointDataSource)_fabrica.Services.GetService(typeof(EndpointDataSource))!;
        var provedor = (IAuthorizationPolicyProvider)_fabrica.Services.GetService(typeof(IAuthorizationPolicyProvider))!;
        var protegidos = 0;

        foreach (var endpoint in fonte.Endpoints.OfType<RouteEndpoint>())
        {
            if (endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null)
            {
                continue;
            }

            var dados = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>();
            var politica = dados.Count > 0
                ? await AuthorizationPolicy.CombineAsync(provedor, dados)
                : await provedor.GetFallbackPolicyAsync();

            Assert.True(
                politica?.Requirements.Any(r => r is SenhaDefinitivaRequirement) == true,
                $"{endpoint.RoutePattern.RawText} nao exige SenhaDefinitivaRequirement");
            protegidos++;
        }

        Assert.True(protegidos >= 2, "a varredura deveria ter achado /auth/me e /auth/trocar-senha");
    }
}

public class ConfiguracaoDeRedeTests
{
    [Fact]
    public void Padroes_sao_validos()
    {
        Assert.Empty(new RedeOptions().Validar());
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    [InlineData("nao-e-ip")]
    public void BindInterno_recusa_todas_as_interfaces_e_valor_invalido(string ip)
    {
        Assert.NotEmpty(new RedeOptions { BindInterno = [ip] }.Validar());
    }

    [Fact]
    public void Portas_iguais_e_rede_invalida_sao_recusadas()
    {
        Assert.NotEmpty(new RedeOptions { PortaInterna = 8080, PortaPublica = 8080 }.Validar());
        Assert.NotEmpty(new RedeOptions { KnownNetworks = ["lixo"] }.Validar());
    }
}

public class LimiteDeCorpoTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _fabrica;

    public LimiteDeCorpoTests(ApiFactory fabrica) => _fabrica = fabrica;

    [Fact]
    public async Task Corpo_acima_do_teto_nas_rotas_de_auth_e_recusado_quando_o_servidor_aplica_o_limite()
    {
        var cliente = _fabrica.CreateClient();
        cliente.DefaultRequestHeaders.Add(ApiFactory.HeaderIpRemoto, "198.51.100.200");
        var enorme = new StringContent("{\"email\":\"" + new string('a', 64 * 1024) + "\"}", Encoding.UTF8, "application/json");

        var resposta = await cliente.PostAsync("/api/v1/auth/login", enorme);

        // O TestServer nao expoe IHttpMaxRequestBodySizeFeature gravavel: la o limite nao e aplicado (o validador responde 400).
        // No Kestrel real o limite de 4 KB gera 413. Aceita os dois, mas nunca 200/500.
        Assert.Contains(resposta.StatusCode, new[] { HttpStatusCode.BadRequest, HttpStatusCode.RequestEntityTooLarge });
    }
}
