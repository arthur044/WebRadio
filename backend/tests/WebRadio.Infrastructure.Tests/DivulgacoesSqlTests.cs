using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using WebRadio.Application.Abstracoes;
using WebRadio.Application.Features.Divulgacoes;
using WebRadio.Domain.Enums;
using WebRadio.Domain.Erros;
using WebRadio.Infrastructure.Tests.Suporte;
using Xunit;

namespace WebRadio.Infrastructure.Tests;

/// <summary>
/// E3-F05 contra SQL Server de verdade: o texto SQL das ativas (Ativo como constante, só colunas do INCLUDE do índice), o
/// <c>UX_Divulgacao_ImagemChave</c> que fecha a corrida do 409 e a ordem determinística por Prioridade e Id.
/// </summary>
public class DivulgacoesSqlTests(BancoDeTeste banco) : IClassFixture<BancoDeTeste>
{
    private readonly Cenario _c = new(banco);

    private sealed class StorageSempreComImagem : IObjectStorage
    {
        public Task<InfoObjeto?> HeadAsync(string bucket, string chave, CancellationToken ct = default) => Task.FromResult<InfoObjeto?>(new InfoObjeto(1, "image/webp", "e"));

        public string UrlPublica(string bucket, string chave) => $"http://localhost/storage/{bucket}/{chave}";

        public Task PutAsync(string bucket, string chave, Stream conteudo, long tamanhoBytes, string contentType, CancellationToken ct = default) => Task.CompletedTask;

