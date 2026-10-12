using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using WebRadio.Application.Abstracoes;
using WebRadio.Infrastructure.Persistencia;
using WebRadio.Infrastructure.Persistencia.Outbox;
using WebRadio.Infrastructure.Tests.Suporte;
using Xunit;

namespace WebRadio.Infrastructure.Tests;

/// <summary>
/// E1-F10 contra SQL Server de verdade (UPDLOCK/READPAST não existem no provedor em memória). O hub é um notificador
/// falso; a entrega até um cliente SignalR real é testada em WebRadio.Api.IntegrationTests.
/// </summary>
public class OutboxProcessorTests(BancoDeTeste banco) : IClassFixture<BancoDeTeste>
{
    private sealed record Envio(string Evento, IReadOnlyList<string> Grupos, JsonElement Carga);

    private sealed class NotificadorFalso : IRealtimeNotifier
    {
        private readonly object _trava = new();

        public List<Envio> Envios { get; } = [];
        public List<Guid> Desconectados { get; } = [];

        /// <summary>Evento cujo envio falha sempre (simula o backplane fora só para ele).</summary>
        public string? EventoQueFalha { get; set; }

        /// <summary>Quando definido, o primeiro envio avisa <see cref="Entrou"/> e espera esta tarefa antes de concluir.</summary>
        public TaskCompletionSource? Segurar { get; set; }
        public TaskCompletionSource Entrou { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task NotificarTodosAsync(string evento, object? carga, CancellationToken ct = default) => Registrar(evento, [], carga);

        public Task NotificarGruposAsync(IReadOnlyList<string> grupos, string evento, object? carga, CancellationToken ct = default) => Registrar(evento, grupos, carga);

        public Task DesconectarUsuarioAsync(Guid usuarioId, CancellationToken ct = default)
        {
            lock (_trava) Desconectados.Add(usuarioId);
            return Task.CompletedTask;
        }

        private async Task Registrar(string evento, IReadOnlyList<string> grupos, object? carga)
        {
            if (evento == EventoQueFalha) throw new InvalidOperationException("backplane fora do ar (simulado)");
            if (Segurar is { } segurar)
            {
                Entrou.TrySetResult();
                await segurar.Task;
            }

            lock (_trava) Envios.Add(new Envio(evento, grupos, JsonSerializer.SerializeToElement(carga)));
        }
    }

    private readonly RelogioFalso _relogio = new(new DateTime(2026, 10, 12, 15, 0, 0, DateTimeKind.Utc));

    private OutboxProcessor Novo(NotificadorFalso notificador) =>
        new(banco.CriarContexto(), notificador, _relogio, NullLogger<OutboxProcessor>.Instance);

    /// <summary>O banco é da classe inteira: cada teste começa sem pendências dos anteriores.</summary>
    private async Task<long[]> EnfileirarAsync(params (string Tipo, object Carga, string[] Grupos)[] eventos)
    {
        await using var db = banco.CriarContexto();
        await db.EventosOutbox.Where(e => e.ProcessadoEmUtc == null).ExecuteUpdateAsync(s => s.SetProperty(e => e.ProcessadoEmUtc, DateTime.UtcNow));
        var outbox = new OutboxEf(db);
        foreach (var (tipo, carga, grupos) in eventos) outbox.Enfileirar(tipo, carga, grupos);
        await db.SaveChangesAsync();
        return await db.EventosOutbox.Where(e => e.ProcessadoEmUtc == null).OrderBy(e => e.Id).Select(e => e.Id).ToArrayAsync();
    }

    private async Task<EventoOutbox> LerAsync(long id)
    {
        await using var db = banco.CriarContexto();
        return await db.EventosOutbox.AsNoTracking().SingleAsync(e => e.Id == id);
    }

