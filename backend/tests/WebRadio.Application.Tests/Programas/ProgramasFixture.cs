using System.Text.Json;
using WebRadio.Application.Features.Programas;
using WebRadio.Application.Tests.Auth;
using WebRadio.Domain.Enums;
using WebRadio.Domain.Grade;
using WebRadio.Domain.Usuarios;
using WebRadio.Infrastructure.Persistencia;
using WebRadio.Infrastructure.Persistencia.Outbox;
using WebRadio.Infrastructure.Seguranca;
using static WebRadio.Application.Tests.Auth.AuthFixture;

namespace WebRadio.Application.Tests.Programas;

/// <summary>Banco em memória + relógio falso + os handlers de Programa montados como na Api (E2-F01).</summary>
public sealed class ProgramasFixture
{
    private static readonly SenhaHasher Hasher = new();

    public ProgramasFixture()
    {
        Db = NovoDb();
        Relogio = new RelogioFalso(Agora);
        Locutor = CriarUsuario(Db, Hasher, "locutor@example.com", role: Role.Locutor);
        OutroLocutor = CriarUsuario(Db, Hasher, "outro@example.com", role: Role.Locutor);
        Admin = CriarUsuario(Db, Hasher, "admin@example.com", role: Role.Admin);
    }

    public RadioDbContext Db { get; }

    public RelogioFalso Relogio { get; }

    public Usuario Locutor { get; }

    public Usuario OutroLocutor { get; }

    public Usuario Admin { get; }

    public CriarProgramaHandler Criar => new(Db, new TransacaoComTrava(Db), Relogio);

    public EditarProgramaHandler Editar => new(Db, new TransacaoComTrava(Db), Relogio);

    public CancelarProgramaHandler Cancelar => new(Db, new TransacaoComTrava(Db), new OutboxEf(Db), Relogio);

    public EncerrarProgramaHandler Encerrar => new(Db, new TransacaoComTrava(Db), new OutboxEf(Db), Relogio);

    public ListarProgramasHandler Listar => new(Db);

    public ProgramaAoVivoHandler AoVivo => new(Db);

    public ProgramaPorIdHandler PorId => new(Db);

    /// <summary>Tudo a partir de "agora" (2026-10-01 12:00Z): <c>Em(60, 120)</c> = de 13:00 a 14:00.</summary>
    public static (DateTime Inicio, DateTime Fim) Em(int inicioMin, int fimMin) => (Agora.AddMinutes(inicioMin), Agora.AddMinutes(fimMin));

    public async Task<ProgramaDto> CriarAsync(string titulo, int inicioMin, int fimMin, Guid? locutorId = null)
    {
        var (i, f) = Em(inicioMin, fimMin);
        return await Criar.Handle(new CriarProgramaCommand(titulo, null, locutorId ?? Locutor.Id, i, f), default);
    }

    /// <summary>Muda o status direto na entidade (o que o Worker faz em SQL), sem passar pelos handlers.</summary>
    public async Task ForcarAsync(Guid id, Action<Programa> acao)
    {
        var p = await Db.Programas.FindAsync(id) ?? throw new InvalidOperationException("programa inexistente");
        acao(p);
        await Db.SaveChangesAsync();
    }

    public List<(string Tipo, JsonElement Dados, bool ParaTodos)> Eventos()
    {
        var saida = new List<(string, JsonElement, bool)>();
        foreach (var e in Db.EventosOutbox.OrderBy(x => x.Id).ToList())
        {
            using var doc = JsonDocument.Parse(e.Payload);
            saida.Add((e.Tipo, doc.RootElement.GetProperty("dados").Clone(), doc.RootElement.GetProperty("grupos").ValueKind == JsonValueKind.Null));
        }

        return saida;
    }
}