        public Task DeleteAsync(string bucket, string chave, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<ObjetoListado>> ListarAsync(string bucket, string prefixo, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<PostPolicyAssinada> CriarPostPolicyAsync(string bucket, string chave, string contentType, long tamanhoMaximoBytes, TimeSpan validade, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<string> UrlGetPreAssinadaAsync(string bucket, string chave, TimeSpan validade, string? contentType = null, string? contentDisposition = null, CancellationToken ct = default) => throw new NotSupportedException();

        public Task MoverAsync(string bucketOrigem, string chaveOrigem, string bucketDestino, string chaveDestino, string etagEsperado, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<bool> VerificarAsync(CancellationToken ct = default) => Task.FromResult(true);
    }

    private async Task LimparAsync()
    {
        await using var db = _c.NovoDb();
        await db.Database.ExecuteSqlRawAsync("DELETE FROM interacao.Divulgacao;");
    }

    private CriarDivulgacaoHandler Criar() => new(_c.NovoDb(), _c.Relogio, new StorageSempreComImagem());

    private async Task<Guid> AdminAsync() => (await _c.CriarUsuarioAsync(Cenario.Email(), Role.Admin)).Id;

    [SqlFact]
    public async Task As_ativas_comparam_Ativo_com_constante_e_projetam_so_o_INCLUDE_do_indice()
    {
        await using var db = _c.NovoDb();

        var sql = DivulgacoesAtivasHandler.Ativas(db, DateTime.UtcNow).OrderByDescending(d => d.Prioridade).ThenBy(d => d.Id)
            .Select(d => new { d.Id, d.Prioridade, d.Titulo, d.Mensagem, d.ImagemChaveStorage, d.LinkDestino, d.InicioExibicaoUtc, d.FimExibicaoUtc }).ToQueryString();

        Assert.Matches(@"\[d\]\.\[Ativo\] = CAST\(1 AS bit\)|\[d\]\.\[Ativo\] = 1", sql);
        Assert.DoesNotMatch(@"Ativo\] = @", sql);
        Assert.Contains("ORDER BY [d].[Prioridade] DESC, [d].[Id]", sql);
        Assert.DoesNotContain("CriadoPorUsuarioId", sql);
        Assert.DoesNotContain("RowVersion", sql);
    }

    [SqlFact]
    public async Task Ativas_no_SQL_de_verdade_respeitam_janela_prioridade_e_desempate()
    {
        await LimparAsync();
        var admin = await AdminAsync();
        var agora = DateTime.UtcNow;
        var inicio = new DateTime(agora.Ticks - agora.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
        async Task C(string t, byte p, DateTime? i = null, DateTime? f = null, bool ativo = true) =>
            await Criar().Handle(new CriarDivulgacaoCommand(admin, t, "Msg", null, null, p, i, f, ativo), default);
        await C("Baixa", 10);
        await C("Alta", 90);
        await C("Futura", 99, inicio.AddHours(1));
        await C("Vencida", 99, f: inicio.AddMinutes(-1));
        await C("Inativa", 99, ativo: false);
        await C("Media-A", 50);
        await C("Media-B", 50);

        await using var db = _c.NovoDb();
        var lista = await new DivulgacoesAtivasHandler(db, new RelogioDeAgora(), new StorageSempreComImagem()).Handle(new DivulgacoesAtivasQuery(), default);

        var titulos = lista.Select(d => d.Titulo).ToArray();
        Assert.Equal("Alta", titulos[0]);
        Assert.Equal("Baixa", titulos[^1]);
        Assert.Equal(["Media-A", "Media-B"], titulos[1..3].Order());
        // O desempate é por Id (a ordem do uniqueidentifier no SQL Server, que não é a do .NET): determinístico entre duas consultas.
        var denovo = await new DivulgacoesAtivasHandler(_c.NovoDb(), new RelogioDeAgora(), new StorageSempreComImagem()).Handle(new DivulgacoesAtivasQuery(), default);
        Assert.Equal(titulos, denovo.Select(d => d.Titulo));
    }

    private sealed class RelogioDeAgora : IClock
    {
        public DateTime UtcNow => DateTime.UtcNow;
    }

    [SqlFact]
    public async Task O_indice_unico_barra_a_mesma_imagem_em_duas_divulgacoes_mesmo_sem_a_checagem_do_handler()
    {
        await LimparAsync();
        var admin = await AdminAsync();
        var chave = ChaveDeImagem.Nova();
        var a = new Domain.Interacao.Divulgacao(Guid.NewGuid(), "A", "M", chave, null, 1, null, null, admin, DateTime.UtcNow);
        var b = new Domain.Interacao.Divulgacao(Guid.NewGuid(), "B", "M", chave, null, 1, null, null, admin, DateTime.UtcNow);
        await using var db = _c.NovoDb();
        db.Divulgacoes.AddRange(a, b);

        var erro = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());

        Assert.Contains("UX_Divulgacao_ImagemChave", erro.InnerException!.Message);
    }

    [SqlFact]
    public async Task Duas_divulgacoes_sem_imagem_convivem_o_indice_so_vale_para_chave_nao_nula()
    {
        await LimparAsync();
        var admin = await AdminAsync();

        await Criar().Handle(new CriarDivulgacaoCommand(admin, "A", "M", null, null, 1, null, null, true), default);
        await Criar().Handle(new CriarDivulgacaoCommand(admin, "B", "M", null, null, 1, null, null, true), default);

        await using var db = _c.NovoDb();
        Assert.Equal(2, await db.Divulgacoes.CountAsync());
    }

    [SqlFact]
    public async Task Dois_PUT_simultaneos_com_a_mesma_chave_um_200_e_um_409()
    {
        await LimparAsync();
        var admin = await AdminAsync();
        var a = await Criar().Handle(new CriarDivulgacaoCommand(admin, "A", "M", null, null, 1, null, null, true), default);
        var b = await Criar().Handle(new CriarDivulgacaoCommand(admin, "B", "M", null, null, 1, null, null, true), default);
        var chave = ChaveDeImagem.Nova();

        async Task<bool> Editar(Guid id)
        {
            var db = _c.NovoDb();
            var versao = Convert.FromBase64String((await db.Divulgacoes.AsNoTracking().SingleAsync(d => d.Id == id)).RowVersion is { } v ? Convert.ToBase64String(v) : "");
            try
            {
                await new EditarDivulgacaoHandler(db, _c.Relogio, NullLogger<EditarDivulgacaoHandler>.Instance, new StorageSempreComImagem())
                    .Handle(new EditarDivulgacaoCommand(id, versao, "Editada", "M", chave, null, 1, null, null, true), default);
                return true;
            }
            catch (ImagemEmUsoException)
            {
                return false;
            }
        }

        var r = await Task.WhenAll(Editar(a.Id), Editar(b.Id));

        Assert.Equal(1, r.Count(x => x));
        await using var conferir = _c.NovoDb();
        Assert.Equal(1, await conferir.Divulgacoes.CountAsync(d => d.ImagemChaveStorage == chave));
    }

    [SqlFact]
    public async Task PUT_com_RowVersion_velho_e_412_no_SQL_real()
    {
        await LimparAsync();
        var admin = await AdminAsync();
        var criada = await Criar().Handle(new CriarDivulgacaoCommand(admin, "A", "M", null, null, 1, null, null, true), default);
        var versaoAntiga = Convert.FromBase64String(criada.Versao);
        var db1 = _c.NovoDb();
        await new EditarDivulgacaoHandler(db1, _c.Relogio, NullLogger<EditarDivulgacaoHandler>.Instance, new StorageSempreComImagem())
            .Handle(new EditarDivulgacaoCommand(criada.Id, versaoAntiga, "Primeira", "M", null, null, 1, null, null, true), default);

        var db2 = _c.NovoDb();
        await Assert.ThrowsAsync<VersaoDesatualizadaException>(() => new EditarDivulgacaoHandler(db2, _c.Relogio, NullLogger<EditarDivulgacaoHandler>.Instance, new StorageSempreComImagem())
            .Handle(new EditarDivulgacaoCommand(criada.Id, versaoAntiga, "Segunda", "M", null, null, 1, null, null, true), default));
    }
}
