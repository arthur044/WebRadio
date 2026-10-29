using WebRadio.Application.Features.Pedidos;
using WebRadio.Domain.Enums;
using Xunit;
using static WebRadio.Application.Tests.Auth.AuthFixture;

namespace WebRadio.Application.Tests.Pedidos;

/// <summary>07 E3-F02: "meus pedidos" (dispositivo ou usuário) e a fila de moderação (Pendente e Aprovado das últimas 24 h).</summary>
public class ConsultasDePedidoTests
{
    private readonly PedidosFixture _f = new();

    [Fact]
    public async Task Meus_pedidos_devolve_os_ultimos_20_do_dispositivo_do_mais_novo_para_o_mais_antigo()
    {
        var ouvinte = Guid.NewGuid();
        for (var i = 0; i < 25; i++) await _f.InserirAsync(ouvinte, idadeEmMinutos: i * 10, nome: $"P{i}");

        var lista = await _f.Meus.Handle(new MeusPedidosQuery(ouvinte, null), default);

        Assert.Equal(20, lista.Count);
        Assert.Equal("P0", lista[0].NomeOuvinte);
        Assert.Equal("P19", lista[^1].NomeOuvinte);
    }

    [Fact]
    public async Task Um_ouvinte_nao_ve_pedido_de_outro_dispositivo_nem_pedido_anonimizado()
    {
        var a = Guid.NewGuid();
        await _f.InserirAsync(a, nome: "Da A");
        await _f.InserirAsync(Guid.NewGuid(), nome: "Da B");
        await _f.InserirAsync(semHash: true, nome: "Sem hash");

        var lista = await _f.Meus.Handle(new MeusPedidosQuery(a, null), default);

        Assert.Equal("Da A", Assert.Single(lista).NomeOuvinte);
    }

    [Fact]
    public async Task Com_Bearer_a_consulta_e_pelo_usuario_e_nao_pelo_dispositivo()
    {
        var usuario = Guid.NewGuid();
        var ouvinte = Guid.NewGuid();
        await _f.Criar.Handle(PedidosFixture.Comando(ouvinte, usuarioId: usuario), default);
        await _f.InserirAsync(ouvinte, nome: "Anônimo do mesmo aparelho");

        var lista = await _f.Meus.Handle(new MeusPedidosQuery(ouvinte, usuario), default);

        Assert.Equal("Maria", Assert.Single(lista).NomeOuvinte);
    }

    [Fact]
    public void Meus_pedidos_exige_X_Listener_Id_so_quando_nao_ha_usuario()
    {
        var v = new MeusPedidosValidator();
        Assert.False(v.Validate(new MeusPedidosQuery(Guid.Empty, null)).IsValid);
        Assert.True(v.Validate(new MeusPedidosQuery(Guid.Empty, Guid.NewGuid())).IsValid);
        Assert.True(v.Validate(new MeusPedidosQuery(Guid.NewGuid(), null)).IsValid);
    }

    [Fact]
    public async Task O_DTO_dos_meus_pedidos_nao_expoe_hash_nem_usuario()
    {
        var ouvinte = Guid.NewGuid();
        await _f.InserirAsync(ouvinte);

        var lista = await _f.Meus.Handle(new MeusPedidosQuery(ouvinte, null), default);

        var json = System.Text.Json.JsonSerializer.Serialize(lista);
        Assert.DoesNotContain("hash", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("usuarioId", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Fila_de_pendentes_ordena_do_mais_antigo_para_o_mais_novo_com_desempate_e_ignora_os_outros_status()
    {
        await _f.InserirAsync(idadeEmMinutos: 5, nome: "Novo");
        await _f.InserirAsync(idadeEmMinutos: 50, nome: "Velho");
        await _f.InserirAsync(idadeEmMinutos: 30, nome: "Médio");
        await _f.InserirAsync(idadeEmMinutos: 40, nome: "Aprovado", status: StatusPedido.Aprovado);

        var pagina = await _f.Listar.Handle(new ListarPedidosQuery(null, 1, 20), default);

        Assert.Equal(["Velho", "Médio", "Novo"], pagina.Itens.Select(p => p.NomeOuvinte));
        Assert.Equal(3, pagina.Total);
        Assert.All(pagina.Itens, p => Assert.Equal("Pendente", p.Status));
    }

    [Fact]
    public async Task Fila_pagina_e_devolve_o_total()
    {
        for (var i = 0; i < 5; i++) await _f.InserirAsync(idadeEmMinutos: 100 - i, nome: $"P{i}");

        var segunda = await _f.Listar.Handle(new ListarPedidosQuery("Pendente", 2, 2), default);

        Assert.Equal(["P2", "P3"], segunda.Itens.Select(p => p.NomeOuvinte));
        Assert.Equal(5, segunda.Total);
        Assert.Equal(2, segunda.Pagina);
        Assert.Equal(2, segunda.Tamanho);
    }

    [Fact]
    public async Task Aprovados_so_das_ultimas_24_horas_pela_data_da_moderacao()
    {
        await _f.InserirAsync(status: StatusPedido.Aprovado, moderadoHaMinutos: 60, nome: "Recente");
        await _f.InserirAsync(status: StatusPedido.Aprovado, moderadoHaMinutos: 24 * 60 + 1, nome: "Velho");
        await _f.InserirAsync(status: StatusPedido.Aprovado, moderadoHaMinutos: 120, nome: "Mais antigo");
        await _f.InserirAsync(nome: "Pendente");
        await _f.InserirAsync(status: StatusPedido.Tocado, moderadoHaMinutos: 10, nome: "Tocado");

        var pagina = await _f.Listar.Handle(new ListarPedidosQuery("Aprovado", 1, 20), default);

        Assert.Equal(["Mais antigo", "Recente"], pagina.Itens.Select(p => p.NomeOuvinte));
        Assert.All(pagina.Itens, p =>
        {
            Assert.Equal("Aprovado", p.Status);
            Assert.NotNull(p.ModeradoEmUtc);
        });
        Assert.Equal(2, pagina.Total);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("Pendente", true)]
    [InlineData("aprovado", true)]
    [InlineData("Rejeitado", false)]
    [InlineData("Tocado", false)]
    [InlineData("Expirado", false)]
    [InlineData("qualquer", false)]
    public void Status_so_Pendente_ou_Aprovado(string? status, bool valido) =>
        Assert.Equal(valido, new ListarPedidosValidator().Validate(new ListarPedidosQuery(status, 1, 20)).IsValid);

    [Theory]
    [InlineData(0, 20, false)]
    [InlineData(1, 0, false)]
    [InlineData(1, 100, true)]
    [InlineData(1, 101, false)]
    public void Pagina_e_tamanho_tem_limites(int pagina, int tamanho, bool valido) =>
        Assert.Equal(valido, new ListarPedidosValidator().Validate(new ListarPedidosQuery(null, pagina, tamanho)).IsValid);
}
