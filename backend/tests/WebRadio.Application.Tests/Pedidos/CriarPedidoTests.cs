using WebRadio.Application.Abstracoes;
using WebRadio.Application.Features.Pedidos;
using WebRadio.Domain.Enums;
using WebRadio.Domain.Erros;
using Xunit;
using static WebRadio.Application.Tests.Auth.AuthFixture;
using static WebRadio.Application.Tests.Pedidos.PedidosFixture;

namespace WebRadio.Application.Tests.Pedidos;

/// <summary>07 E3-F01: validação (E3-D7), gravação com hash e programa da janela, outbox e fallback SQL.</summary>
public class CriarPedidoTests
{
    private readonly PedidosFixture _f = new();
    private readonly CriarPedidoValidator _v = new();

    [Fact]
    public async Task Grava_Pendente_com_os_hashes_o_programa_da_janela_e_nenhum_dado_em_claro()
    {
        var programa = Guid.NewGuid();
        _f.Consultas.Programa = programa;
        var ouvinte = Guid.NewGuid();

        var dto = await _f.Criar.Handle(Comando(ouvinte, ip: "203.0.113.7"), default);

        var p = Assert.Single(_f.Db.PedidosMusica);
        Assert.Equal(dto.Id, p.Id);
        Assert.Equal(StatusPedido.Pendente, p.Status);
        Assert.Equal(programa, p.ProgramaId);
        Assert.Equal(_f.Identidade.DeviceHash(ouvinte), p.ListenerDeviceHash);
        Assert.Equal(_f.Identidade.IpHash(System.Net.IPAddress.Parse("203.0.113.7")), p.ListenerIpHash);
        Assert.Equal(Agora, p.CriadoEmUtc);
        Assert.Null(p.UsuarioId);
        Assert.Equal("Pendente", dto.Status);
        Assert.Equal(programa, dto.ProgramaId);
    }

    [Fact]
    public async Task Sem_programa_na_janela_o_ProgramaId_fica_nulo()
    {
        var dto = await _f.Criar.Handle(Comando(), default);

        Assert.Null(dto.ProgramaId);
    }

    [Fact]
    public async Task Com_Bearer_grava_o_UsuarioId()
    {
        var usuario = Guid.NewGuid();

        await _f.Criar.Handle(Comando(usuarioId: usuario), default);

        Assert.Equal(usuario, Assert.Single(_f.Db.PedidosMusica).UsuarioId);
    }

    [Fact]
    public async Task Normaliza_para_NFC_apara_as_pontas_e_a_dedicatoria_vazia_vira_nula()
    {
        var dto = await _f.Criar.Handle(Comando(nome: "  José  ", titulo: " Café ", artista: " Artista ", mensagem: ""), default);

        Assert.Equal("José", dto.NomeOuvinte);
        Assert.Equal("Café", dto.TituloMusica);
        Assert.Equal("Artista", dto.Artista);
        Assert.Null(dto.Mensagem);
    }

    [Fact]
    public async Task Grava_o_PedidoCriado_no_outbox_so_para_locutores_com_o_PedidoDto()
    {
        var dto = await _f.Criar.Handle(Comando(), default);

        var (tipo, dados, grupos) = Assert.Single(_f.Eventos());
        Assert.Equal("PedidoCriado", tipo);
        Assert.Equal(["locutores"], grupos!);
        Assert.Equal(dto.Id, dados.GetProperty("id").GetGuid());
        Assert.Equal("Maria", dados.GetProperty("nomeOuvinte").GetString());
        Assert.Equal("Pendente", dados.GetProperty("status").GetString());
        // O PedidoDto nunca leva hash, IP nem usuário.
        Assert.False(dados.TryGetProperty("listenerDeviceHash", out _));
        Assert.False(dados.TryGetProperty("usuarioId", out _));
    }

    [Fact]
    public async Task Com_o_Redis_bom_nao_consulta_nem_trava_o_fallback_SQL()
    {
        await _f.Criar.Handle(Comando(), default);

        Assert.Equal(0, _f.Consultas.ConsultasDoFallback);
        Assert.Empty(_f.Transacao.Recursos);
    }

    [Fact]
    public async Task Com_o_Redis_fora_aplica_o_fallback_na_transacao_com_a_trava_do_dispositivo()
    {
        _f.Estado.UsarFallbackSql = true;
        var ouvinte = Guid.NewGuid();

        await _f.Criar.Handle(Comando(ouvinte), default);

        Assert.Equal(1, _f.Consultas.ConsultasDoFallback);
        Assert.Equal(Travas.PedidoDoDispositivo(_f.Identidade.DeviceHash(ouvinte)), Assert.Single(_f.Transacao.Recursos));
        Assert.Single(_f.Db.PedidosMusica);
    }

