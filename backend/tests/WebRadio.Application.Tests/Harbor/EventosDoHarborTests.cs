using System.Text.Json;
using WebRadio.Application.Abstracoes;
using WebRadio.Application.Features.Harbor;
using WebRadio.Application.Tests.Auth;
using WebRadio.Application.Tests.Transmissao;
using WebRadio.Domain.Enums;
using WebRadio.Domain.Grade;
using WebRadio.Infrastructure.Persistencia;
using WebRadio.Infrastructure.Persistencia.Outbox;
using Xunit;

namespace WebRadio.Application.Tests.Harbor;

public sealed class EstadoEmMemoria : IEstadoDoAoVivoStore
{
    public EstadoDoAoVivo? Atual { get; set; }
    public Task<EstadoDoAoVivo?> ObterAsync(CancellationToken ct) => Task.FromResult(Atual);
    public Task DefinirAsync(EstadoDoAoVivo estado, CancellationToken ct) { Atual = estado; return Task.CompletedTask; }

    public DateTime Agora { get; set; } = DateTime.UtcNow;

    public Task DesfazerExpiracaoAsync(ExpiracaoDoAoVivo troca, CancellationToken ct)
    {
        if (Atual == troca.Depois) Atual = troca.Antes;
        return Task.CompletedTask;
    }

    public Task<ExpiracaoDoAoVivo?> ExpirarVencidoAsync(CancellationToken ct)
    {
        if (Atual is not { } antes || antes.Vigente(Agora) == antes) return Task.FromResult<ExpiracaoDoAoVivo?>(null);
        Atual = EstadoDoAoVivo.ForaDoAr(antes.DesdeUtc);
        return Task.FromResult<ExpiracaoDoAoVivo?>(new ExpiracaoDoAoVivo(antes, Atual));
    }
}

/// <summary>E2-F06: POST /internal/harbor/eventos → estado do ao vivo + TransmissaoAoVivoAlterada pelo outbox.</summary>
public class EventosDoHarborTests
{
    private readonly RadioDbContext _db = AuthFixture.NovoDb();
    private readonly ConsultasFalsas _consultas = new();
    private readonly EstadoEmMemoria _estado = new();
    private readonly RelogioFalso _relogio = new(AuthFixture.Agora);
    private readonly Guid _id = Guid.NewGuid();
    private readonly Programa _programa;

    public EventosDoHarborTests()
    {
        var locutor = AuthFixture.CriarUsuario(_db, new HasherContador());
        _programa = new Programa(Guid.NewGuid(), "Manhã", null, locutor.Id, AuthFixture.Agora.AddMinutes(-10), AuthFixture.Agora.AddHours(1), AuthFixture.Agora.AddMinutes(-10));
        _db.Programas.Add(_programa);
        _db.SaveChanges();
        _consultas.Janelas[_id] = new JanelaDoHarbor(_programa.Id, _programa.FimUtc);
    }

    private Task Evento(string tipo, int segundos = 0, Guid? id = null) =>
        new RegistrarEventoDoHarborHandler(_consultas, _estado, new OutboxEf(_db), _db, new LogCapturado<RegistrarEventoDoHarborHandler>())
            .Handle(new RegistrarEventoDoHarborCommand(tipo, (id ?? _id).ToString(), AuthFixture.Agora.AddSeconds(segundos)), CancellationToken.None);

