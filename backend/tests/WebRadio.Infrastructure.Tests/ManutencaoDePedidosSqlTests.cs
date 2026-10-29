using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using WebRadio.Domain.Enums;
using WebRadio.Domain.Interacao;
using WebRadio.Domain.Usuarios;
using WebRadio.Infrastructure.Persistencia.Outbox;
using WebRadio.Infrastructure.Tests.Suporte;
using WebRadio.Infrastructure.Trabalhos;
using Xunit;

namespace WebRadio.Infrastructure.Tests;

/// <summary>
/// E3-F09 (07, E3-D8) contra SQL Server de verdade: o hash de IP aos 7 dias, a anonimização dos terminais aos 30 dias (usuário,
/// dedicatória e nome) e a limpeza dos eventos de pedido do outbox, entregues ou parados.
/// </summary>
public class ManutencaoDePedidosSqlTests(BancoDeTeste banco) : IClassFixture<BancoDeTeste>
{
    private static byte[] Hash() => RandomNumberGenerator.GetBytes(32);

    private async Task LimparAsync()
    {
        await using var db = banco.CriarContexto();
        await db.Database.ExecuteSqlRawAsync("DELETE FROM interacao.PedidoMusica; DELETE FROM infra.EventoOutbox;");
    }

    private async Task<Guid> UsuarioAsync()
    {
        await using var db = banco.CriarContexto();
        var u = new Usuario(Guid.CreateVersion7(), "Ana", Cenario.Email(), "hash", Role.Ouvinte, false, DateTime.UtcNow);
        db.Usuarios.Add(u);
        await db.SaveChangesAsync();
        return u.Id;
    }

    private async Task<PedidoMusica> PedidoAsync(int idadeEmDias, StatusPedido status = StatusPedido.Pendente, double moderadoHaHoras = 0, bool comUsuario = true)
    {
        var criado = DateTime.UtcNow.AddDays(-idadeEmDias);
        var usuario = comUsuario ? await UsuarioAsync() : (Guid?)null;
        var p = new PedidoMusica(Guid.CreateVersion7(), null, usuario, "Maria Silva", "Faixa", "Banda", "Para minha mãe", Hash(), Hash(), criado);
        var moderador = status == StatusPedido.Pendente ? Guid.Empty : await UsuarioAsync();
        var moderadoEm = DateTime.UtcNow.AddHours(-moderadoHaHoras);
        switch (status)
        {
            case StatusPedido.Aprovado: p.Aprovar(moderador, null, moderadoEm); break;
            case StatusPedido.Tocado: p.Aprovar(moderador, null, moderadoEm); p.MarcarTocado(moderadoEm); break;
            case StatusPedido.Rejeitado: p.Rejeitar(moderador, "Fora do estilo", moderadoEm); break;
        }

        await using var db = banco.CriarContexto();
        db.PedidosMusica.Add(p);
        await db.SaveChangesAsync();
        if (status == StatusPedido.Expirado)
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE interacao.PedidoMusica SET Status = 5 WHERE Id = {p.Id}");
        return p;
    }

    private async Task<PedidoMusica> LerAsync(Guid id)
    {
        await using var db = banco.CriarContexto();
        return await db.PedidosMusica.AsNoTracking().SingleAsync(p => p.Id == id);
    }

    private async Task<ResultadoDaManutencao> ExecutarAsync(int lote = Manutencao.LotePadrao)
    {
        await using var db = banco.CriarContexto();
        return await new Manutencao(db) { Lote = lote }.ExecutarAsync(default);
    }

    [SqlFact]
    public async Task Hash_de_IP_de_8_dias_e_anulado_e_o_de_dispositivo_e_mantido()
    {
        await LimparAsync();
        var oito = await PedidoAsync(8);
        var seis = await PedidoAsync(6);

        var r = await ExecutarAsync();

        Assert.Equal(1, r.IpsApagados);
        var a = await LerAsync(oito.Id);
        Assert.Null(a.ListenerIpHash);
        Assert.NotNull(a.ListenerDeviceHash);
        Assert.NotNull((await LerAsync(seis.Id)).ListenerIpHash);
    }

    [SqlFact]
    public async Task Terminais_de_31_dias_perdem_usuario_dedicatoria_nome_e_hashes_e_mantem_titulo_status_e_datas()
    {
        await LimparAsync();
        var ids = new List<(StatusPedido Status, Guid Id)>();
        foreach (var status in new[] { StatusPedido.Rejeitado, StatusPedido.Tocado, StatusPedido.Expirado })
            ids.Add((status, (await PedidoAsync(31, status, moderadoHaHoras: 31 * 24)).Id));

        var r = await ExecutarAsync();

        Assert.Equal(3, r.PedidosAnonimizados);
        foreach (var (status, id) in ids)
        {
            var p = await LerAsync(id);
            Assert.Null(p.UsuarioId);
            Assert.Null(p.Mensagem);
            Assert.Equal("(anônimo)", p.NomeOuvinte);
            Assert.Null(p.ListenerDeviceHash);
            Assert.Null(p.ListenerIpHash);
            Assert.Equal("Faixa", p.TituloMusica);
            Assert.Equal("Banda", p.Artista);
            Assert.Equal(status, p.Status);
        }
    }

