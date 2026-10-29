using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WebRadio.Application.Abstracoes;
using WebRadio.Application.Features.Pedidos;
using WebRadio.Domain.Enums;
using WebRadio.Domain.Interacao;
using WebRadio.Infrastructure.Seguranca;
using WebRadio.Infrastructure.Tests.Suporte;
using WebRadio.Infrastructure.Trabalhos;
using Xunit;

namespace WebRadio.Infrastructure.Tests;

/// <summary>
/// E3-F04 contra SQL Server de verdade (DoD 5): a expiração em lotes com o relógio do SQL e o <c>PedidoModerado { status: Expirado }</c>
/// no outbox, na mesma transação de cada lote, para <c>locutores</c> e <c>listener:{hash}</c>.
/// </summary>
public class ExpiracaoDePedidosSqlTests(BancoDeTeste banco) : IClassFixture<BancoDeTeste>
{
    private readonly Cenario _c = new(banco);

    private ListenerIdentity Identidade() => new(_c.Pepper, new IpHasher(_c.Pepper));

    private ExpiracaoDePedidos Expiracao(int lote = ExpiracaoDePedidos.LotePadrao, int minutos = 120) =>
        new(_c.NovoDb(), Options.Create(new PedidosOptions { ExpiracaoMinutos = minutos })) { Lote = lote };

    private async Task LimparAsync()
    {
        await using var db = _c.NovoDb();
        await db.Database.ExecuteSqlRawAsync("DELETE FROM interacao.PedidoMusica; DELETE FROM infra.EventoOutbox;");
    }

    private async Task<PedidoMusica> InserirAsync(int idadeEmMinutos, Guid? listenerId = null, bool semHash = false, bool aprovado = false)
    {
        var criado = DateTime.UtcNow.AddMinutes(-idadeEmMinutos);
        var hash = semHash ? null : Identidade().DeviceHash(listenerId ?? Guid.NewGuid());
        var p = new PedidoMusica(Guid.NewGuid(), null, null, "Maria", "Música", "Artista", null, hash, null, criado);
        if (aprovado) p.Aprovar((await _c.CriarUsuarioAsync(Cenario.Email())).Id, null, criado);
        await using var db = _c.NovoDb();
        db.PedidosMusica.Add(p);
        await db.SaveChangesAsync();
        return p;
    }

    private async Task<List<(string Tipo, JsonElement Dados, string[] Grupos)>> EventosAsync()
    {
        await using var db = _c.NovoDb();
        var saida = new List<(string, JsonElement, string[])>();
        foreach (var e in await db.EventosOutbox.OrderBy(x => x.Id).ToListAsync())
        {
            using var doc = JsonDocument.Parse(e.Payload);
            saida.Add((e.Tipo, doc.RootElement.GetProperty("dados").Clone(), [.. doc.RootElement.GetProperty("grupos").EnumerateArray().Select(g => g.GetString()!)]));
        }

        return saida;
    }

    private async Task<StatusPedido> StatusAsync(Guid id)
    {
        await using var db = _c.NovoDb();
        return await db.PedidosMusica.Where(p => p.Id == id).Select(p => p.Status).SingleAsync();
    }

    [SqlFact]
    public async Task DoD_5_pendente_de_121_minutos_vira_Expirado_e_o_outbox_ganha_o_PedidoModerado_para_o_ouvinte_e_os_locutores()
    {
        await LimparAsync();
        var ouvinte = Guid.NewGuid();
        var velho = await InserirAsync(121, ouvinte);

        var expirados = await Expiracao().ExecutarAsync(default);

        Assert.Equal(1, expirados);
        Assert.Equal(StatusPedido.Expirado, await StatusAsync(velho.Id));
        var (tipo, dados, grupos) = Assert.Single(await EventosAsync());
        Assert.Equal("PedidoModerado", tipo);
        Assert.Equal(velho.Id, dados.GetProperty("pedidoId").GetGuid());
        Assert.Equal("Expirado", dados.GetProperty("status").GetString());
        Assert.False(dados.TryGetProperty("motivoRejeicao", out _));
        Assert.Equal(["locutores", GruposDoHub.Listener(Identidade().DeviceHash(ouvinte))], grupos);
    }

