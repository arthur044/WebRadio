using Microsoft.AspNetCore.Builder;
using System.Buffers.Text;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace WebRadio.Api.IntegrationTests;

/// <summary>
/// Sobe a Api de verdade (pipeline, DI, validação de opções) com configuração válida e nenhum serviço externo: nada abre
/// conexão com SQL, Redis ou MinIO no boot. O TestServer não tem socket, então a "porta local" vem do cabeçalho X-Test-Porta.
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    public const string HeaderPorta = "X-Test-Porta";
    public const string HeaderIpRemoto = "X-Test-Ip-Remoto";

    public string ChaveJwt { get; } = Chave();

    public Dictionary<string, string?> Sobrescritas { get; } = new();

    public static string Chave(int bytes = 32) => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(bytes));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        // UseSetting (e não ConfigureAppConfiguration): o Program lê builder.Configuration durante o próprio top-level,
        // antes de os callbacks da fábrica serem aplicados; UseSetting entra na configuração do host desde o início.
        {
            var valores = new Dictionary<string, string?>
            {
                ["ConnectionStrings:RadioDb"] = "Server=sql-inexistente;Database=x;User Id=u;Password=senha-de-teste-123;Connect Timeout=1",
                ["Jwt:SigningKey"] = ChaveJwt,
                ["Seguranca:Pepper"] = Chave(),
                ["Redis:Host"] = "redis-inexistente",
                ["Redis:Password"] = Chave(24),
                ["Storage:Endpoint"] = "minio-inexistente:9000",
                ["Storage:PublicEndpoint"] = "http://localhost:8080/storage",
                ["Storage:AccessKey"] = "svc-api-acesso",
                ["Storage:SecretKey"] = Chave(30),
            };

            foreach (var (chave, valor) in Sobrescritas)
            {
                valores[chave] = valor;
            }

            foreach (var (chave, valor) in valores)
            {
                builder.UseSetting(chave, valor);
            }
        }

        builder.ConfigureTestServices(services => services.AddSingleton<Microsoft.AspNetCore.Hosting.IStartupFilter, SimulaSocket>());
    }

    /// <summary>Roda antes de tudo no pipeline: aplica a porta local e o IP remoto pedidos pelo teste.</summary>
    private sealed class SimulaSocket : Microsoft.AspNetCore.Hosting.IStartupFilter
    {
        public Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> Configure(Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> next) =>
            app =>
            {
                app.Use(async (contexto, proximo) =>
                {
                    if (int.TryParse(contexto.Request.Headers[HeaderPorta], out var porta))
                    {
                        contexto.Connection.LocalPort = porta;
                    }

                    if (System.Net.IPAddress.TryParse(contexto.Request.Headers[HeaderIpRemoto], out var ip))
                    {
                        contexto.Connection.RemoteIpAddress = ip;
                    }

                    await proximo();
                });

                next(app);
            };
    }
}