    [SqlFact]
    public async Task Aprovado_com_mais_de_24_horas_e_mais_de_30_dias_conta_como_terminal()
    {
        await LimparAsync();
        var antigo = await PedidoAsync(31, StatusPedido.Aprovado, moderadoHaHoras: 25);

        await ExecutarAsync();

        var p = await LerAsync(antigo.Id);
        Assert.Equal("(anônimo)", p.NomeOuvinte);
        Assert.Null(p.Mensagem);
        Assert.Null(p.UsuarioId);
    }

    [SqlFact]
    public async Task Pendente_e_Aprovado_moderado_ha_menos_de_24_horas_ficam_intactos_no_texto_mesmo_com_mais_de_30_dias()
    {
        await LimparAsync();
        var pendente = await PedidoAsync(31);
        var aprovadoRecente = await PedidoAsync(31, StatusPedido.Aprovado, moderadoHaHoras: 2);

        await ExecutarAsync();

        foreach (var id in new[] { pendente.Id, aprovadoRecente.Id })
        {
            var p = await LerAsync(id);
            Assert.Equal("Maria Silva", p.NomeOuvinte);
            Assert.Equal("Para minha mãe", p.Mensagem);
            Assert.NotNull(p.UsuarioId);
            Assert.Null(p.ListenerDeviceHash); // o hash do dispositivo sai aos 30 dias para todos (regra que já existia)
        }
    }

    [SqlFact]
    public async Task Terminal_recente_fica_intacto_e_o_pedido_sem_usuario_nao_quebra()
    {
        await LimparAsync();
        var recente = await PedidoAsync(10, StatusPedido.Rejeitado, moderadoHaHoras: 240);
        var semUsuario = await PedidoAsync(40, StatusPedido.Rejeitado, moderadoHaHoras: 960, comUsuario: false);

        await ExecutarAsync();

        var r = await LerAsync(recente.Id);
        Assert.Equal("Maria Silva", r.NomeOuvinte);
        Assert.NotNull(r.ListenerDeviceHash);
        Assert.Equal("(anônimo)", (await LerAsync(semUsuario.Id)).NomeOuvinte);
    }

    [SqlFact]
    public async Task E_idempotente_a_segunda_rodada_nao_toca_em_nada()
    {
        await LimparAsync();
        await PedidoAsync(31, StatusPedido.Rejeitado, moderadoHaHoras: 744);
        await PedidoAsync(9);

        var primeira = await ExecutarAsync();
        var segunda = await ExecutarAsync();

        Assert.Equal(1, primeira.PedidosAnonimizados);
        Assert.Equal(2, primeira.IpsApagados); // o de 9 e o de 31 dias (o passo dos 7 dias vem antes do dos 30)
        Assert.Equal(0, segunda.PedidosAnonimizados);
        Assert.Equal(0, segunda.IpsApagados);
    }

    [SqlFact]
    public async Task Em_varios_lotes_termina_e_anonimiza_todos()
    {
        await LimparAsync();
        for (var i = 0; i < 12; i++) await PedidoAsync(35, StatusPedido.Rejeitado, moderadoHaHoras: 840);

        var r = await ExecutarAsync(lote: 5);

        Assert.Equal(12, r.PedidosAnonimizados);
        await using var db = banco.CriarContexto();
        Assert.Equal(12, await db.PedidosMusica.CountAsync(p => p.NomeOuvinte == "(anônimo)"));
    }

    [SqlFact]
    public async Task Evento_de_pedido_de_31_dias_e_apagado_entregue_ou_parado_e_o_de_29_dias_fica()
    {
        await LimparAsync();
        await using (var db = banco.CriarContexto())
        {
            var antigoEntregue = new EventoOutbox("PedidoCriado", "{}");
            var antigoParado = new EventoOutbox("PedidoModerado", "{}");
            var antigoTocado = new EventoOutbox("PedidoTocado", "{}");
            var recente = new EventoOutbox("PedidoCriado", "{}");
            var outroTipoAntigoParado = new EventoOutbox("TocandoAgora", "{}");
            db.EventosOutbox.AddRange(antigoEntregue, antigoParado, antigoTocado, recente, outroTipoAntigoParado);
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE infra.EventoOutbox SET CriadoEmUtc = DATEADD(DAY, -31, SYSUTCDATETIME()), ProcessadoEmUtc = DATEADD(DAY, -30, SYSUTCDATETIME()) WHERE Id = {antigoEntregue.Id}");
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE infra.EventoOutbox SET CriadoEmUtc = DATEADD(DAY, -31, SYSUTCDATETIME()) WHERE Id IN ({antigoParado.Id}, {antigoTocado.Id}, {outroTipoAntigoParado.Id})");
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE infra.EventoOutbox SET CriadoEmUtc = DATEADD(DAY, -29, SYSUTCDATETIME()) WHERE Id = {recente.Id}");
        }

        var r = await ExecutarAsync();

        Assert.Equal(3, r.EventosDePedidoApagados);
        await using var leitura = banco.CriarContexto();
        var tipos = await leitura.EventosOutbox.AsNoTracking().Select(e => e.Tipo).ToListAsync();
        Assert.Contains("PedidoCriado", tipos); // o de 29 dias
        Assert.Contains("TocandoAgora", tipos); // outro tipo, parado: a regra dos 7 dias para entregues não o apaga, e esta não é a dele
        Assert.Equal(2, tipos.Count);
    }
}