    [SqlFact]
    public async Task O_grupo_do_ouvinte_e_o_mesmo_que_o_hub_usa_listener_e_hex_maiusculo_do_hash()
    {
        await LimparAsync();
        var ouvinte = Guid.NewGuid();
        await InserirAsync(130, ouvinte);

        await Expiracao().ExecutarAsync(default);

        var grupo = (await EventosAsync()).Single().Grupos[1];
        Assert.Equal(new ListenerIdentity(_c.Pepper, new IpHasher(_c.Pepper)).GrupoListener(ouvinte), grupo);
    }

    [SqlFact]
    public async Task Pedido_de_119_minutos_aprovado_e_ja_expirado_ficam_como_estao()
    {
        await LimparAsync();
        var novo = await InserirAsync(119);
        var aprovado = await InserirAsync(500, aprovado: true);

        Assert.Equal(0, await Expiracao().ExecutarAsync(default));

        Assert.Equal(StatusPedido.Pendente, await StatusAsync(novo.Id));
        Assert.Equal(StatusPedido.Aprovado, await StatusAsync(aprovado.Id));
        Assert.Empty(await EventosAsync());
    }

    [SqlFact]
    public async Task Pedido_anonimizado_sem_hash_expira_e_so_vai_para_locutores()
    {
        await LimparAsync();
        var p = await InserirAsync(200, semHash: true);

        await Expiracao().ExecutarAsync(default);

        Assert.Equal(StatusPedido.Expirado, await StatusAsync(p.Id));
        Assert.Equal(["locutores"], (await EventosAsync()).Single().Grupos);
    }

    [SqlFact]
    public async Task E_idempotente_rodar_duas_vezes_nao_gera_evento_repetido()
    {
        await LimparAsync();
        await InserirAsync(150);
        await InserirAsync(151);

        Assert.Equal(2, await Expiracao().ExecutarAsync(default));
        Assert.Equal(0, await Expiracao().ExecutarAsync(default));

        Assert.Equal(2, (await EventosAsync()).Count);
    }

    [SqlFact]
    public async Task Dois_workers_ao_mesmo_tempo_expiram_cada_pedido_uma_vez_so()
    {
        await LimparAsync();
        for (var i = 0; i < 30; i++) await InserirAsync(150);

        var totais = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Expiracao(lote: 7).ExecutarAsync(default)));

        Assert.Equal(30, totais.Sum());
        Assert.Equal(30, (await EventosAsync()).Count);
    }

    [SqlFact]
    public async Task Varios_lotes_repete_ate_o_lote_vir_menor_e_cada_lote_tem_os_proprios_eventos()
    {
        await LimparAsync();
        for (var i = 0; i < 25; i++) await InserirAsync(300);
        await InserirAsync(10); // recente: fica

        var expirados = await Expiracao(lote: 10).ExecutarAsync(default);

        Assert.Equal(25, expirados);
        Assert.Equal(25, (await EventosAsync()).Count);
        await using var db = _c.NovoDb();
        Assert.Equal(1, await db.PedidosMusica.CountAsync(p => p.Status == StatusPedido.Pendente));
        Assert.Equal(25, await db.PedidosMusica.CountAsync(p => p.Status == StatusPedido.Expirado));
    }

    [SqlFact]
    public async Task Lote_exato_confere_mais_uma_vez_e_termina()
    {
        await LimparAsync();
        for (var i = 0; i < 10; i++) await InserirAsync(300);

        Assert.Equal(10, await Expiracao(lote: 10).ExecutarAsync(default));
    }

    [SqlFact]
    public async Task O_prazo_e_configuravel()
    {
        await LimparAsync();
        var p = await InserirAsync(20);

        Assert.Equal(0, await Expiracao(minutos: 30).ExecutarAsync(default));
        Assert.Equal(1, await Expiracao(minutos: 10).ExecutarAsync(default));
        Assert.Equal(StatusPedido.Expirado, await StatusAsync(p.Id));
    }

    [Fact]
    public void Os_numeros_do_enum_que_o_SQL_e_o_indice_filtrado_usam_estao_fixos()
    {
        Assert.Equal(1, (int)StatusPedido.Pendente);
        Assert.Equal(2, (int)StatusPedido.Aprovado);
        Assert.Equal(5, (int)StatusPedido.Expirado);
    }
}
