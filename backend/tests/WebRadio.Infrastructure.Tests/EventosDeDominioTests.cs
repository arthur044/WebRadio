using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WebRadio.Application.Abstracoes;
using WebRadio.Domain.Enums;
using WebRadio.Domain.Usuarios;
using WebRadio.Infrastructure.Persistencia;
using WebRadio.Infrastructure.Persistencia.Eventos;
using Xunit;

namespace WebRadio.Infrastructure.Tests;

/// <summary>D22: os eventos de domínio são despachados ANTES do commit, e o que o handler muda entra no mesmo SaveChanges.</summary>
public class EventosDeDominioTests
{
    private static readonly DateTime Agora = new(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);

    private sealed class HandlerQueRenomeia(RadioDbContext db) : IDomainEventHandler<UsuarioCredenciaisAlteradas>
    {
        public List<UsuarioCredenciaisAlteradas> Recebidos { get; } = [];

        public async Task HandleAsync(UsuarioCredenciaisAlteradas evento, CancellationToken ct)
        {
            Recebidos.Add(evento);
            // Consulta e altera outra linha no meio do SaveChanges: precisa ser gravada junto.
            var outro = await db.Usuarios.FirstAsync(u => u.Email == "outro@example.com", ct);
            outro.AlterarRole(Role.Admin, ehUltimoAdminAtivo: false, Agora);
        }
    }

    private static (ServiceProvider Sp, string Banco) Montar()
    {
        var banco = Guid.NewGuid().ToString();
        var services = new ServiceCollection();
        services.AddScoped<DespachanteDeEventosDeDominio>();
        services.AddDbContext<RadioDbContext>((sp, o) => o.UseInMemoryDatabase(banco).ComEventosDeDominio(sp));
        services.AddScoped<HandlerQueRenomeia>();
        services.AddScoped<IDomainEventHandler<UsuarioCredenciaisAlteradas>>(sp => sp.GetRequiredService<HandlerQueRenomeia>());
        return (services.BuildServiceProvider(), banco);
    }

    private static Usuario Usuario(string email, Role role = Role.Locutor) =>
        new(Guid.NewGuid(), "Nome", email, "hash", role, deveTrocarSenha: false, Agora);

    [Fact]
    public async Task SaveChangesAsync_despacha_o_evento_e_grava_o_que_o_handler_mudou()
    {
        var (sp, _) = Montar();
        Guid alvo;
        using (var escopo = sp.CreateScope())
        {
            var db = escopo.ServiceProvider.GetRequiredService<RadioDbContext>();
            var u = Usuario("ana@example.com");
            db.Usuarios.AddRange(u, Usuario("outro@example.com"));
            await db.SaveChangesAsync();
            alvo = u.Id;

            u.Desativar(ehUltimoAdminAtivo: false, Agora);
            await db.SaveChangesAsync();

            var handler = escopo.ServiceProvider.GetRequiredService<HandlerQueRenomeia>();
            var evento = handler.Recebidos[0];
            Assert.Equal((alvo, MotivoAlteracaoCredenciais.Desativado), (evento.UsuarioId, evento.Motivo));
            Assert.Empty(u.DomainEvents);
        }

        using (var escopo = sp.CreateScope())
        {
            var db = escopo.ServiceProvider.GetRequiredService<RadioDbContext>();
            Assert.Equal(Role.Admin, (await db.Usuarios.SingleAsync(u => u.Email == "outro@example.com")).Role);
        }
    }

    [Fact]
    public async Task Evento_levantado_pelo_handler_tambem_e_despachado_e_nao_entra_em_laco()
    {
        var (sp, _) = Montar();
        using var escopo = sp.CreateScope();
        var db = escopo.ServiceProvider.GetRequiredService<RadioDbContext>();
        var u = Usuario("ana@example.com");
        db.Usuarios.AddRange(u, Usuario("outro@example.com"));
        await db.SaveChangesAsync();

        u.Desativar(ehUltimoAdminAtivo: false, Agora);
        await db.SaveChangesAsync();

        // O handler promove "outro" (RoleAlterada) e esse evento novo é despachado na rodada seguinte.
        var handler = escopo.ServiceProvider.GetRequiredService<HandlerQueRenomeia>();
        Assert.Equal([MotivoAlteracaoCredenciais.Desativado, MotivoAlteracaoCredenciais.RoleAlterada], handler.Recebidos.Select(e => e.Motivo));
    }

    [Fact]
    public void SaveChanges_sincrono_com_evento_pendente_falha_em_vez_de_perder_o_evento()
    {
        var (sp, _) = Montar();
        using var escopo = sp.CreateScope();
        var db = escopo.ServiceProvider.GetRequiredService<RadioDbContext>();
        var u = Usuario("ana@example.com");
        db.Usuarios.Add(u);
        db.SaveChanges(); // sem evento: passa

        u.Desativar(ehUltimoAdminAtivo: false, Agora);

        Assert.Throws<InvalidOperationException>(() => db.SaveChanges());
    }
}
