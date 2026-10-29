using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WebRadio.Application.Abstracoes;
using WebRadio.Application.Comum;
using WebRadio.Domain.Erros;
using WebRadio.Domain.Interacao;

namespace WebRadio.Application.Features.Divulgacoes;

/// <summary>
/// <c>POST /divulgacoes</c> (02 §7): o corpo é o mesmo do <c>PUT</c>; <c>ativo = false</c> cria e chama <c>Desativar</c>. O texto vai normalizado
/// (NFC, sem as pontas) e o link na forma canônica do S-B03. Cria só o Admin (<c>CriadoPorUsuarioId</c>).
/// </summary>
public sealed record CriarDivulgacaoCommand(
    Guid AdminId, string Titulo, string Mensagem, string? ImagemChave, string? LinkDestino, byte Prioridade,
    DateTime? InicioExibicaoUtc, DateTime? FimExibicaoUtc, bool Ativo) : IRequest<DivulgacaoDto>, ICorpoDeDivulgacao;

public sealed class CriarDivulgacaoValidator : CorpoDeDivulgacaoValidator<CriarDivulgacaoCommand>;

public sealed class CriarDivulgacaoHandler(IRadioDbContext db, IClock clock, IObjectStorage? storage = null)
    : IRequestHandler<CriarDivulgacaoCommand, DivulgacaoDto>
{
    public async Task<DivulgacaoDto> Handle(CriarDivulgacaoCommand cmd, CancellationToken ct)
    {
        if (cmd.ImagemChave is { } chave)
            await ImagemDeDivulgacao.GarantirUsavelAsync(storage ?? throw new InvalidOperationException("Storage não configurado."), db, chave, divulgacaoId: null, ct);

        var divulgacao = new Divulgacao(Guid.CreateVersion7(), TextoSeguro.Normalizar(cmd.Titulo), TextoSeguro.Normalizar(cmd.Mensagem), cmd.ImagemChave,
            cmd.LinkDestino, cmd.Prioridade, cmd.InicioExibicaoUtc, cmd.FimExibicaoUtc, cmd.AdminId, clock.UtcNow);
        if (!cmd.Ativo) divulgacao.Desativar(clock.UtcNow);

        db.Divulgacoes.Add(divulgacao);
        await DivulgacoesDb.SalvarAsync(db, ct);
        return DivulgacaoDto.De(divulgacao, storage);
    }
}

/// <summary>
/// <c>PUT /divulgacoes/{id}</c> com <c>If-Match</c>: SUBSTITUI o recurso inteiro (<c>ImagemChave</c> nula = sem imagem). O <c>Ativo</c> do corpo
/// chama <c>Ativar</c>/<c>Desativar</c> depois do <c>Editar</c> (que não recebe <c>ativo</c>). A imagem antiga é apagada DEPOIS do commit
/// (E3-F07); falha no storage só gera aviso: a <c>Manutencao</c> recolhe a órfã em 24 h.
/// </summary>
public sealed record EditarDivulgacaoCommand(
    Guid Id, byte[] Versao, string Titulo, string Mensagem, string? ImagemChave, string? LinkDestino, byte Prioridade,
    DateTime? InicioExibicaoUtc, DateTime? FimExibicaoUtc, bool Ativo) : IRequest<DivulgacaoDto>, ICorpoDeDivulgacao;

public sealed class EditarDivulgacaoValidator : CorpoDeDivulgacaoValidator<EditarDivulgacaoCommand>;

public sealed class EditarDivulgacaoHandler(IRadioDbContext db, IClock clock, ILogger<EditarDivulgacaoHandler> logger, IObjectStorage? storage = null)
    : IRequestHandler<EditarDivulgacaoCommand, DivulgacaoDto>
{
    public async Task<DivulgacaoDto> Handle(EditarDivulgacaoCommand cmd, CancellationToken ct)
    {
        var divulgacao = await db.Divulgacoes.FirstOrDefaultAsync(d => d.Id == cmd.Id, ct) ?? throw new NaoEncontradoException("Divulgação");
        if (!divulgacao.RowVersion.AsSpan().SequenceEqual(cmd.Versao)) throw new VersaoDesatualizadaException();

        var antiga = divulgacao.ImagemChaveStorage;
        if (cmd.ImagemChave is { } chave && chave != antiga)
            await ImagemDeDivulgacao.GarantirUsavelAsync(storage ?? throw new InvalidOperationException("Storage não configurado."), db, chave, cmd.Id, ct);

        var agora = clock.UtcNow;
        divulgacao.Editar(TextoSeguro.Normalizar(cmd.Titulo), TextoSeguro.Normalizar(cmd.Mensagem), cmd.ImagemChave, cmd.LinkDestino, cmd.Prioridade,
            cmd.InicioExibicaoUtc, cmd.FimExibicaoUtc, agora);
        if (cmd.Ativo && !divulgacao.Ativo) divulgacao.Ativar(agora);
        else if (!cmd.Ativo && divulgacao.Ativo) divulgacao.Desativar(agora);

        await DivulgacoesDb.SalvarAsync(db, ct);
        if (antiga is not null && antiga != cmd.ImagemChave) await DivulgacoesDb.ApagarImagemAsync(storage, antiga, logger);
        return DivulgacaoDto.De(divulgacao, storage);
    }
}

/// <summary><c>DELETE /divulgacoes/{id}</c> → 204. Apaga a imagem depois do commit (E3-F07); falha no storage só gera aviso.</summary>
public sealed record RemoverDivulgacaoCommand(Guid Id) : IRequest;

public sealed class RemoverDivulgacaoHandler(IRadioDbContext db, ILogger<RemoverDivulgacaoHandler> logger, IObjectStorage? storage = null)
    : IRequestHandler<RemoverDivulgacaoCommand>
{
    public async Task Handle(RemoverDivulgacaoCommand cmd, CancellationToken ct)
    {
        var divulgacao = await db.Divulgacoes.FirstOrDefaultAsync(d => d.Id == cmd.Id, ct) ?? throw new NaoEncontradoException("Divulgação");
        var imagem = divulgacao.ImagemChaveStorage;

        db.Divulgacoes.Remove(divulgacao);
        await db.SaveChangesAsync(ct);
        if (imagem is not null) await DivulgacoesDb.ApagarImagemAsync(storage, imagem, logger);
    }
}

internal static class DivulgacoesDb
{
    /// <summary>
    /// A violação do <c>UX_Divulgacao_ImagemChave</c> (dois <c>PUT</c> com a mesma chave ao mesmo tempo) é o 409 do E3-D5; o <c>RowVersion</c>
    /// divergente entre a leitura e o UPDATE é o 412.
    /// </summary>
    public static async Task SalvarAsync(IRadioDbContext db, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new VersaoDesatualizadaException();
        }
        catch (DbUpdateException e) when (e.InnerException?.Message.Contains("UX_Divulgacao_ImagemChave", StringComparison.Ordinal) == true)
        {
            throw new ImagemEmUsoException();
        }
    }

    /// <summary>Depois do commit: a divulgação já não aponta para a imagem. Falha no storage vira aviso (a Manutencao apaga a órfã em 24 h, E3-D6).</summary>
    public static async Task ApagarImagemAsync(IObjectStorage? storage, string chave, ILogger logger)
    {
        if (storage is null) return;
        try
        {
            using var prazo = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await storage.DeleteAsync(Buckets.Publico, chave, prazo.Token);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            logger.LogWarning("DIVULGACAO_IMAGEM_NAO_APAGADA ({Tipo}): a Manutencao recolhe a imagem órfã em até 24 h.", e.GetType().Name);
        }
    }
}
