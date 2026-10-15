using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using WebRadio.Application;
using WebRadio.Application.Abstracoes;
using WebRadio.Domain.Enums;
using WebRadio.Domain.Grade;
using WebRadio.Infrastructure.Persistencia;
using WebRadio.Infrastructure.Seed;
using WebRadio.Infrastructure.Tests.Suporte;
using Xunit;

namespace WebRadio.Infrastructure.Tests;

/// <summary>E2-A03: o seed de dev cria os locutores e a semana de grade PELO comando da E2-F01 (respeita as regras e o lock).</summary>
public class DevDataSeederTests(BancoDeTeste banco) : IClassFixture<BancoDeTeste>
{
    /// <summary>O DevDataSeeder como a Api o monta: o IMediator real, com o pipeline de validação, sobre o mesmo DbContext.</summary>
    internal static DevDataSeeder Novo(Cenario c, bool ligado)
    {
        var db = c.NovoDb();
        var servicos = new ServiceCollection().AddLogging();
        servicos.AddSingleton<IRadioDbContext>(db);
        servicos.AddSingleton<ITransacaoComTrava>(new TransacaoComTrava(db));
        servicos.AddSingleton<IClock>(c.Relogio);
        servicos.AddApplication();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Seed:DevData"] = ligado ? "true" : "false",
            ["Seed:DevSenha"] = "senha-de-dev-1234",
        }).Build();
        return new DevDataSeeder(db, c.Hasher, c.Relogio, config, new Ambiente("Development"), NullLogger<DevDataSeeder>.Instance,
            servicos.BuildServiceProvider().GetRequiredService<IMediator>());
    }

    /// <summary>O banco é da classe (compartilhado): cada teste parte do zero do que o seed cria.</summary>
    private static async Task LimparSeedAsync(Cenario c)
    {
        await using var db = c.NovoDb();
        await db.Database.ExecuteSqlRawAsync("DELETE FROM grade.Programa; DELETE FROM interacao.Divulgacao;");
    }

    [SqlFact]
    public async Task Cria_sete_dias_de_grade_pelo_comando_alternando_os_locutores_e_tres_divulgacoes()
    {
        var c = new Cenario(banco);
        await LimparSeedAsync(c);

        await Novo(c, ligado: true).ExecutarAsync(default);

        await using var db = c.NovoDb();
        var programas = await db.Programas.AsNoTracking().Where(p => p.Titulo.StartsWith("[seed] ")).OrderBy(p => p.InicioUtc).ToListAsync();
        Assert.Equal(7, programas.Count);
        Assert.All(programas, p => Assert.Equal(StatusPrograma.Agendado, p.Status));
        var amanha = c.Relogio.UtcNow.Date.AddDays(1);
        Assert.Equal(Enumerable.Range(0, 7).Select(n => amanha.AddDays(n).AddHours(12)), programas.Select(p => p.InicioUtc));
        Assert.All(programas, p => Assert.Equal(TimeSpan.FromHours(2), p.FimUtc - p.InicioUtc));
        Assert.Equal(2, programas.Select(p => p.LocutorId).Distinct().Count()); // alternam os dois locutores do seed
        for (var i = 1; i < programas.Count; i++) Assert.True(programas[i].InicioUtc >= programas[i - 1].FimUtc); // sem sobreposição
        Assert.Equal(3, await db.Divulgacoes.CountAsync(d => d.Titulo.StartsWith("[seed] ")));
    }

    [SqlFact]
    public async Task Rodar_de_novo_nao_duplica_nada()
    {
        var c = new Cenario(banco);
        await LimparSeedAsync(c);
        await Novo(c, true).ExecutarAsync(default);
        await using (var db = c.NovoDb())
        {
            var antes = (await db.Programas.CountAsync(), await db.Divulgacoes.CountAsync(), await db.Usuarios.CountAsync());
            await Novo(c, true).ExecutarAsync(default);
            await using var depois = c.NovoDb();
            Assert.Equal(antes, (await depois.Programas.CountAsync(), await depois.Divulgacoes.CountAsync(), await depois.Usuarios.CountAsync()));
        }
    }

    [SqlFact]
    public async Task Sem_DevData_nada_e_criado_e_um_dia_ocupado_por_outro_programa_e_pulado_sem_derrubar_o_boot()
    {
        var c = new Cenario(banco);
        await LimparSeedAsync(c);
        await using (var db = c.NovoDb())
        {
            var antes = await db.Programas.CountAsync();
            await Novo(c, ligado: false).ExecutarAsync(default);
            Assert.Equal(antes, await db.Programas.CountAsync());
        }

        // Programa de outro autor no meio do 3º dia: o comando respeita a grade e devolve conflito; o seed pula só esse dia.
        var locutor = await c.CriarUsuarioAsync(Cenario.Email(), Role.Locutor);
        var terceiroDia = c.Relogio.UtcNow.Date.AddDays(1 + 2).AddHours(12);
        await using (var db = c.NovoDb())
        {
            db.Programas.Add(new Programa(Guid.CreateVersion7(), "Ocupado", null, locutor.Id, terceiroDia.AddMinutes(30), terceiroDia.AddMinutes(90), c.Relogio.UtcNow));
            await db.SaveChangesAsync();
        }

        await Novo(c, true).ExecutarAsync(default);

        await using var leitura = c.NovoDb();
        var criados = await leitura.Programas.AsNoTracking().Where(p => p.Titulo.StartsWith("[seed] ")).ToListAsync();
        Assert.Equal(6, criados.Count);
        Assert.DoesNotContain(criados, p => p.InicioUtc == terceiroDia);
    }
}
