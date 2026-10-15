using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using WebRadio.Domain.Enums;
using WebRadio.Infrastructure.Seed;
using WebRadio.Infrastructure.Tests.Suporte;
using Xunit;

namespace WebRadio.Infrastructure.Tests;

public sealed class Ambiente(string nome) : Microsoft.Extensions.Hosting.IHostEnvironment
{
    public string EnvironmentName { get; set; } = nome;
    public string ApplicationName { get; set; } = "teste";
    public string ContentRootPath { get; set; } = ".";
    public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
}

public class SeedersTests(BancoDeTeste banco) : IClassFixture<BancoDeTeste>
{
    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task DevDataSeeder_fora_de_Development_falha_alto_e_nao_cria_nada(string ambiente)
    {
        var seeder = new DevDataSeeder(null!, null!, null!,
            Config(("Seed:DevData", "true"), ("Seed:DevSenha", "senha-de-dev-1234")), new Ambiente(ambiente), NullLogger<DevDataSeeder>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() => seeder.ExecutarAsync(default));
    }

    private static IConfiguration Config(params (string, string?)[] valores) =>
        new ConfigurationBuilder().AddInMemoryCollection(valores.ToDictionary(v => v.Item1, v => v.Item2)).Build();

    private static readonly WebRadio.Infrastructure.Seguranca.SenhasVazadas Vazadas = new();

    private AdminSeeder NovoAdminSeeder(Cenario c, IConfiguration config, Microsoft.Extensions.Logging.ILogger<AdminSeeder>? log = null) =>
        new(c.NovoDb(), c.Hasher, c.Relogio, config, Vazadas, log ?? NullLogger<AdminSeeder>.Instance);

    private async Task LimparAdminsAsync(Cenario c)
    {
        await using var db = c.NovoDb();
        await db.Database.ExecuteSqlRawAsync("DELETE FROM seg.RefreshToken; DELETE FROM seg.Usuario WHERE Role = 1");
    }

    [SqlFact]
    public async Task AdminSeeder_cria_um_Admin_com_DeveTrocarSenha_mesmo_com_tres_APIs_subindo_juntas()
    {
        var c = new Cenario(banco);
        await LimparAdminsAsync(c);
        var config = Config(("Seed:AdminEmail", "root@example.com"), ("Seed:AdminSenha", "senha-do-admin-123"));

        await Task.WhenAll(
            NovoAdminSeeder(c, config).ExecutarAsync(default),
            NovoAdminSeeder(c, config).ExecutarAsync(default),
            NovoAdminSeeder(c, config).ExecutarAsync(default));

        await using var db = c.NovoDb();
        var admin = Assert.Single(await db.Usuarios.Where(u => u.Role == Role.Admin).ToListAsync());
        Assert.True(admin.DeveTrocarSenha);
        Assert.Equal(WebRadio.Application.Abstracoes.ResultadoSenha.Ok, c.Hasher.Verificar(admin.SenhaHash, "senha-do-admin-123"));
    }

    [SqlFact]
    public async Task AdminSeeder_nao_cria_outro_Admin_se_ja_existe_um()
    {
        var c = new Cenario(banco);
        await LimparAdminsAsync(c);
        await c.CriarUsuarioAsync("primeiro@example.com", Role.Admin);

        await NovoAdminSeeder(c, Config(("Seed:AdminEmail", "root2@example.com"), ("Seed:AdminSenha", "senha-do-admin-123"))).ExecutarAsync(default);

        await using var db = c.NovoDb();
        Assert.Single(await db.Usuarios.Where(u => u.Role == Role.Admin).ToListAsync());
    }

    [SqlFact]
    public async Task AdminSeeder_nao_promove_nem_derruba_o_boot_se_o_email_ja_e_de_um_Ouvinte()
    {
        var c = new Cenario(banco);
        await LimparAdminsAsync(c);
        await c.CriarUsuarioAsync("ocupado@example.com", Role.Ouvinte);

        await NovoAdminSeeder(c, Config(("Seed:AdminEmail", "OCUPADO@example.com"), ("Seed:AdminSenha", "senha-do-admin-123"))).ExecutarAsync(default);

        await using var db = c.NovoDb();
        Assert.Empty(await db.Usuarios.Where(u => u.Role == Role.Admin).ToListAsync());
        Assert.Equal(Role.Ouvinte, (await db.Usuarios.SingleAsync(u => u.EmailNormalizado == "OCUPADO@EXAMPLE.COM")).Role);
    }