    private List<JsonElement> Publicados() => _db.EventosOutbox.OrderBy(e => e.Id).ToList()
        .Where(e => e.Tipo == EventosDoOutbox.TransmissaoAoVivoAlterada)
        .Select(e =>
        {
            using var doc = JsonDocument.Parse(e.Payload);
            Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("grupos").ValueKind); // para todos
            return doc.RootElement.GetProperty("dados").Clone();
        }).ToList();

    [Fact]
    public async Task Conectado_poe_no_ar_com_programa_e_locutor_e_publica()
    {
        await Evento("Conectado");

        Assert.Equal(new EstadoDoAoVivo(true, _programa.Id, "Ana", _id, AuthFixture.Agora, _programa.FimUtc), _estado.Atual);
        var dados = Assert.Single(Publicados());
        Assert.True(dados.GetProperty("noAr").GetBoolean());
        Assert.Equal(_programa.Id, dados.GetProperty("programaId").GetGuid());
        Assert.Equal("Ana", dados.GetProperty("locutorNome").GetString());
    }

    [Fact]
    public async Task Desconectado_tira_do_ar_e_publica()
    {
        await Evento("Conectado");
        await Evento("Desconectado", 30);

        Assert.False(_estado.Atual!.NoAr);
        var ultimo = Publicados()[^1];
        Assert.False(ultimo.GetProperty("noAr").GetBoolean());
        Assert.Equal(JsonValueKind.Null, ultimo.GetProperty("programaId").ValueKind);
    }

    [Fact]
    public async Task Evento_repetido_nao_muda_o_estado_nem_publica_de_novo()
    {
        await Evento("Conectado");
        await Evento("Conectado", 5);
        await Evento("Desconectado", 10);
        await Evento("Desconectado", 15);

        Assert.Equal(2, Publicados().Count);
        Assert.Equal(AuthFixture.Agora.AddSeconds(10), _estado.Atual!.DesdeUtc);
    }

    [Fact]
    public async Task Desconectado_sem_ninguem_no_ar_nao_publica()
    {
        await Evento("Desconectado");

        Assert.Empty(Publicados());
    }

    [Fact]
    public async Task Evento_atrasado_nao_desfaz_o_mais_novo()
    {
        await Evento("Conectado", 10);
        await Evento("Desconectado", 5); // chegou depois, mas aconteceu antes

        Assert.True(_estado.Atual!.NoAr);
        Assert.Single(Publicados());
    }

    [Fact]
    public async Task Desconexao_de_outro_identificador_nao_derruba_quem_esta_no_ar()
    {
        await Evento("Conectado");
        await Evento("Desconectado", 5, Guid.NewGuid());

        Assert.True(_estado.Atual!.NoAr);
    }

    [Fact]
    public async Task Conectado_fora_da_janela_e_ignorado()
    {
        _consultas.Janelas.Clear();

        await Evento("Conectado");

        Assert.Null(_estado.Atual);
        Assert.Empty(Publicados());
    }

    [Theory]
    [InlineData("Ligado", "3f2b1c4d-5e6f-4a7b-8c9d-0e1f2a3b4c5d", 0)]
    [InlineData("Conectado", "nao-e-guid", 0)]
    [InlineData("Conectado", "3F2B1C4D-5E6F-4A7B-8C9D-0E1F2A3B4C5D", 0)]
    [InlineData("Conectado", "3f2b1c4d-5e6f-4a7b-8c9d-0e1f2a3b4c5d", 60)]
    public void Validador_recusa_tipo_identificador_e_data_no_futuro(string tipo, string identificador, int minutosNoFuturo)
    {
        var v = new RegistrarEventoDoHarborValidator(_relogio);

        Assert.False(v.Validate(new RegistrarEventoDoHarborCommand(tipo, identificador, AuthFixture.Agora.AddMinutes(minutosNoFuturo))).IsValid);
    }

    [Fact]
    public void Validador_exige_ocorridoEmUtc()
    {
        Assert.False(new RegistrarEventoDoHarborValidator(_relogio).Validate(new RegistrarEventoDoHarborCommand("Conectado", _id.ToString(), null)).IsValid);
        Assert.True(new RegistrarEventoDoHarborValidator(_relogio).Validate(new RegistrarEventoDoHarborCommand("Desconectado", _id.ToString(), AuthFixture.Agora)).IsValid);
    }

    [Fact]
    public void Estado_no_ar_vence_um_minuto_depois_do_fim_previsto()
    {
        var estado = new EstadoDoAoVivo(true, _programa.Id, "Ana", _id, AuthFixture.Agora, AuthFixture.Agora.AddHours(1));

        Assert.True(estado.Vigente(AuthFixture.Agora.AddHours(1).AddSeconds(59)).NoAr);
        Assert.False(estado.Vigente(AuthFixture.Agora.AddHours(1).AddMinutes(1)).NoAr);
    }
}
