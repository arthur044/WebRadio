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

        /// <summary>Evento cujo envio nunca termina e ignora o cancelamento (destino pendurado).</summary>
        public string? EventoQuePendura { get; set; }

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
            if (evento == EventoQuePendura) await new TaskCompletionSource().Task;
            if (Segurar is { } segurar)
            {
                Entrou.TrySetResult();
                await segurar.Task;
            }

            lock (_trava) Envios.Add(new Envio(evento, grupos, JsonSerializer.SerializeToElement(carga)));
        }
    }

    private readonly RelogioFalso _relogio = new(new DateTime(2026, 10, 12, 15, 0, 0, DateTimeKind.Utc));

    private OutboxProcessor Novo(NotificadorFalso notificador, TimeSpan? tempoMaximoPorEvento = null) =>
        new(banco.CriarContexto(), notificador, _relogio, NullLogger<OutboxProcessor>.Instance)
        {
            TempoMaximoPorEvento = tempoMaximoPorEvento ?? TimeSpan.FromSeconds(30),
        };

    /// <summary>O banco é da classe inteira: cada teste começa sem pendências dos anteriores.</summary>
    private async Task<long[]> EnfileirarAsync(params (string Tipo, object Carga, string[] Grupos)[] eventos)
    {
        // Atalho dos testes: lista vazia aqui quer dizer "para todos"; o destino real continua explícito no IOutbox.
        await using var db = banco.CriarContexto();
        await db.EventosOutbox.Where(e => e.ProcessadoEmUtc == null).ExecuteUpdateAsync(s => s.SetProperty(e => e.ProcessadoEmUtc, DateTime.UtcNow));
        var outbox = new OutboxEf(db, new WebRadio.Infrastructure.Seguranca.SistemaRelogio());
        foreach (var (tipo, carga, grupos) in eventos)
        {
            if (tipo == EventosDoOutbox.UsuarioSessoesRevogadas) outbox.EnfileirarSessoesRevogadas(JsonSerializer.SerializeToElement(carga).GetProperty("usuarioId").GetGuid());
            else if (grupos.Length == 0) outbox.EnfileirarParaTodos(tipo, carga);
            else outbox.EnfileirarParaGrupos(grupos, tipo, carga);
        }

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

        // Backoff POR EVENTO (#25): o "Ruim" só volta à fila depois da espera própria; sem avançar o relógio o passe não o lê.
        Assert.Equal(new ResultadoDoLote(0, 0), await Novo(notificador).ProcessarLoteAsync(default));
        for (var i = 0; i < 8; i++)
        {
            _relogio.Avancar(EventoOutbox.EsperaMaximaEntreTentativas);
            Assert.Equal(new ResultadoDoLote(1, 1), await Novo(notificador).ProcessarLoteAsync(default));
        }

        // Décima tentativa: além de falhar, o evento sai da fila (e o processor registra OUTBOX_ESGOTADO).
        _relogio.Avancar(EventoOutbox.EsperaMaximaEntreTentativas);
        Assert.Equal(new ResultadoDoLote(1, 1, Parados: 1), await Novo(notificador).ProcessarLoteAsync(default));
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
        _relogio.Avancar(EventoOutbox.EsperaDepoisDaFalha(1)); // o backoff do evento venceu
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
            db.EventosOutbox.Add(new EventoOutbox("SemDados", """{"grupos":null}""", DateTime.UtcNow));
            db.EventosOutbox.Add(new EventoOutbox("DadosNaoObjeto", """{"grupos":null,"dados":[1,2]}""", DateTime.UtcNow));
            db.EventosOutbox.Add(new EventoOutbox("NaoEObjeto", """[1,2]""", DateTime.UtcNow));
            db.EventosOutbox.Add(new EventoOutbox(EventosDoOutbox.UsuarioSessoesRevogadas, """{"grupos":null,"dados":{"usuarioId":"nao-e-guid"}}""", DateTime.UtcNow));
            new OutboxEf(db, new WebRadio.Infrastructure.Seguranca.SistemaRelogio()).EnfileirarParaTodos("Bom", new { n = 1 });
            await db.SaveChangesAsync();
        }

        var notificador = new NotificadorFalso();

        // Não contam como falha a repetir (o dispatcher não desacelera por causa deles): param na hora.
        Assert.Equal(new ResultadoDoLote(5, 0, Parados: 4), await Novo(notificador).ProcessarLoteAsync(default));
        Assert.Equal(new ResultadoDoLote(0, 0), await Novo(notificador).ProcessarLoteAsync(default));
        Assert.Equal("Bom", Assert.Single(notificador.Envios).Evento);
        Assert.Empty(notificador.Desconectados);
    }

    [SqlTheory]
    [InlineData("""{"dados":{"pedidoId":1}}""", "sem a propriedade grupos")]
    [InlineData("""{"grupos":[],"dados":{"pedidoId":1}}""", "lista vazia")]
    [InlineData("""{"grupos":[""],"dados":{"pedidoId":1}}""", "nome em branco")]
    [InlineData("""{"grupos":["locutores",null],"dados":{"pedidoId":1}}""", "nome nulo")]
    [InlineData("""{"grupos":"locutores","dados":{"pedidoId":1}}""", "texto em vez de lista")]
    [InlineData("""{"grupos":[7],"dados":{"pedidoId":1}}""", "numero em vez de nome")]
    public async Task Destino_ausente_ou_vazio_e_FALHA_com_UltimoErro_e_nunca_vira_broadcast(string payload, string caso)
    {
        long id;
        await using (var db = banco.CriarContexto())
        {
            await db.EventosOutbox.Where(e => e.ProcessadoEmUtc == null).ExecuteUpdateAsync(s => s.SetProperty(e => e.ProcessadoEmUtc, DateTime.UtcNow));
            var evento = new EventoOutbox("PedidoCriado", payload, DateTime.UtcNow);
            db.EventosOutbox.Add(evento);
            await db.SaveChangesAsync();
            id = evento.Id;
        }

        var notificador = new NotificadorFalso();

        Assert.Equal(new ResultadoDoLote(1, 0, Parados: 1), await Novo(notificador).ProcessarLoteAsync(default));

        Assert.True(notificador.Envios.Count == 0, caso);
        var salvo = await LerAsync(id);
        Assert.Null(salvo.ProcessadoEmUtc);
        Assert.True(salvo.Parado);
        Assert.Contains("grupos", salvo.UltimoErro, StringComparison.Ordinal);
    }

    [SqlFact]
    public async Task Grupos_null_no_envelope_e_o_unico_jeito_de_publicar_para_todos()
    {
        await using (var db = banco.CriarContexto())
        {
            await db.EventosOutbox.Where(e => e.ProcessadoEmUtc == null).ExecuteUpdateAsync(s => s.SetProperty(e => e.ProcessadoEmUtc, DateTime.UtcNow));
            db.EventosOutbox.Add(new EventoOutbox("TocandoAgora", """{"grupos":null,"dados":{"titulo":"Faixa"}}""", DateTime.UtcNow));
            await db.SaveChangesAsync();
        }

        var notificador = new NotificadorFalso();
        await Novo(notificador).ProcessarLoteAsync(default);

        Assert.Empty(Assert.Single(notificador.Envios).Grupos); // NotificarTodos
    }

    [SqlFact]
    public async Task Entrega_pendurada_estoura_o_teto_por_evento_vira_falha_e_nao_segura_o_lote_nem_a_transacao()
    {
        var ids = await EnfileirarAsync(("Pendurado", new { n = 1 }, []), ("Bom", new { n = 2 }, []));
        var notificador = new NotificadorFalso { EventoQuePendura = "Pendurado" };
        var relogio = System.Diagnostics.Stopwatch.StartNew();

        var resultado = await Novo(notificador, TimeSpan.FromMilliseconds(300)).ProcessarLoteAsync(default).WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(new ResultadoDoLote(2, 1), resultado);
        Assert.True(relogio.Elapsed < TimeSpan.FromSeconds(10), $"o passe levou {relogio.Elapsed}");
        Assert.Equal("Bom", Assert.Single(notificador.Envios).Evento);
        var pendurado = await LerAsync(ids[0]);
        Assert.Null(pendurado.ProcessadoEmUtc);
        Assert.Equal(1, pendurado.Tentativas);
        Assert.StartsWith("Tempo esgotado", pendurado.UltimoErro, StringComparison.Ordinal);
        // A transação terminou (as travas de linha caíram): outro passe consegue ler o evento de novo (depois do backoff dele).
        _relogio.Avancar(EventoOutbox.EsperaDepoisDaFalha(1));
        Assert.Equal(1, (await Novo(new NotificadorFalso()).ProcessarLoteAsync(default)).Lidos);
    }

    [SqlFact]
    public async Task Desligamento_do_host_no_meio_da_entrega_cancela_o_passe_sem_contar_falha()
    {
        var ids = await EnfileirarAsync(("Pendurado", new { n = 1 }, []));
        var notificador = new NotificadorFalso { EventoQuePendura = "Pendurado" };
        using var desligando = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Novo(notificador).ProcessarLoteAsync(desligando.Token));

        var evento = await LerAsync(ids[0]);
        Assert.Equal(0, evento.Tentativas); // rollback: não foi culpa do evento
        Assert.Null(evento.ProcessadoEmUtc);
    }

    [Fact]
    public void O_teto_padrao_por_evento_e_de_5_segundos()
        => Assert.Equal(TimeSpan.FromSeconds(5), new OutboxProcessor(null!, null!, null!, null!).TempoMaximoPorEvento);

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

    [Fact]
    public void IOutbox_exige_destino_explicito_e_grava_o_envelope_da_spec()
    {
        // Nada é gravado: o IOutbox só adiciona ao contexto (quem salva é o handler), então nem precisa de servidor.
        using var db = new RadioDbContext(new DbContextOptionsBuilder<RadioDbContext>().UseSqlServer("Server=nao-usado").Options);
        var outbox = new OutboxEf(db, new WebRadio.Infrastructure.Seguranca.SistemaRelogio());
        var usuarioId = Guid.NewGuid();

        outbox.EnfileirarParaTodos("TocandoAgora", new { titulo = "Faixa" });
        outbox.EnfileirarParaGrupos([GruposDoHub.Locutores, "listener:ABC"], "PedidoModerado", new { pedidoId = 1 });
        outbox.EnfileirarSessoesRevogadas(usuarioId);

        var payloads = db.EventosOutbox.Local.Select(e => e.Payload).ToList();
        Assert.Equal("""{"grupos":null,"dados":{"titulo":"Faixa"}}""", payloads[0]);
        Assert.Equal("""{"grupos":["locutores","listener:ABC"],"dados":{"pedidoId":1}}""", payloads[1]);
        Assert.Equal("""{"grupos":null,"dados":{"usuarioId":"ID"}}""".Replace("ID", usuarioId.ToString(), StringComparison.Ordinal), payloads[2]);

        // Lista vazia ou nome em branco: erro na hora de enfileirar, nunca um broadcast por esquecimento.
        Assert.Throws<ArgumentException>(() => outbox.EnfileirarParaGrupos([], "PedidoCriado", new { pedidoId = 1 }));
        Assert.Throws<ArgumentException>(() => outbox.EnfileirarParaGrupos([GruposDoHub.Locutores, " "], "PedidoCriado", new { pedidoId = 1 }));
        // O tipo interno não pode ser enfileirado com destino de cliente, nem com usuário vazio.
        Assert.Throws<ArgumentException>(() => outbox.EnfileirarParaTodos(EventosDoOutbox.UsuarioSessoesRevogadas, new { usuarioId }));
        Assert.Throws<ArgumentException>(() => outbox.EnfileirarParaGrupos([GruposDoHub.Locutores], EventosDoOutbox.UsuarioSessoesRevogadas, new { usuarioId }));
        Assert.Throws<ArgumentException>(() => outbox.EnfileirarSessoesRevogadas(Guid.Empty));
        Assert.Equal(3, db.EventosOutbox.Local.Count);
    }

    [SqlFact]
    public async Task Backoff_por_evento_o_que_falhou_espera_o_proprio_prazo_e_dobra_enquanto_os_outros_seguem()
    {
        var ids = await EnfileirarAsync(("Ruim", new { n = 1 }, []), ("Bom", new { n = 2 }, []));
        var notificador = new NotificadorFalso { EventoQueFalha = "Ruim" };

        Assert.Equal(new ResultadoDoLote(2, 1), await Novo(notificador).ProcessarLoteAsync(default));
        var depoisDaPrimeira = await LerAsync(ids[0]);
        Assert.Equal(_relogio.UtcNow + EventoOutbox.EsperaDepoisDaFalha(1), depoisDaPrimeira.ProximaTentativaEmUtc);
        Assert.Equal(TimeSpan.FromSeconds(5), EventoOutbox.EsperaDepoisDaFalha(1));

        // Um evento NOVO entra no meio da espera do "Ruim": é entregue na hora, sem ser atrasado pelo backoff do outro.
        var novos = await EnfileirarNovoSemMarcarOsOutrosAsync("Bom2");
        Assert.Equal(new ResultadoDoLote(1, 0), await Novo(notificador).ProcessarLoteAsync(default));
        Assert.NotNull((await LerAsync(novos)).ProcessadoEmUtc);
        Assert.Equal(1, (await LerAsync(ids[0])).Tentativas); // o Ruim nem foi tentado de novo

        // Antes do prazo nada; no prazo ele volta, falha de novo e a espera DOBRA.
        _relogio.Avancar(TimeSpan.FromSeconds(4));
        Assert.Equal(new ResultadoDoLote(0, 0), await Novo(notificador).ProcessarLoteAsync(default));
        _relogio.Avancar(TimeSpan.FromSeconds(1));
        Assert.Equal(new ResultadoDoLote(1, 1), await Novo(notificador).ProcessarLoteAsync(default));
        var depoisDaSegunda = await LerAsync(ids[0]);
        Assert.Equal(2, depoisDaSegunda.Tentativas);
        Assert.Equal(_relogio.UtcNow + TimeSpan.FromSeconds(10), depoisDaSegunda.ProximaTentativaEmUtc);
    }

    private async Task<long> EnfileirarNovoSemMarcarOsOutrosAsync(string tipo)
    {
        await using var db = banco.CriarContexto();
        new OutboxEf(db, _relogio).EnfileirarParaTodos(tipo, new { n = 3 });
        await db.SaveChangesAsync();
        return await db.EventosOutbox.Where(e => e.Tipo == tipo).OrderByDescending(e => e.Id).Select(e => e.Id).FirstAsync();
    }

    [Theory]
    [InlineData(1, 5)]
    [InlineData(2, 10)]
    [InlineData(5, 80)]
    [InlineData(7, 300)] // 320 s, cortado no teto de 5 min
    [InlineData(9, 300)]
    public void Espera_depois_da_falha_dobra_a_partir_de_5_s_ate_o_teto_de_5_min(int falhas, int segundos)
        => Assert.Equal(TimeSpan.FromSeconds(segundos), EventoOutbox.EsperaDepoisDaFalha(falhas));

    [SqlFact]
    public async Task Evento_enfileirado_usa_o_instante_do_IClock_e_nao_o_relogio_do_sistema()
    {
        var relogio = new RelogioFalso(new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc));
        await using var db = banco.CriarContexto();
        new OutboxEf(db, relogio).EnfileirarParaTodos("RelogioDoOutbox", new { n = 1 });
        await db.SaveChangesAsync();

        var gravado = await db.EventosOutbox.AsNoTracking().Where(e => e.Tipo == "RelogioDoOutbox").OrderByDescending(e => e.Id).FirstAsync();
        Assert.Equal(relogio.UtcNow, gravado.CriadoEmUtc);
    }

    [SqlFact]
    public async Task Eventos_parados_ficam_fora_do_indice_de_pendentes_e_o_filtro_e_o_texto_literal_da_consulta()
    {
        await using var db = banco.CriarContexto();
        await db.EventosOutbox.Where(e => e.ProcessadoEmUtc == null).ExecuteUpdateAsync(s => s.SetProperty(e => e.ProcessadoEmUtc, DateTime.UtcNow));
        // 2 pendentes e 2 parados (10 tentativas, sem ProcessadoEmUtc): só os 2 pendentes cabem no índice filtrado.
        db.EventosOutbox.AddRange(
            new EventoOutbox("IdxPendente", "{\"grupos\":null,\"dados\":{}}", DateTime.UtcNow),
            new EventoOutbox("IdxPendente", "{\"grupos\":null,\"dados\":{}}", DateTime.UtcNow));
        var parado1 = new EventoOutbox("IdxParado", "{\"grupos\":null,\"dados\":{}}", DateTime.UtcNow);
        var parado2 = new EventoOutbox("IdxParado", "{\"grupos\":null,\"dados\":{}}", DateTime.UtcNow);
        parado1.Parar("erro de teste", DateTime.UtcNow);
        parado2.Parar("erro de teste", DateTime.UtcNow);
        db.EventosOutbox.AddRange(parado1, parado2);
        await db.SaveChangesAsync();

        var filtro = await db.Database.SqlQueryRaw<string>("SELECT filter_definition AS [Value] FROM sys.indexes WHERE name = N'IX_EventoOutbox_Pendentes' AND object_id = OBJECT_ID(N'infra.EventoOutbox')").SingleAsync();
        Assert.Contains("[ProcessadoEmUtc] IS NULL", filtro, StringComparison.Ordinal);
        Assert.Contains("[Tentativas]<(10)", filtro, StringComparison.Ordinal);
        var linhasNoIndice = await db.Database.SqlQueryRaw<long>("SELECT SUM(ps.row_count) AS [Value] FROM sys.dm_db_partition_stats ps JOIN sys.indexes i ON i.object_id = ps.object_id AND i.index_id = ps.index_id WHERE i.name = N'IX_EventoOutbox_Pendentes' AND i.object_id = OBJECT_ID(N'infra.EventoOutbox')").SingleAsync();
        Assert.Equal(2, linhasNoIndice);
        Assert.Equal(2, await db.EventosOutbox.CountAsync(e => e.Tipo == "IdxParado")); // continuam na tabela, para análise
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
        var evento = new EventoOutbox("X", "{}", DateTime.UtcNow);
        evento.RegistrarFalha(new string('e', 5000), DateTime.UtcNow);

        Assert.Equal(1000, evento.UltimoErro!.Length);
        Assert.Equal(1, evento.Tentativas);
    }
}
