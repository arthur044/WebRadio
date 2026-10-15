using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using WebRadio.Application.Features.Programas;
using WebRadio.Domain.Enums;
using WebRadio.Domain.Erros;
using WebRadio.Infrastructure.Persistencia;
using WebRadio.Infrastructure.Persistencia.Outbox;
using WebRadio.Infrastructure.Tests.Suporte;
using Xunit;

namespace WebRadio.Infrastructure.Tests;

/// <summary>E2-F01 contra SQL Server de verdade: o sp_getapplock, os CHECKs, o RowVersion e o outbox na mesma transação.</summary>
public class ProgramasSqlTests(BancoDeTeste banco) : IClassFixture<BancoDeTeste>
{
    private static int _proximoDia;

    private readonly Cenario _c = new(banco);

    /// <summary>Um dia só para cada teste/rodada (o banco é compartilhado pela classe): programas de testes diferentes nunca se cruzam.</summary>
    private DateTime Dia() => Truncar(_c.Relogio.UtcNow.Date.AddDays(10 + Interlocked.Increment(ref _proximoDia)));

    private static DateTime Truncar(DateTime d) => new(d.Ticks - (d.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc);

    private CriarProgramaHandler Criar()
    {
        var db = _c.NovoDb();
        return new CriarProgramaHandler(db, new TransacaoComTrava(db), _c.Relogio);
    }

    private EditarProgramaHandler Editar()
    {
        var db = _c.NovoDb();
        return new EditarProgramaHandler(db, new TransacaoComTrava(db), _c.Relogio);
    }

    private EncerrarProgramaHandler Encerrar()
    {
        var db = _c.NovoDb();
        return new EncerrarProgramaHandler(db, new TransacaoComTrava(db), new OutboxEf(db), _c.Relogio);
    }

    private CancelarProgramaHandler Cancelar()
    {
        var db = _c.NovoDb();
        return new CancelarProgramaHandler(db, new TransacaoComTrava(db), new OutboxEf(db), _c.Relogio);
    }

    private async Task<Guid> LocutorAsync() => (await _c.CriarUsuarioAsync(Cenario.Email(), Role.Locutor)).Id;

    private Task<ProgramaDto> CriarAsync(Guid locutor, DateTime inicio, TimeSpan duracao, string titulo = "Programa") =>
        Criar().Handle(new CriarProgramaCommand(titulo, null, locutor, inicio, inicio + duracao), default);

    private async Task<int> EventosDoProgramaAsync(Guid programaId)
    {
        await using var db = _c.NovoDb();
        return await db.EventosOutbox.CountAsync(e => e.Tipo == "ProgramaStatusAlterado" && e.Payload.Contains(programaId.ToString()));
    }

    [SqlFact]
    public async Task Dois_POST_concorrentes_no_mesmo_horario_dao_um_201_e_um_409()
    {
        var locutor = await LocutorAsync();
        for (var rodada = 0; rodada < 5; rodada++)
        {
            var inicio = Dia().AddHours(10);
            var resultados = await Task.WhenAll(
                Tentar(() => CriarAsync(locutor, inicio, TimeSpan.FromHours(1), "A")),
                Tentar(() => CriarAsync(locutor, inicio, TimeSpan.FromHours(1), "B")));

            Assert.Equal(1, resultados.Count(r => r is ProgramaDto));
            var conflito = Assert.Single(resultados.OfType<ConflitoDeGradeException>());
            await using var db = _c.NovoDb();
            Assert.Equal(1, await db.Programas.CountAsync(p => p.InicioUtc == inicio));
            Assert.Equal((await db.Programas.SingleAsync(p => p.InicioUtc == inicio)).Id, conflito.ProgramaConflitanteId);
        }
    }

    [SqlFact]
    public async Task Dez_POST_concorrentes_com_horarios_que_se_cruzam_criam_exatamente_um()
    {
        var locutor = await LocutorAsync();
        var inicio = Dia().AddHours(8);

        // Cada um começa 5 min depois do anterior e dura 1 h: todos se cruzam com todos.
        var resultados = await Task.WhenAll(Enumerable.Range(0, 10).Select(i =>
            Tentar(() => CriarAsync(locutor, inicio.AddMinutes(i * 5), TimeSpan.FromHours(1), $"P{i}"))));

        Assert.Equal(1, resultados.Count(r => r is ProgramaDto));
        Assert.Equal(9, resultados.Count(r => r is ConflitoDeGradeException));
    }

    [SqlFact]
    public async Task Intervalos_encostados_nao_conflitam_no_SQL()
    {
        var locutor = await LocutorAsync();
        var inicio = Dia().AddHours(12);

        await CriarAsync(locutor, inicio, TimeSpan.FromHours(1), "A");
        await CriarAsync(locutor, inicio.AddHours(1), TimeSpan.FromHours(1), "B"); // [a, b) e [b, c)
        await CriarAsync(locutor, inicio.AddHours(-1), TimeSpan.FromHours(1), "C");

        await using var db = _c.NovoDb();
        Assert.Equal(3, await db.Programas.CountAsync(p => p.InicioUtc >= inicio.AddHours(-1) && p.InicioUtc <= inicio.AddHours(1)));
    }

    [SqlFact]
    public async Task Programa_de_12_horas_que_comeca_antes_da_faixa_ainda_conflita_no_SQL()
    {
        var locutor = await LocutorAsync();
        var inicio = Dia();
        var longo = await CriarAsync(locutor, inicio, TimeSpan.FromHours(12), "Maratona");

        var e = await Assert.ThrowsAsync<ConflitoDeGradeException>(() => CriarAsync(locutor, inicio.AddHours(11), TimeSpan.FromHours(1)));

        Assert.Equal(longo.Id, e.ProgramaConflitanteId);
    }

    [SqlFact]
    public async Task Editar_exclui_o_proprio_id_e_o_RowVersion_do_SQL_protege_de_edicao_concorrente()
    {
        var locutor = await LocutorAsync();
        var inicio = Dia().AddHours(6);
        var p = await CriarAsync(locutor, inicio, TimeSpan.FromHours(2));
        Assert.False(string.IsNullOrEmpty(p.Versao));

        var editado = await Editar().Handle(new EditarProgramaCommand(p.Id, "Novo", "d", locutor, inicio.AddMinutes(30), inicio.AddHours(3),
            Convert.FromBase64String(p.Versao)), default);

        Assert.Equal("Novo", editado.Titulo);
        Assert.NotEqual(p.Versao, editado.Versao); // o SQL gerou outro RowVersion
        await Assert.ThrowsAsync<VersaoDesatualizadaException>(() => Editar().Handle(new EditarProgramaCommand(p.Id, "Velho", null, locutor,
            inicio, inicio.AddHours(1), Convert.FromBase64String(p.Versao)), default));
    }

    [SqlFact]
    public async Task Encerrar_um_Agendado_com_a_janela_em_curso_passa_pelos_CHECKs_e_grava_um_evento()
    {
        var locutor = await LocutorAsync();
        var p = await CriarAsync(locutor, Dia().AddHours(3), TimeSpan.FromHours(1));
        var agora = Truncar(_c.Relogio.UtcNow);
        // Programa Agendado cuja janela já começou pelo relógio do handler: o Worker nunca o iniciou (parado).
        await using (var db = _c.NovoDb())
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE grade.Programa SET InicioUtc = {agora.AddMinutes(-10)}, FimUtc = {agora.AddHours(1)} WHERE Id = {p.Id}");

        var dto = await Encerrar().Handle(new EncerrarProgramaCommand(p.Id, locutor, SolicitanteEhAdmin: false), default);

        Assert.Equal("Finalizado", dto.Status);
        Assert.Equal(agora, dto.FimUtc);
        Assert.Equal(1, await EventosDoProgramaAsync(p.Id));
        await using var leitura = _c.NovoDb();
        Assert.Equal(StatusPrograma.Finalizado, (await leitura.Programas.AsNoTracking().SingleAsync(x => x.Id == p.Id)).Status);
    }

    [SqlFact]
    public async Task Encerrar_no_segundo_exato_do_inicio_respeita_o_CK_Programa_Intervalo()
    {
        var locutor = await LocutorAsync();
        var p = await CriarAsync(locutor, Dia().AddHours(20), TimeSpan.FromHours(2));
        var inicio = Truncar(_c.Relogio.UtcNow);
        await using (var db = _c.NovoDb())
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE grade.Programa SET InicioUtc = {inicio}, FimUtc = {inicio.AddHours(2)} WHERE Id = {p.Id}");
        _c.Relogio.Avancar(inicio - _c.Relogio.UtcNow); // agora == InicioUtc, no segundo exato

        var dto = await Encerrar().Handle(new EncerrarProgramaCommand(p.Id, locutor, false), default);

        Assert.Equal(inicio.AddSeconds(1), dto.FimUtc); // Fim = Início + 1 s; com Fim = Início o CK_Programa_Intervalo (Fim > Início) falharia
    }

    [SqlFact]
    public async Task Cancelar_grava_um_evento_na_mesma_transacao_e_cancelar_de_novo_nao_grava_outro()
    {
        var locutor = await LocutorAsync();
        var p = await CriarAsync(locutor, Dia().AddHours(5), TimeSpan.FromHours(1));

        await Cancelar().Handle(new CancelarProgramaCommand(p.Id), default);
        await Assert.ThrowsAsync<TransicaoInvalidaException>(() => Cancelar().Handle(new CancelarProgramaCommand(p.Id), default));

        Assert.Equal(1, await EventosDoProgramaAsync(p.Id));
        await using var db = _c.NovoDb();
        var salvo = await db.Programas.AsNoTracking().SingleAsync(x => x.Id == p.Id);
        Assert.Equal(StatusPrograma.Cancelado, salvo.Status);
        Assert.NotNull(salvo.CanceladoEmUtc); // CK_Programa_Cancelado
    }

    [SqlFact]
    public async Task Listar_ao_vivo_e_por_id_funcionam_no_SQL()
    {
        var locutor = await LocutorAsync();
        var inicio = Dia().AddHours(15);
        var p = await CriarAsync(locutor, inicio, TimeSpan.FromHours(1), "Listado");
        await using var db = _c.NovoDb();

        var lista = await new ListarProgramasHandler(db).Handle(new ListarProgramasQuery(inicio.AddMinutes(-5), inicio.AddMinutes(5), false), default);
        var porId = await new ProgramaPorIdHandler(db).Handle(new ProgramaPorIdQuery(p.Id), default);

        Assert.Equal(p.Id, Assert.Single(lista).Id);
        Assert.Equal("Listado", porId.Titulo);
        // O EF lê datetime2 como Unspecified: sem o Kind=Utc o JSON sairia sem o "Z" e o navegador leria como hora local.
        Assert.All(new[] { lista[0], porId }, d => Assert.Equal((DateTimeKind.Utc, DateTimeKind.Utc), (d.InicioUtc.Kind, d.FimUtc.Kind)));
        Assert.Contains("\"inicioUtc\":\"" + inicio.ToString("yyyy-MM-ddTHH:mm:ss") + "Z\"", System.Text.Json.JsonSerializer.Serialize(porId, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)));
        Assert.False(string.IsNullOrEmpty(porId.Versao));
        var aoVivo = await new ProgramaAoVivoHandler(db).Handle(new ProgramaAoVivoQuery(), default);
        Assert.NotEqual(p.Id, aoVivo?.Id); // Agendado não é "ao vivo": quem muda o status é o Worker
    }

