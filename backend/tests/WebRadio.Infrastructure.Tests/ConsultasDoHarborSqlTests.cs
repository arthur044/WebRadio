using Microsoft.EntityFrameworkCore;
using WebRadio.Application.Features.Transmissao;
using WebRadio.Domain.Enums;
using WebRadio.Domain.Grade;
using WebRadio.Domain.Transmissao;
using WebRadio.Infrastructure.Harbor;
using WebRadio.Infrastructure.Tests.Suporte;
using Xunit;

namespace WebRadio.Infrastructure.Tests;

/// <summary>
/// E2-F05 contra SQL Server de verdade: a janela é calculada no SQL com SYSUTCDATETIME() (relógio REAL do servidor, não o IClock),
/// então os programas aqui são montados em volta do agora de verdade.
/// </summary>
public class ConsultasDoHarborSqlTests(BancoDeTeste banco) : IClassFixture<BancoDeTeste>
{
    private readonly Cenario _c = new(banco);

    private static DateTime Agora() => Truncar(DateTime.UtcNow);

    private static DateTime Truncar(DateTime d) => new(d.Ticks - (d.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc);

    private ConsultasDoHarbor Consultas() => new(_c.NovoDb());

    /// <summary>Locutor com credencial ativa e (opcionalmente) um programa dele de inicio a fim.</summary>
    private async Task<(Guid Identificador, Guid UsuarioId, Programa? Programa)> MontarAsync(
        Role role = Role.Locutor, TimeSpan? inicio = null, TimeSpan? duracao = null, Action<Programa, DateTime>? status = null, bool comPrograma = true)
    {
        var usuario = await _c.CriarUsuarioAsync(Cenario.Email(), role);
        var g = SegredoDeTransmissao.Gerar();
        var identificador = SegredoDeTransmissao.NovoIdentificador();
        await using var db = _c.NovoDb();
        db.CredenciaisTransmissao.Add(new CredencialTransmissao(Guid.NewGuid(), usuario.Id, identificador, g.Hash, g.Salt, DateTime.UtcNow));
        Programa? programa = null;
        if (comPrograma)
        {
            var ini = Agora().Add(inicio ?? TimeSpan.FromMinutes(-10));
            programa = new Programa(Guid.NewGuid(), "Programa", null, usuario.Id, ini, ini.Add(duracao ?? TimeSpan.FromHours(1)), ini);
            status?.Invoke(programa, ini.AddSeconds(1));
            db.Programas.Add(programa);
        }

        await db.SaveChangesAsync();
        return (identificador, usuario.Id, programa);
    }

    [SqlFact]
    public async Task Na_janela_devolve_o_programa_e_o_fim()
    {
        var (id, _, programa) = await MontarAsync();

        var janela = await Consultas().JanelaAsync(id, CancellationToken.None);

        Assert.NotNull(janela);
        Assert.Equal(programa!.Id, janela.ProgramaId);
        Assert.Equal(programa.FimUtc, janela.FimUtc);
        Assert.Equal(DateTimeKind.Utc, janela.FimUtc.Kind);
    }

    [SqlFact]
    public async Task Admin_dono_do_programa_tambem_transmite()
    {
        var (id, _, _) = await MontarAsync(Role.Admin);

        Assert.NotNull(await Consultas().JanelaAsync(id, CancellationToken.None));
    }

    [SqlFact]
    public async Task Status_AoVivo_nao_e_exigido_o_programa_Agendado_na_janela_vale()
    {
        var (agendado, _, _) = await MontarAsync();
        var (aoVivo, _, _) = await MontarAsync(status: (p, t) => p.IniciarAoVivo(t));

        Assert.NotNull(await Consultas().JanelaAsync(agendado, CancellationToken.None));
        Assert.NotNull(await Consultas().JanelaAsync(aoVivo, CancellationToken.None));
    }

    [SqlFact]
    public async Task Com_o_Worker_parado_o_AoVivo_cuja_janela_passou_e_negado()
    {
        // Ficou AoVivo (o Worker não rodou para finalizar), mas FimUtc já passou: a janela no SQL nega.
        var (id, _, _) = await MontarAsync(inicio: TimeSpan.FromMinutes(-10), duracao: TimeSpan.FromMinutes(5), status: (p, t) => p.IniciarAoVivo(t));

        Assert.Null(await Consultas().JanelaAsync(id, CancellationToken.None));
    }

    [SqlFact]
    public async Task Cada_motivo_de_recusa_devolve_null()
    {
        var consultas = Consultas();

        Assert.Null(await consultas.JanelaAsync(Guid.NewGuid(), CancellationToken.None)); // inexistente

        var (antes, _, _) = await MontarAsync(inicio: TimeSpan.FromMinutes(5));
        Assert.Null(await consultas.JanelaAsync(antes, CancellationToken.None));

        var (semPrograma, _, _) = await MontarAsync(comPrograma: false);
        Assert.Null(await consultas.JanelaAsync(semPrograma, CancellationToken.None));

        var (cancelado, _, _) = await MontarAsync(inicio: TimeSpan.FromMinutes(5), status: (p, t) => p.Cancelar(t));
        Assert.Null(await consultas.JanelaAsync(cancelado, CancellationToken.None));

        var (encerrado, _, _) = await MontarAsync(status: (p, t) => p.Encerrar(t.AddMinutes(1)));
        Assert.Null(await consultas.JanelaAsync(encerrado, CancellationToken.None));

        var (ouvinte, _, _) = await MontarAsync(Role.Ouvinte);
        Assert.Null(await consultas.JanelaAsync(ouvinte, CancellationToken.None));

        var (revogada, donoRevogada, _) = await MontarAsync();
        await using (var db = _c.NovoDb())
        {
            var c = await db.CredenciaisTransmissao.SingleAsync(x => x.UsuarioId == donoRevogada);
            c.Revogar(DateTime.UtcNow);
            await db.SaveChangesAsync();
        }

        Assert.Null(await consultas.JanelaAsync(revogada, CancellationToken.None));
        Assert.Null(await consultas.CredencialAtivaAsync(revogada, CancellationToken.None));

        var (desativado, donoDesativado, _) = await MontarAsync();
        await using (var db = _c.NovoDb())
        {
            // Direto no banco: o caminho normal (handler) também revogaria a credencial; aqui só o dono inativo.
            await db.Database.ExecuteSqlAsync($"UPDATE seg.Usuario SET Ativo = 0 WHERE Id = {donoDesativado}");
        }

        Assert.Null(await consultas.JanelaAsync(desativado, CancellationToken.None));
    }

    [SqlFact]
    public async Task Programa_de_outro_locutor_na_janela_nao_autoriza()
    {
        var (id, _, _) = await MontarAsync(comPrograma: false);
        await MontarAsync(); // outro locutor no ar agora

        Assert.Null(await Consultas().JanelaAsync(id, CancellationToken.None));
    }

    [SqlFact]
    public async Task CredencialAtiva_traz_salt_e_hash_e_RegistrarUso_grava_a_hora_do_servidor()
    {
        var (id, dono, _) = await MontarAsync();
        var consultas = Consultas();

        var c = await consultas.CredencialAtivaAsync(id, CancellationToken.None);
        Assert.NotNull(c);
        Assert.Equal(16, c.Salt.Length);
        Assert.Equal(32, c.SegredoHash.Length);

        var antes = DateTime.UtcNow.AddSeconds(-5);
        await consultas.RegistrarUsoAsync(c.Id, CancellationToken.None);

        await using var db = _c.NovoDb();
        var uso = (await db.CredenciaisTransmissao.SingleAsync(x => x.UsuarioId == dono)).UltimoUsoEmUtc;
        Assert.NotNull(uso);
        Assert.InRange(uso.Value, antes, DateTime.UtcNow.AddSeconds(5));
    }
}
