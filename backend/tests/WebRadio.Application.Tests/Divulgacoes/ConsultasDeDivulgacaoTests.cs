using WebRadio.Application.Features.Divulgacoes;
using Xunit;
using static WebRadio.Application.Tests.Auth.AuthFixture;

namespace WebRadio.Application.Tests.Divulgacoes;

/// <summary>07 E3-F05 / DoD 8: só divulgações ativas (janela e Ativo), por prioridade e depois Id.</summary>
public class ConsultasDeDivulgacaoTests
{
    private readonly DivulgacoesFixture _f = new();

    private async Task<string[]> TitulosAtivasAsync() => [.. (await _f.Ativas.Handle(new DivulgacoesAtivasQuery(), default)).Select(d => d.Titulo)];

    [Fact]
    public async Task Ativas_ordenam_por_prioridade_decrescente_e_desempatam_por_Id()
    {
        await _f.CriarAsync("Baixa", prioridade: 10);
        await _f.CriarAsync("Alta", prioridade: 90);
        var m1 = await _f.CriarAsync("Media-1", prioridade: 50);
        var m2 = await _f.CriarAsync("Media-2", prioridade: 50);

        var titulos = await TitulosAtivasAsync();

        Assert.Equal("Alta", titulos[0]);
        Assert.Equal("Baixa", titulos[^1]);
        // Entre as de mesma prioridade a ordem é a do Id (determinística): Guid v7 cresce com o tempo, então Media-1 vem antes.
        Assert.Equal(m1.Id.CompareTo(m2.Id) < 0 ? ["Media-1", "Media-2"] : ["Media-2", "Media-1"], titulos[1..3]);
    }

    [Fact]
    public async Task A_janela_usa_o_relogio_do_servidor_inicio_inclusivo_e_fim_exclusivo()
    {
        await _f.CriarAsync("Sempre");
        await _f.CriarAsync("Começa em 1 h", inicio: Agora.AddHours(1));
        await _f.CriarAsync("Acabou", fim: Agora.AddMinutes(-1));
        await _f.CriarAsync("Dentro", inicio: Agora.AddMinutes(-5), fim: Agora.AddMinutes(5));
        await _f.CriarAsync("Começa agora", inicio: Agora);
        await _f.CriarAsync("Acaba agora", inicio: Agora.AddHours(-1), fim: Agora);

        Assert.Equal(["Começa agora", "Dentro", "Sempre"], (await TitulosAtivasAsync()).Order());

        _f.Relogio.UtcNow = Agora.AddHours(1);
        Assert.Contains("Começa em 1 h", await TitulosAtivasAsync());
    }

    [Fact]
    public async Task Inativas_ficam_de_fora_das_ativas_mas_aparecem_na_lista_do_Admin()
    {
        await _f.CriarAsync("Ativa");
        await _f.CriarAsync("Inativa", ativo: false);

        Assert.Equal(["Ativa"], await TitulosAtivasAsync());
        var pagina = await _f.Listar.Handle(new ListarDivulgacoesQuery(1, 20), default);
        Assert.Equal(2, pagina.Total);
        Assert.Contains(pagina.Itens, d => !d.Ativo);
    }

    [Fact]
    public async Task Lista_do_Admin_pagina()
    {
        for (var i = 0; i < 5; i++) await _f.CriarAsync($"D{i}", prioridade: (byte)(50 + i));

        var segunda = await _f.Listar.Handle(new ListarDivulgacoesQuery(2, 2), default);

        Assert.Equal(["D2", "D1"], segunda.Itens.Select(d => d.Titulo));
        Assert.Equal(5, segunda.Total);
    }

    [Theory]
    [InlineData(0, 20, false)]
    [InlineData(1, 0, false)]
    [InlineData(1, 100, true)]
    [InlineData(1, 101, false)]
    public void Pagina_e_tamanho_tem_limites(int pagina, int tamanho, bool valido) =>
        Assert.Equal(valido, new ListarDivulgacoesValidator().Validate(new ListarDivulgacoesQuery(pagina, tamanho)).IsValid);

    [Fact]
    public async Task O_DTO_das_ativas_nao_expoe_nada_alem_do_contrato_publico()
    {
        await _f.CriarAsync("Pública");

        var json = System.Text.Json.JsonSerializer.Serialize(await _f.Ativas.Handle(new DivulgacoesAtivasQuery(), default));

        Assert.DoesNotContain("criadoPor", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("versao", json, StringComparison.OrdinalIgnoreCase);
    }
}
