using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using WebRadio.Application.Abstracoes;
using WebRadio.Application.Features.Programas;
using WebRadio.Domain.Enums;
using WebRadio.Domain.Grade;
using WebRadio.Infrastructure.Persistencia;
using WebRadio.Infrastructure.Persistencia.Outbox;
using WebRadio.Infrastructure.Tests.Suporte;
using WebRadio.Infrastructure.Trabalhos;
using Xunit;

namespace WebRadio.Infrastructure.Tests;

/// <summary>
/// E2-F03 contra SQL Server de verdade: o SQL do Fluxo A (passo 2) com o relógio do SQL, o envelope do outbox montado em SQL e a
/// entrega pelo OutboxProcessor real (o mesmo parser do envelope que a Api usa).
/// </summary>
public class TransicoesDaGradeTests(BancoDeTeste banco) : IClassFixture<BancoDeTeste>
{
    private sealed class NotificadorFalso : IRealtimeNotifier
    {
        public List<(string Evento, JsonObject Carga)> Todos { get; } = [];

        public Task NotificarTodosAsync(string evento, object? carga, CancellationToken ct = default)
        {
            Todos.Add((evento, (JsonObject)carga!));
            return Task.CompletedTask;
        }

        public Task NotificarGruposAsync(IReadOnlyList<string> grupos, string evento, object? carga, CancellationToken ct = default) =>
            throw new InvalidOperationException("ProgramaStatusAlterado vai para todos");

