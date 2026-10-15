using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WebRadio.Application.Features.Usuarios;
using WebRadio.Domain.Autenticacao;
using WebRadio.Domain.Enums;
using WebRadio.Domain.Grade;
using WebRadio.Infrastructure.Persistencia;
using WebRadio.Infrastructure.Persistencia.Outbox;
using WebRadio.Infrastructure.Tests.Suporte;
using Xunit;

namespace WebRadio.Infrastructure.Tests;

/// <summary>
/// O EF materializa <c>datetime2</c> como <c>Unspecified</c> e o System.Text.Json só escreve o sufixo Z para <c>Kind = Utc</c>: um DTO
/// com data lida do banco saía "2026-10-15T19:49:48", que o navegador lê como hora LOCAL. Tudo no banco é UTC (02 §4), então o
/// <see cref="RadioDbContext"/> lê TODO <c>DateTime</c> e <c>DateTime?</c> com <c>Kind = Utc</c> (conversor global, M1 do review do #40).
/// </summary>
public class KindUtcTests(BancoDeTeste banco) : IClassFixture<BancoDeTeste>
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly Cenario _c = new(banco);

    [Fact]
    public void Toda_propriedade_DateTime_ou_DateTime_nulavel_do_modelo_tem_o_conversor_para_UTC()
    {
        // Sem conexão: só o modelo. Uma entidade nova com DateTime sem o conversor voltaria a gerar JSON sem o Z.
        using var db = new RadioDbContext(new DbContextOptionsBuilder<RadioDbContext>().UseSqlServer("Server=nao-existe;Database=x").Options);

        var semConversor = db.Model.GetEntityTypes()
            .SelectMany(t => t.GetProperties().Where(p => p.ClrType == typeof(DateTime) || p.ClrType == typeof(DateTime?)))
            .Where(p => p.GetValueConverter() is null)
            .Select(p => $"{p.DeclaringType.DisplayName()}.{p.Name}")
            .ToList();

        Assert.Empty(semConversor);
    }

    [Fact]
    public void O_modelo_tem_propriedades_DateTime_para_o_teste_acima_nao_passar_no_vazio()
    {
        using var db = new RadioDbContext(new DbContextOptionsBuilder<RadioDbContext>().UseSqlServer("Server=nao-existe;Database=x").Options);

        Assert.True(db.Model.GetEntityTypes().SelectMany(t => t.GetProperties()).Count(p => p.ClrType == typeof(DateTime) || p.ClrType == typeof(DateTime?)) > 20);
    }

    [SqlFact]
    public async Task Entidades_lidas_do_banco_vem_com_Kind_Utc_inclusive_as_datas_anulaveis()
    {
        var locutor = await _c.CriarUsuarioAsync(Cenario.Email(), Role.Locutor);
        var agora = DateTime.UtcNow;
        var inicio = new DateTime(agora.Ticks - (agora.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc).AddDays(400);
        await using (var db = _c.NovoDb())
        {
            var programa = new Programa(Guid.CreateVersion7(), "Cancelado", null, locutor.Id, inicio, inicio.AddHours(1), agora);
            programa.Cancelar(agora); // CanceladoEmUtc: datetime2(3) anulável
            db.Programas.Add(programa);
            db.EventosOutbox.Add(new EventoOutbox("Teste", "{\"grupos\":null,\"dados\":{}}"));
            db.RefreshTokens.Add(new RefreshToken(Guid.CreateVersion7(), locutor.Id, new byte[32], Guid.NewGuid(), agora, TimeSpan.FromDays(7), null));
            await db.SaveChangesAsync();
        }

        await using var leitura = _c.NovoDb();
        var u = await leitura.Usuarios.AsNoTracking().SingleAsync(x => x.Id == locutor.Id);
        var p = await leitura.Programas.AsNoTracking().SingleAsync(x => x.InicioUtc == inicio);
        var e = await leitura.EventosOutbox.AsNoTracking().OrderByDescending(x => x.Id).FirstAsync();
        var r = await leitura.RefreshTokens.AsNoTracking().SingleAsync(x => x.UsuarioId == locutor.Id);

        Assert.Equal(DateTimeKind.Utc, u.CriadoEmUtc.Kind);
        Assert.Equal(DateTimeKind.Utc, u.AtualizadoEmUtc.Kind);
        Assert.Equal((DateTimeKind.Utc, DateTimeKind.Utc, DateTimeKind.Utc), (p.InicioUtc.Kind, p.FimUtc.Kind, p.CriadoEmUtc.Kind));
        Assert.Equal(DateTimeKind.Utc, p.CanceladoEmUtc!.Value.Kind); // o DateTime? também
        Assert.Equal(DateTimeKind.Utc, e.CriadoEmUtc.Kind);
        Assert.Equal((DateTimeKind.Utc, DateTimeKind.Utc), (r.CriadoEmUtc.Kind, r.ExpiraEmUtc.Kind));
        Assert.Null(e.ProcessadoEmUtc); // anulável sem valor continua nulo
    }

    [SqlFact]
    public async Task Filtrar_por_data_continua_traduzindo_igual_para_SQL()
    {
        var locutor = await _c.CriarUsuarioAsync(Cenario.Email(), Role.Locutor);
        var inicio = DateTime.SpecifyKind(DateTime.UtcNow.Date.AddDays(500).AddHours(7), DateTimeKind.Utc);
        await using (var db = _c.NovoDb())
        {
            db.Programas.Add(new Programa(Guid.CreateVersion7(), "Filtrado", null, locutor.Id, inicio, inicio.AddHours(1), DateTime.UtcNow));
            await db.SaveChangesAsync();
        }

        await using var leitura = _c.NovoDb();
        // Parâmetros Utc, Unspecified e Local: o conversor só age na leitura e não pode mudar o valor que vai para o SQL.
        foreach (var kind in new[] { DateTimeKind.Utc, DateTimeKind.Unspecified })
        {
            var de = DateTime.SpecifyKind(inicio.AddMinutes(-1), kind);
            var ate = DateTime.SpecifyKind(inicio.AddMinutes(1), kind);
            Assert.Single(await leitura.Programas.AsNoTracking().Where(p => p.InicioUtc > de && p.InicioUtc < ate).ToListAsync());
        }
    }

    [SqlFact]
    public async Task UsuarioAdminDto_sai_com_Z_na_lista_e_no_PATCH_como_ja_saia_no_POST()
    {
        var usuario = await _c.CriarUsuarioAsync(Cenario.Email(), Role.Locutor);
        await using var db = _c.NovoDb();

        var lista = await new ListarUsuariosHandler(db).Handle(new ListarUsuariosQuery(null, usuario.Email, 1, 20), default);
        var doBanco = lista.Itens.Single(i => i.Id == usuario.Id);

        var atual = await _c.NovoDb().Usuarios.AsNoTracking().SingleAsync(u => u.Id == usuario.Id);
        var patch = await _c.NovoAlterarUsuario().Handle(new AlterarUsuarioCommand(usuario.Id, Role.Admin, null, atual.RowVersion), default);

        foreach (var dto in new[] { doBanco, patch })
        {
            Assert.Equal(DateTimeKind.Utc, dto.CriadoEmUtc.Kind);
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(dto, Web));
            Assert.EndsWith("Z", json.RootElement.GetProperty("criadoEmUtc").GetString());
        }
    }
}
