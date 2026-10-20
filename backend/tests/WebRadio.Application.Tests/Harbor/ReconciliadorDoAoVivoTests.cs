using System.Text.Json;
using WebRadio.Application.Abstracoes;
using WebRadio.Application.Features.Harbor;
using WebRadio.Application.Tests.Auth;
using WebRadio.Application.Tests.Transmissao;
using WebRadio.Infrastructure.Persistencia;
using WebRadio.Infrastructure.Persistencia.Outbox;
using Xunit;

namespace WebRadio.Application.Tests.Harbor;

/// <summary>Guardian M1 (B2): o "no ar" que venceu sem Desconectado também vira TransmissaoAoVivoAlterada.</summary>
public class ReconciliadorDoAoVivoTests
{
    private static readonly DateTime Fim = AuthFixture.Agora.AddHours(1);

    private readonly RadioDbContext _db = AuthFixture.NovoDb();
    private readonly EstadoEmMemoria _estado = new() { Agora = AuthFixture.Agora };

    private ReconciliadorDoAoVivo Novo(IOutbox? outbox = null) =>
        new(_estado, outbox ?? new OutboxEf(_db), _db, new LogCapturado<ReconciliadorDoAoVivo>());

    private void NoAr() => _estado.Atual = new EstadoDoAoVivo(true, Guid.NewGuid(), "Ana", Guid.NewGuid(), AuthFixture.Agora, Fim);

    private List<JsonElement> Publicados() => _db.EventosOutbox.ToList()
        .Where(e => e.Tipo == EventosDoOutbox.TransmissaoAoVivoAlterada)
        .Select(e => JsonDocument.Parse(e.Payload).RootElement.GetProperty("dados").Clone()).ToList();

    [Fact]
    public async Task No_ar_vencido_sai_do_ar_e_publica_uma_vez()
    {
        NoAr();
        _estado.Agora = Fim.AddMinutes(1);

        Assert.True(await Novo().ExecutarAsync(default));

        Assert.False(_estado.Atual!.NoAr);
        var dados = Assert.Single(Publicados());
        Assert.False(dados.GetProperty("noAr").GetBoolean());
        Assert.Equal(JsonValueKind.Null, dados.GetProperty("programaId").ValueKind);
        Assert.False(await Novo().ExecutarAsync(default)); // a segunda passada não repete
        Assert.Single(Publicados());
    }

    [Fact]
    public async Task No_ar_dentro_do_fim_mais_um_minuto_nao_muda_nada()
    {
        NoAr();
        _estado.Agora = Fim.AddSeconds(59);

        Assert.False(await Novo().ExecutarAsync(default));

        Assert.True(_estado.Atual!.NoAr);
        Assert.Empty(Publicados());
    }

    [Fact]
    public async Task Sem_estado_ou_fora_do_ar_nao_publica()
    {
        Assert.False(await Novo().ExecutarAsync(default));
        _estado.Atual = EstadoDoAoVivo.ForaDoAr(AuthFixture.Agora);
        _estado.Agora = Fim.AddDays(1);

        Assert.False(await Novo().ExecutarAsync(default));
        Assert.Empty(Publicados());
    }

    [Fact]
    public async Task Se_o_evento_nao_grava_o_estado_vencido_volta_para_a_proxima_passada()
    {
        NoAr();
        var antes = _estado.Atual;
        _estado.Agora = Fim.AddMinutes(2);

        Assert.False(await Novo(new OutboxQueFalha()).ExecutarAsync(default));

        Assert.Equal(antes, _estado.Atual);
        Assert.True(await Novo().ExecutarAsync(default)); // a passada seguinte funciona
    }

    private sealed class OutboxQueFalha : IOutbox
    {
        public void EnfileirarParaTodos(string tipo, object carga) => throw new InvalidOperationException("banco fora (simulado)");
        public void EnfileirarParaGrupos(IReadOnlyCollection<string> grupos, string tipo, object carga) => throw new InvalidOperationException("banco fora (simulado)");
        public void EnfileirarSessoesRevogadas(Guid usuarioId) => throw new InvalidOperationException("banco fora (simulado)");
    }
}
