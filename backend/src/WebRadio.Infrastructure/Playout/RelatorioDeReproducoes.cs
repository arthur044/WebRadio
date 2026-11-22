using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using WebRadio.Application.Features.Relatorios;
using WebRadio.Domain.Enums;
using WebRadio.Infrastructure.Persistencia;

namespace WebRadio.Infrastructure.Playout;

internal sealed class LinhaDoRelatorio
{
    public Guid MidiaId { get; set; }

    public string? Titulo { get; set; }

    public string? Artista { get; set; }

    public byte TipoMidia { get; set; }

    public int Reproducoes { get; set; }
}

/// <summary>Chama <c>rel.usp_MaisTocadas</c> (E4-A02). As entradas já foram validadas pela Application; a procedure valida de novo (<c>THROW</c>) e a falha dela sobe como erro de servidor.</summary>
public sealed class RelatorioDeReproducoes(RadioDbContext db) : IRelatorioDeReproducoes
{
    public async Task<IReadOnlyList<MidiaMaisTocadaDto>> MaisTocadasAsync(DateTime deUtc, DateTime ateUtc, int top, CancellationToken ct)
    {
        var linhas = await db.Database.SqlQueryRaw<LinhaDoRelatorio>("EXEC rel.usp_MaisTocadas @DeUtc, @AteUtc, @Top",
            new SqlParameter("@DeUtc", System.Data.SqlDbType.DateTime2) { Value = deUtc, Scale = 0 },
            new SqlParameter("@AteUtc", System.Data.SqlDbType.DateTime2) { Value = ateUtc, Scale = 0 },
            new SqlParameter("@Top", System.Data.SqlDbType.Int) { Value = top }).ToListAsync(ct);
        return [.. linhas.Select(l => new MidiaMaisTocadaDto(l.MidiaId, l.Titulo, l.Artista, (TipoMidia)l.TipoMidia, l.Reproducoes))];
    }
}
