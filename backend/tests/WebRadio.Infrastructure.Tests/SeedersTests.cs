using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using WebRadio.Domain.Enums;
using WebRadio.Infrastructure.Seed;
using WebRadio.Infrastructure.Tests.Suporte;
using Xunit;

namespace WebRadio.Infrastructure.Tests;

public class SeedersTests(BancoDeTeste banco) : IClassFixture<BancoDeTeste>
{
    private static IConfiguration Config(params (string, string?)[] valores) =>
        new ConfigurationBuilder().AddInMemoryCollection(valores.ToDictionary(v => v.Item1, v => v.Item2)).Build();

    private AdminSeeder NovoAdminSeeder(Cenario c, IConfiguration config) =>
        new(c.NovoDb(), c.Hasher, c.Relogio, config, NullLogger<AdminSeeder>.Instance);

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
            Config(("Seed:DevData", ligado ? "true" : "false"), ("Seed:DevSenha", "senha-de-dev-1234")), NullLogger<DevDataSeeder>.Instance);

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