    [SqlFact]
    public async Task Evento_pendente_e_entregue_com_eventoId_e_ocorridoEmUtc_fica_processado_e_reprocessar_nao_reenvia()
    {
        var ids = await EnfileirarAsync((EventosDoOutbox.TocandoAgora, new { titulo = "Faixa", artista = "Banda" }, []));
        var notificador = new NotificadorFalso();

        var primeiro = await Novo(notificador).ProcessarLoteAsync(default);

        Assert.Equal(new ResultadoDoLote(1, 0), primeiro);
        var envio = Assert.Single(notificador.Envios);
        Assert.Equal(EventosDoOutbox.TocandoAgora, envio.Evento);
        Assert.Empty(envio.Grupos);
        Assert.Equal("Faixa", envio.Carga.GetProperty("titulo").GetString());
        Assert.Equal(ids[0].ToString(), envio.Carga.GetProperty("eventoId").GetString());
        Assert.Equal(DateTimeKind.Utc, envio.Carga.GetProperty("ocorridoEmUtc").GetDateTime().Kind);
        var salvo = await LerAsync(ids[0]);
        Assert.Equal(_relogio.UtcNow, salvo.ProcessadoEmUtc);
        Assert.Equal(0, salvo.Tentativas);

        Assert.Equal(new ResultadoDoLote(0, 0), await Novo(notificador).ProcessarLoteAsync(default));
        Assert.Single(notificador.Envios);
    }

    [SqlFact]
    public async Task Destino_por_grupos_ordem_de_criacao_e_evento_interno_de_sessoes_revogadas()
    {
        var usuarioId = Guid.NewGuid();
        await EnfileirarAsync(
            ("PedidoCriado", new { pedidoId = 1 }, [GruposDoHub.Locutores]),
            (EventosDoOutbox.UsuarioSessoesRevogadas, new { usuarioId }, []),
            ("PedidoModerado", new { pedidoId = 1, status = "Aprovado" }, ["listener:ABC", GruposDoHub.Locutores]));
        var notificador = new NotificadorFalso();

        Assert.Equal(new ResultadoDoLote(3, 0), await Novo(notificador).ProcessarLoteAsync(default));

        Assert.Equal(["PedidoCriado", "PedidoModerado"], notificador.Envios.Select(e => e.Evento));
        Assert.Equal([GruposDoHub.Locutores], notificador.Envios[0].Grupos);
        Assert.Equal(["listener:ABC", GruposDoHub.Locutores], notificador.Envios[1].Grupos);
        Assert.Equal("Aprovado", notificador.Envios[1].Carga.GetProperty("status").GetString());
        // O evento interno derruba as conexões e NÃO é enviado a cliente nenhum.
        Assert.Equal([usuarioId], notificador.Desconectados);
    }

    [SqlFact]
    public async Task Falha_de_um_evento_nao_segura_os_outros_conta_tentativa_e_para_depois_de_dez()
    {
        var ids = await EnfileirarAsync(("Ruim", new { n = 1 }, []), ("Bom", new { n = 2 }, []));
        var notificador = new NotificadorFalso { EventoQueFalha = "Ruim" };

        Assert.Equal(new ResultadoDoLote(2, 1), await Novo(notificador).ProcessarLoteAsync(default));

        Assert.Equal("Bom", Assert.Single(notificador.Envios).Evento);
        var ruim = await LerAsync(ids[0]);
        Assert.Null(ruim.ProcessadoEmUtc);
        Assert.Equal(1, ruim.Tentativas);
        Assert.Contains("backplane fora do ar", ruim.UltimoErro, StringComparison.Ordinal);
        Assert.NotNull((await LerAsync(ids[1])).ProcessadoEmUtc);

        for (var i = 0; i < 9; i++) Assert.Equal(new ResultadoDoLote(1, 1), await Novo(notificador).ProcessarLoteAsync(default));
        Assert.Equal(EventoOutbox.MaximoDeTentativas, (await LerAsync(ids[0])).Tentativas);

        // Dez tentativas: parado para análise, fora da fila mesmo que o destino volte.
        notificador.EventoQueFalha = null;
        Assert.Equal(new ResultadoDoLote(0, 0), await Novo(notificador).ProcessarLoteAsync(default));
        Assert.Single(notificador.Envios);
    }

    [SqlFact]
    public async Task Evento_que_volta_a_funcionar_e_entregue_na_tentativa_seguinte()
    {
        var ids = await EnfileirarAsync(("Instavel", new { n = 1 }, []));
        var notificador = new NotificadorFalso { EventoQueFalha = "Instavel" };
        await Novo(notificador).ProcessarLoteAsync(default);

        notificador.EventoQueFalha = null;
        Assert.Equal(new ResultadoDoLote(1, 0), await Novo(notificador).ProcessarLoteAsync(default));

        var salvo = await LerAsync(ids[0]);
        Assert.NotNull(salvo.ProcessadoEmUtc);
        Assert.Equal(1, salvo.Tentativas); // o histórico da falha fica
    }