        public Task DesconectarUsuarioAsync(Guid usuarioId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private readonly Cenario _c = new(banco);

    private static DateTime Agora() => new(DateTime.UtcNow.Ticks - (DateTime.UtcNow.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc);

    private async Task<Guid> LocutorAsync(string nome = "Locutora Dona")
    {
        var usuario = await _c.CriarUsuarioAsync(Cenario.Email(), Role.Locutor);
        await using var db = _c.NovoDb();
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE seg.Usuario SET Nome = {nome} WHERE Id = {usuario.Id}");
        return usuario.Id;
    }

    /// <summary>Programa Agendado (ou AoVivo) com a janela dada, inserido como se tivesse sido criado em <c>inicio</c>.</summary>
    private async Task<Guid> ProgramaAsync(Guid locutor, DateTime inicio, DateTime fim, string titulo, bool aoVivo = false)
    {
        await using var db = _c.NovoDb();
        var p = new Programa(Guid.CreateVersion7(), titulo, null, locutor, inicio, fim, agora: inicio);
        if (aoVivo) p.IniciarAoVivo(inicio);
        db.Programas.Add(p);
        await db.SaveChangesAsync();
        return p.Id;
    }

    private async Task<StatusPrograma> StatusAsync(Guid id)
    {
        await using var db = _c.NovoDb();
        return (await db.Programas.AsNoTracking().SingleAsync(p => p.Id == id)).Status;
    }

    private async Task<List<EventoOutbox>> EventosAsync(params Guid[] programas)
    {
        await using var db = _c.NovoDb();
        var todos = await db.EventosOutbox.AsNoTracking().Where(e => e.Tipo == "ProgramaStatusAlterado").OrderBy(e => e.Id).ToListAsync();
        return [.. todos.Where(e => programas.Any(id => e.Payload.Contains(id.ToString())))];
    }

    private async Task<ResultadoDasTransicoes> RodarAsync()
    {
        await using var db = _c.NovoDb();
        return await new TransicoesDaGrade(db).ExecutarAsync(default);
    }

    [SqlFact]
    public async Task Programa_Agendado_com_a_janela_em_curso_vira_AoVivo_com_o_payload_completo()
    {
        var locutor = await LocutorAsync("Ana \"Aspas\" Açaí");
        var agora = Agora();
        var id = await ProgramaAsync(locutor, agora.AddMinutes(-10), agora.AddHours(1), "Manhã \"Fresca\" — ação");

        var r = await RodarAsync();

        Assert.True(r.Iniciados >= 1);
        Assert.Equal(StatusPrograma.AoVivo, await StatusAsync(id));
        var evento = Assert.Single(await EventosAsync(id));
        using var doc = JsonDocument.Parse(evento.Payload);
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("grupos").ValueKind); // envelope: todos
        var dados = doc.RootElement.GetProperty("dados");
        Assert.Equal(id, dados.GetProperty("programaId").GetGuid());
        Assert.Equal("Manhã \"Fresca\" — ação", dados.GetProperty("titulo").GetString());
        Assert.Equal("AoVivo", dados.GetProperty("status").GetString());
        Assert.Equal("Ana \"Aspas\" Açaí", dados.GetProperty("locutorNome").GetString());
    }

    [SqlFact]
    public async Task FimUtc_passado_vira_Finalizado_com_o_payload_completo_tanto_do_AoVivo_quanto_do_Agendado_com_janela_perdida()
    {
        var locutor = await LocutorAsync("Bia");
        var agora = Agora();
        var aoVivo = await ProgramaAsync(locutor, agora.AddHours(-3), agora.AddSeconds(-5), "Estava no ar", aoVivo: true);
        var perdido = await ProgramaAsync(locutor, agora.AddHours(-6), agora.AddHours(-5), "Janela perdida");

        await RodarAsync();

        Assert.Equal(StatusPrograma.Finalizado, await StatusAsync(aoVivo));
        Assert.Equal(StatusPrograma.Finalizado, await StatusAsync(perdido));
        foreach (var (id, titulo) in new[] { (aoVivo, "Estava no ar"), (perdido, "Janela perdida") })
        {
            var evento = Assert.Single(await EventosAsync(id));
            using var doc = JsonDocument.Parse(evento.Payload);
            var dados = doc.RootElement.GetProperty("dados");
            Assert.Equal(id, dados.GetProperty("programaId").GetGuid());
            Assert.Equal(titulo, dados.GetProperty("titulo").GetString());
            Assert.Equal("Finalizado", dados.GetProperty("status").GetString());
            Assert.Equal("Bia", dados.GetProperty("locutorNome").GetString());
        }
    }

    [SqlFact]
    public async Task Rodar_de_novo_nao_gera_evento_repetido()
    {
        var locutor = await LocutorAsync();
        var agora = Agora();
        var inicia = await ProgramaAsync(locutor, agora.AddMinutes(-10), agora.AddHours(1), "Inicia");
        var termina = await ProgramaAsync(locutor, agora.AddHours(-4), agora.AddSeconds(-3), "Termina", aoVivo: true);

        await RodarAsync();
        await RodarAsync();

        Assert.Single(await EventosAsync(inicia));
        Assert.Single(await EventosAsync(termina));
    }

    [SqlFact]
    public async Task Programa_futuro_Cancelado_e_Finalizado_nao_mudam_e_nao_geram_evento()
    {
        var locutor = await LocutorAsync();
        var agora = Agora();
        var futuro = await ProgramaAsync(locutor, agora.AddHours(2), agora.AddHours(3), "Futuro");
        var cancelado = await ProgramaAsync(locutor, agora.AddMinutes(-10), agora.AddHours(1), "Cancelado");
        await using (var db = _c.NovoDb())
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE grade.Programa SET Status = 4, CanceladoEmUtc = SYSUTCDATETIME() WHERE Id = {cancelado}");
        var finalizado = await ProgramaAsync(locutor, agora.AddHours(-2), agora.AddHours(-1), "Finalizado");
        await using (var db = _c.NovoDb())
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE grade.Programa SET Status = 3 WHERE Id = {finalizado}");

        await RodarAsync();

        Assert.Equal(StatusPrograma.Agendado, await StatusAsync(futuro));
        Assert.Equal(StatusPrograma.Cancelado, await StatusAsync(cancelado));
        Assert.Equal(StatusPrograma.Finalizado, await StatusAsync(finalizado));
        Assert.Empty(await EventosAsync(futuro, cancelado, finalizado));
    }

    [SqlFact]
    public async Task Encerrar_pelo_comando_e_depois_rodar_o_Worker_nao_gera_um_segundo_evento()
    {
        var locutor = await LocutorAsync();
        var agora = Agora();
        var id = await ProgramaAsync(locutor, agora.AddMinutes(-10), agora.AddHours(1), "Encerrado a mao");
        await using (var db = _c.NovoDb())
        {
            var encerrar = new EncerrarProgramaHandler(db, new TransacaoComTrava(db), new OutboxEf(db), _c.Relogio);
            await encerrar.Handle(new EncerrarProgramaCommand(id, locutor, SolicitanteEhAdmin: false), default);
        }

        await RodarAsync();

        Assert.Single(await EventosAsync(id)); // o do encerrar; o Worker não vê mais o programa (já é Finalizado)
        Assert.Equal(StatusPrograma.Finalizado, await StatusAsync(id));
    }

    [SqlFact]
    public async Task O_evento_gravado_em_SQL_passa_pelo_parser_do_envelope_do_OutboxProcessor()
    {
        var locutor = await LocutorAsync("Cris");
        var agora = Agora();
        var id = await ProgramaAsync(locutor, agora.AddMinutes(-1), agora.AddHours(1), "Pelo dispatcher");
        await RodarAsync();
        var notificador = new NotificadorFalso();

        await using (var db = _c.NovoDb())
            await new OutboxProcessor(db, notificador, _c.Relogio, NullLogger<OutboxProcessor>.Instance).ProcessarLoteAsync(default);

        var (evento, carga) = Assert.Single(notificador.Todos, t => t.Carga["programaId"]?.GetValue<Guid>() == id);
        Assert.Equal("ProgramaStatusAlterado", evento);
        Assert.Equal("AoVivo", carga["status"]!.GetValue<string>());
        Assert.Equal("Cris", carga["locutorNome"]!.GetValue<string>());
        Assert.Equal("Pelo dispatcher", carga["titulo"]!.GetValue<string>());
        Assert.NotNull(carga["eventoId"]);
    }

    [SqlFact]
    public async Task Dois_Workers_ao_mesmo_tempo_geram_um_evento_so_por_programa()
    {
        var locutor = await LocutorAsync();
        var agora = Agora();
        var id = await ProgramaAsync(locutor, agora.AddMinutes(-10), agora.AddHours(1), "Corrida");

        await Task.WhenAll(RodarAsync(), RodarAsync(), RodarAsync());

        Assert.Single(await EventosAsync(id));
    }
}