    [SqlTheory]
    [InlineData("curta")]                                   // < 12
    [InlineData("123456789012")]                            // vazada (lista local)
    public async Task AdminSeeder_recusa_senha_que_o_trocar_senha_recusaria_e_nao_cria_o_Admin(string senha)
    {
        var c = new Cenario(banco);
        await LimparAdminsAsync(c);

        var e = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NovoAdminSeeder(c, Config(("Seed:AdminEmail", "root@example.com"), ("Seed:AdminSenha", senha))).ExecutarAsync(default));

        Assert.Contains("Seed:AdminSenha", e.Message);
        Assert.DoesNotContain(senha, e.Message);
        await using var db = c.NovoDb();
        Assert.Empty(await db.Usuarios.Where(u => u.Role == Role.Admin).ToListAsync());
    }

    [SqlFact]
    public async Task AdminSeeder_recusa_senha_com_mais_de_128_caracteres()
    {
        var c = new Cenario(banco);
        await LimparAdminsAsync(c);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NovoAdminSeeder(c, Config(("Seed:AdminEmail", "root@example.com"), ("Seed:AdminSenha", new string('x', 129)))).ExecutarAsync(default));
    }

    [SqlFact]
    public async Task AdminSeeder_avisa_a_cada_boot_enquanto_a_senha_do_seed_estiver_no_ambiente_mesmo_com_Admin_existente()
    {
        var c = new Cenario(banco);
        await LimparAdminsAsync(c);
        await c.CriarUsuarioAsync("primeiro@example.com", Role.Admin);
        var log = new LoggerDeTeste<AdminSeeder>();
        var config = Config(("Seed:AdminEmail", "root@example.com"), ("Seed:AdminSenha", "senha-do-admin-123"));

        await NovoAdminSeeder(c, config, log).ExecutarAsync(default);
        await NovoAdminSeeder(c, config, log).ExecutarAsync(default);

        var avisos = log.Linhas.Where(l => l.Nivel == Microsoft.Extensions.Logging.LogLevel.Warning && l.Texto.Contains("Seed__AdminSenha")).ToArray();
        Assert.Equal(2, avisos.Length); // um por boot
        Assert.DoesNotContain(log.Linhas, l => l.Texto.Contains("senha-do-admin-123"));
    }

    [SqlFact]
    public async Task AdminSeeder_sem_senha_no_ambiente_nao_avisa()
    {
        var c = new Cenario(banco);
        var log = new LoggerDeTeste<AdminSeeder>();

        await NovoAdminSeeder(c, Config(), log).ExecutarAsync(default);

        Assert.Empty(log.Linhas);
    }

    [SqlFact]
    public async Task AdminSeeder_sem_configuracao_nao_faz_nada()
    {
        var c = new Cenario(banco);
        await using var db = c.NovoDb();
        var antes = await db.Usuarios.CountAsync();

        await NovoAdminSeeder(c, Config()).ExecutarAsync(default);

        Assert.Equal(antes, await db.Usuarios.CountAsync());
    }

    [SqlFact]
    public async Task DevDataSeeder_cria_os_dois_locutores_so_com_DevData_e_e_idempotente()
    {
        var c = new Cenario(banco);
        DevDataSeeder Novo(bool ligado) => new(c.NovoDb(), c.Hasher, c.Relogio,
            Config(("Seed:DevData", ligado ? "true" : "false"), ("Seed:DevSenha", "senha-de-dev-1234")), new Ambiente("Development"),
            NullLogger<DevDataSeeder>.Instance);

        await Novo(false).ExecutarAsync(default);
        await using (var db = c.NovoDb())
            Assert.Equal(0, await db.Usuarios.CountAsync(u => u.EmailNormalizado.StartsWith("LOCUTOR")));

        await Novo(true).ExecutarAsync(default);
        await Novo(true).ExecutarAsync(default);

        await using var depois = c.NovoDb();
        var locutores = await depois.Usuarios.Where(u => u.EmailNormalizado.StartsWith("LOCUTOR") && u.Role == Role.Locutor).ToListAsync();
        Assert.Equal(["LOCUTOR1@EXAMPLE.COM", "LOCUTOR2@EXAMPLE.COM"], locutores.Select(u => u.EmailNormalizado).Order().ToArray());
    }
}