    [SqlFact]
    public async Task Payload_fora_do_formato_do_envelope_vira_falha_e_nao_derruba_o_lote()
    {
        await using (var db = banco.CriarContexto())
        {
            await db.EventosOutbox.Where(e => e.ProcessadoEmUtc == null).ExecuteUpdateAsync(s => s.SetProperty(e => e.ProcessadoEmUtc, DateTime.UtcNow));
            db.EventosOutbox.Add(new EventoOutbox("SemDados", """{"grupos":[]}"""));
            db.EventosOutbox.Add(new EventoOutbox("DadosNaoObjeto", """{"dados":[1,2]}"""));
            db.EventosOutbox.Add(new EventoOutbox(EventosDoOutbox.UsuarioSessoesRevogadas, """{"dados":{"usuarioId":"nao-e-guid"}}"""));
            new OutboxEf(db).Enfileirar("Bom", new { n = 1 });
            await db.SaveChangesAsync();
        }

        var notificador = new NotificadorFalso();

        Assert.Equal(new ResultadoDoLote(4, 3), await Novo(notificador).ProcessarLoteAsync(default));
        Assert.Equal("Bom", Assert.Single(notificador.Envios).Evento);
        Assert.Empty(notificador.Desconectados);
    }

    [SqlFact]
    public async Task Lote_tem_no_maximo_50_e_o_passe_seguinte_pega_o_resto()
    {
        await EnfileirarAsync(Enumerable.Range(0, OutboxProcessor.TamanhoDoLote + 5).Select(i => ("E", (object)new { i }, Array.Empty<string>())).ToArray());
        var notificador = new NotificadorFalso();

        Assert.Equal(OutboxProcessor.TamanhoDoLote, (await Novo(notificador).ProcessarLoteAsync(default)).Lidos);
        Assert.Equal(5, (await Novo(notificador).ProcessarLoteAsync(default)).Lidos);

        Assert.Equal(Enumerable.Range(0, 55), notificador.Envios.Select(e => e.Carga.GetProperty("i").GetInt32()));
    }

    [SqlFact]
    public async Task Duas_replicas_ao_mesmo_tempo_a_segunda_pula_as_linhas_travadas_sem_esperar_e_nada_sai_duas_vezes()
    {
        await EnfileirarAsync(("A", new { n = 1 }, []), ("B", new { n = 2 }, []), ("C", new { n = 3 }, []));
        var lenta = new NotificadorFalso { Segurar = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var rapida = new NotificadorFalso();

        // A réplica 1 trava o lote e fica presa no envio do primeiro evento, com a transação aberta.
        var passeLento = Novo(lenta).ProcessarLoteAsync(default);
        await lenta.Entrou.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // A réplica 2 não bloqueia nem repete: READPAST pula o que a outra segurou.
        var passeRapido = await Novo(rapida).ProcessarLoteAsync(default).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(new ResultadoDoLote(0, 0), passeRapido);

        lenta.Segurar.SetResult();
        Assert.Equal(new ResultadoDoLote(3, 0), await passeLento.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal(["A", "B", "C"], lenta.Envios.Select(e => e.Evento));
        Assert.Empty(rapida.Envios);
    }

    [Theory]
    [InlineData(1, false, 1)]
    [InlineData(1, true, 2)]
    [InlineData(32, true, 60)]
    [InlineData(60, true, 60)]
    [InlineData(60, false, 1)]
    public void Espera_do_dispatcher_dobra_na_falha_ate_um_minuto_e_volta_a_um_segundo(int atual, bool falhou, int esperado)
        => Assert.Equal(TimeSpan.FromSeconds(esperado), OutboxDispatcher.ProximaEspera(TimeSpan.FromSeconds(atual), falhou));

    [Fact]
    public void Erro_gigante_e_truncado_em_1000_caracteres()
    {
        var evento = new EventoOutbox("X", "{}");
        evento.RegistrarFalha(new string('e', 5000));

        Assert.Equal(1000, evento.UltimoErro!.Length);
        Assert.Equal(1, evento.Tentativas);
    }
}