    [Fact]
    public async Task O_recurso_da_trava_e_derivado_do_hash_e_nao_do_listenerId()
    {
        _f.Estado.UsarFallbackSql = true;
        var ouvinte = Guid.NewGuid();

        await _f.Criar.Handle(Comando(ouvinte), default);

        var recurso = Assert.Single(_f.Transacao.Recursos);
        Assert.DoesNotContain(ouvinte.ToString("N"), recurso, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("pedido:dev:", recurso);
    }

    [Fact]
    public async Task Fallback_com_regra_ocupada_responde_429_com_a_maior_espera_e_nao_grava()
    {
        _f.Estado.UsarFallbackSql = true;
        _f.Consultas.Espera = new EsperaDoThrottle(TimeSpan.FromSeconds(100), null, TimeSpan.FromSeconds(250));

        var erro = await Assert.ThrowsAsync<MuitosPedidosException>(() => _f.Criar.Handle(Comando(), default));

        Assert.Equal(TimeSpan.FromSeconds(250), erro.TenteDeNovoEm);
        Assert.Empty(_f.Db.PedidosMusica);
        Assert.Empty(_f.Db.EventosOutbox);
    }

    [Fact]
    public async Task Trava_ocupada_alem_de_2_segundos_responde_429_com_Retry_After_de_1_segundo()
    {
        _f.Estado.UsarFallbackSql = true;
        _f.Transacao.Ocupada = true;

        var erro = await Assert.ThrowsAsync<MuitosPedidosException>(() => _f.Criar.Handle(Comando(), default));

        Assert.Equal(TimeSpan.FromSeconds(1), erro.TenteDeNovoEm);
        Assert.Empty(_f.Db.PedidosMusica);
    }

    [Fact]
    public void Comando_valido_passa_na_validacao()
    {
        Assert.True(_v.Validate(Comando()).IsValid);
        Assert.True(_v.Validate(Comando(mensagem: null)).IsValid);
        Assert.True(_v.Validate(Comando(mensagem: "")).IsValid);
    }

    [Fact]
    public void ListenerId_zerado_e_400_no_campo_X_Listener_Id()
    {
        var erro = Assert.Single(_v.Validate(Comando() with { ListenerId = Guid.Empty }).Errors);

        Assert.Equal("X-Listener-Id", erro.PropertyName);
    }

    [Fact]
    public void Nome_com_60_emoji_vale_120_unidades_UTF16_e_e_recusado_com_400_nunca_500()
    {
        var resultado = _v.Validate(Comando(nome: string.Concat(Enumerable.Repeat("\U0001F600", 60))));

        Assert.False(resultado.IsValid);
        Assert.Contains(resultado.Errors, e => e.PropertyName == "NomeOuvinte");
    }

    [Fact]
    public void Nome_com_30_emoji_cabe_nos_60()
    {
        Assert.True(_v.Validate(Comando(nome: string.Concat(Enumerable.Repeat("\U0001F600", 30)))).IsValid);
    }

    [Theory]
    [InlineData("nomeOuvinte")]
    [InlineData("tituloMusica")]
    [InlineData("artista")]
    public void Campos_obrigatorios_vazios_ou_so_com_espacos_sao_recusados(string campo)
    {
        foreach (var vazio in new[] { "", "   ", "ㅤ" })
        {
            var c = campo switch
            {
                "nomeOuvinte" => Comando(nome: vazio),
                "tituloMusica" => Comando(titulo: vazio),
                _ => Comando(artista: vazio),
            };
            Assert.False(_v.Validate(c).IsValid, $"{campo} = '{vazio}'");
        }
    }

    [Theory]
    [InlineData("a\nb")]
    [InlineData("a‮b")]
    [InlineData("a​b")]
    public void Texto_com_quebra_de_linha_ou_caractere_de_direcao_e_recusado_inclusive_na_dedicatoria(string texto)
    {
        Assert.False(_v.Validate(Comando(mensagem: texto)).IsValid);
        Assert.False(_v.Validate(Comando(nome: texto)).IsValid);
    }

    [Fact]
    public void Dedicatoria_so_com_espacos_e_erro_e_acima_de_280_tambem()
    {
        Assert.False(_v.Validate(Comando(mensagem: "   ")).IsValid);
        Assert.True(_v.Validate(Comando(mensagem: new string('a', 280))).IsValid);
        Assert.False(_v.Validate(Comando(mensagem: new string('a', 281))).IsValid);
    }

    [Fact]
    public void A_mensagem_de_erro_nao_repete_o_valor_enviado()
    {
        var resultado = _v.Validate(Comando(nome: "Fulano‮de Tal", mensagem: "SEGREDO\nlinha"));

        Assert.All(resultado.Errors, e =>
        {
            Assert.DoesNotContain("Fulano", e.ErrorMessage);
            Assert.DoesNotContain("SEGREDO", e.ErrorMessage);
        });
    }

    [Fact]
    public void O_ToString_do_comando_nao_imprime_IP_listenerId_nem_texto()
    {
        var ouvinte = Guid.NewGuid();
        var texto = Comando(ouvinte, ip: "203.0.113.7", nome: "Maria Silva").ToString();

        Assert.DoesNotContain("203.0.113.7", texto);
        Assert.DoesNotContain(ouvinte.ToString(), texto);
        Assert.DoesNotContain("Maria", texto);
    }
}
