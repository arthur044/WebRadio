using System.Buffers.Text;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using WebRadio.Infrastructure.Persistencia;
using Microsoft.Extensions.DependencyInjection;

namespace WebRadio.Api.IntegrationTests;

/// <summary>Host da API para testes: segredos válidos em memória, sem SQL (a conexão nunca é aberta).</summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    public static readonly string ChaveValida = Base64Url.EncodeToString(Enumerable.Range(0, 48).Select(i => (byte)(i * 7)).ToArray());

    private readonly string _nomeDoBanco = Guid.NewGuid().ToString();

    public Dictionary<string, string?> Config { get; } = new()
    {
        ["Jwt:SigningKey"] = ChaveValida,
        ["Seguranca:Pepper"] = ChaveValida,
        ["Playout:Token"] = new string('a', 64),
        ["ConnectionStrings:RadioDb"] = "Server=nao-existe;Database=x;User Id=x;Password=x;TrustServerCertificate=True",
        ["Rede:SubnetApp"] = "172.28.0.0/24",
        ["AllowedHosts"] = "localhost;api",
    };

    /// <summary>IP do socket simulado e porta local (TestServer não tem socket de verdade).</summary>
    public IPAddress? RemoteIp { get; set; }
    public int LocalPort { get; set; } = 8080;
    public string Ambiente { get; set; } = "Development";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(Ambiente);
        // UseSetting (e não AddInMemoryCollection): o Program lê builder.Configuration no topo, antes
        // de ConfigureAppConfiguration ser aplicado.
        foreach (var (chave, valor) in Config) builder.UseSetting(chave, valor);
        builder.ConfigureTestServices(services =>
        {
            services.AddTransient<Microsoft.AspNetCore.Hosting.IStartupFilter>(_ => new SocketSimulado(this));

            // Banco em memória no lugar do SQL Server (sem Docker): o resto da composição é a real.
            foreach (var d in services.Where(d => d.ServiceType == typeof(DbContextOptions<RadioDbContext>)
                                                  || d.ServiceType.Name.StartsWith("IDbContextOptionsConfiguration", StringComparison.Ordinal)).ToList())
                services.Remove(d);
            services.AddDbContext<RadioDbContext>(o => o.UseInMemoryDatabase(_nomeDoBanco));
        });
    }

    private sealed class SocketSimulado(ApiFactory f) : Microsoft.AspNetCore.Hosting.IStartupFilter
    {
        public Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> Configure(Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> next)
            => app =>
            {
                app.Use((ctx, n) =>
                {
                    ctx.Connection.RemoteIpAddress = f.RemoteIp;
                    ctx.Connection.LocalPort = ctx.Request.Headers.TryGetValue("X-Test-LocalPort", out var p) ? int.Parse(p!) : f.LocalPort;
                    return n(ctx);
                });
                next(app);
            };
    }
}

public static class ApiFactoryExtensions
{
    public const string Senha = "uma-senha-bem-longa-123";

    /// <summary>Cria um usuário direto no banco (o hash vem do hasher real da aplicação).</summary>
    public static void CriarUsuario(this ApiFactory f, string email, WebRadio.Domain.Enums.Role role, bool deveTrocar = false, bool ativo = true)
    {
        using var escopo = f.Services.CreateScope();
        var db = escopo.ServiceProvider.GetRequiredService<WebRadio.Application.Abstracoes.IRadioDbContext>();
        var hasher = escopo.ServiceProvider.GetRequiredService<WebRadio.Application.Abstracoes.ISenhaHasher>();
        var agora = DateTime.UtcNow;
        var u = new WebRadio.Domain.Usuarios.Usuario(Guid.NewGuid(), "Fulano", email, hasher.Hash(Senha), role, deveTrocar, agora);
        if (!ativo) u.Desativar(false, agora);
        db.Usuarios.Add(u);
        db.SaveChangesAsync().GetAwaiter().GetResult();
    }
}