    // ---- L2: Worker x encerrar/cancelar (RowVersion + ComUmaRepeticaoAsync) ----

    /// <summary>Roda <paramref name="acao"/> UMA vez, no primeiro SaveChanges: o momento entre a leitura do handler e o UPDATE dele.</summary>
    private sealed class CorridaNoPrimeiroSave(Func<Task> acao) : SaveChangesInterceptor
    {
        private int _feito;

        public int Disparos => Volatile.Read(ref _feito);

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _feito, 1) == 0) await acao();
            return result;
        }
    }

    private RadioDbContext DbComCorrida(CorridaNoPrimeiroSave corrida) =>
        new(new DbContextOptionsBuilder<RadioDbContext>().UseSqlServer(banco.ConnectionString).AddInterceptors(corrida).Options);

    /// <summary>Programa Agendado cuja janela já começou pelo relógio do handler (o Worker ainda não o iniciou).</summary>
    private async Task<(Guid Locutor, Guid Id)> AgendadoEmCursoAsync()
    {
        var locutor = await LocutorAsync();
        var p = await CriarAsync(locutor, Dia().AddHours(2), TimeSpan.FromHours(1));
        var agora = Truncar(_c.Relogio.UtcNow);
        await using var db = _c.NovoDb();
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE grade.Programa SET InicioUtc = {agora.AddMinutes(-5)}, FimUtc = {agora.AddHours(1)} WHERE Id = {p.Id}");
        return (locutor, p.Id);
    }

    private async Task WorkerIniciaAsync(Guid id)
    {
        await using var db = _c.NovoDb();
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE grade.Programa SET Status = {(byte)StatusPrograma.AoVivo}, AtualizadoEmUtc = SYSUTCDATETIME() WHERE Id = {id}");
    }

    [SqlFact]
    public async Task Encerrar_quando_o_Worker_muda_a_linha_no_meio_repete_lendo_o_status_novo_e_grava_um_evento_so()
    {
        var (locutor, id) = await AgendadoEmCursoAsync();
        var corrida = new CorridaNoPrimeiroSave(() => WorkerIniciaAsync(id)); // o Worker passa o programa para AoVivo entre a leitura e o UPDATE
        await using var db = DbComCorrida(corrida);
        var handler = new EncerrarProgramaHandler(db, new TransacaoComTrava(db), new OutboxEf(db), _c.Relogio);

        var dto = await handler.Handle(new EncerrarProgramaCommand(id, locutor, SolicitanteEhAdmin: false), default);

        Assert.Equal(1, corrida.Disparos);
        Assert.Equal("Finalizado", dto.Status);
        Assert.Equal(1, await EventosDoProgramaAsync(id)); // o da 1ª tentativa (revertida) não ficou
    }

    [SqlFact]
    public async Task Cancelar_quando_o_Worker_inicia_o_programa_no_meio_vira_409_sem_evento()
    {
        var (_, id) = await AgendadoEmCursoAsync();
        var corrida = new CorridaNoPrimeiroSave(() => WorkerIniciaAsync(id));
        await using var db = DbComCorrida(corrida);
        var handler = new CancelarProgramaHandler(db, new TransacaoComTrava(db), new OutboxEf(db), _c.Relogio);

        // A repetição relê o status (AoVivo): só Agendado se cancela.
        await Assert.ThrowsAsync<TransicaoInvalidaException>(() => handler.Handle(new CancelarProgramaCommand(id), default));

        Assert.Equal(1, corrida.Disparos);
        Assert.Equal(0, await EventosDoProgramaAsync(id));
        await using var leitura = _c.NovoDb();
        Assert.Equal(StatusPrograma.AoVivo, (await leitura.Programas.AsNoTracking().SingleAsync(p => p.Id == id)).Status);
    }

    private static async Task<object> Tentar(Func<Task<ProgramaDto>> acao)
    {
        try
        {
            return await acao();
        }
        catch (ConflitoDeGradeException e)
        {
            return e;
        }
    }
}
