using FluentValidation;
using FluentValidation.Results;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WebRadio.Application.Abstracoes;
using WebRadio.Application.Comum;
using WebRadio.Domain.Enums;
using WebRadio.Domain.Erros;

namespace WebRadio.Application.Features.Midias;

/// <summary>
/// <c>PATCH /midias/{id}</c> + <c>If-Match</c> (02 §6). <c>TituloPresente</c>/<c>ArtistaPresente</c> distinguem "campo ausente" (não muda) de "vazio" (limpa; a
/// Música não pode ficar sem título). <c>ativo</c> só o Admin altera (<see cref="AcessoNegadoException"/> → 403).
/// </summary>
public sealed record EditarMidiaCommand(Guid MidiaId, byte[] Versao, string? Titulo, string? Artista, bool? Ativo, bool EhAdmin, bool TituloPresente, bool ArtistaPresente)
    : IRequest<MidiaDto>;

public sealed class EditarMidiaValidator : AbstractValidator<EditarMidiaCommand>
{
    public EditarMidiaValidator()
    {
        When(x => x.Titulo is { Length: > 0 }, () => RuleFor(x => x.Titulo).TextoSeguro(IniciarUploadValidator.TamanhoTitulo));
        When(x => x.Artista is { Length: > 0 }, () => RuleFor(x => x.Artista).TextoSeguro(IniciarUploadValidator.TamanhoArtista));
    }
}

public sealed class EditarMidiaHandler(IRadioDbContext db) : IRequestHandler<EditarMidiaCommand, MidiaDto>
{
    public async Task<MidiaDto> Handle(EditarMidiaCommand cmd, CancellationToken ct)
    {
        var midia = await db.ArquivosMidia.FirstOrDefaultAsync(m => m.Id == cmd.MidiaId, ct) ?? throw new NaoEncontradoException("Mídia");
        if (!midia.RowVersion.AsSpan().SequenceEqual(cmd.Versao)) throw new VersaoDesatualizadaException();
        if (cmd.Ativo is not null && !cmd.EhAdmin) throw new AcessoNegadoException("Só o Admin ativa ou desativa uma mídia.");

        var titulo = cmd.TituloPresente ? Normalizar(cmd.Titulo) : midia.Titulo;
        var artista = cmd.ArtistaPresente ? Normalizar(cmd.Artista) : midia.Artista;
        if (midia.TipoMidia == TipoMidia.Musica && titulo is null) throw new ValidationException([new ValidationFailure("titulo", "A música precisa de título.")]);
        midia.EditarTexto(titulo, artista);
        if (cmd.Ativo is { } ativo)
        {
            if (ativo) midia.Ativar();
            else midia.Desativar();
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // O RowVersion mudou entre a leitura e o UPDATE (outro PATCH, ou o Worker): a versão que o cliente leu não vale mais.
            throw new VersaoDesatualizadaException();
        }

        var nome = await db.Usuarios.AsNoTracking().Where(u => u.Id == midia.EnviadoPorUsuarioId).Select(u => u.Nome).FirstOrDefaultAsync(ct);
        return MidiaDto.De(midia, nome, cmd.EhAdmin);
    }

    private static string? Normalizar(string? texto) => string.IsNullOrWhiteSpace(texto) ? null : TextoSeguro.Normalizar(texto);
}

/// <summary><c>DELETE /midias/{id}</c> (Admin, E4-D14): sem reprodução, apaga o registro (204); com reprodução, só desativa (200 + DTO).</summary>
public sealed record ExcluirMidiaCommand(Guid MidiaId, Guid SolicitanteId) : IRequest<ResultadoDaExclusao>;

public sealed record ResultadoDaExclusao(bool Excluida, MidiaDto? Midia);

/// <summary>
/// A auditoria <c>MIDIA_EXCLUIDA</c> sai pelo log estruturado ANTES do <c>DELETE</c> (como as outras trilhas do projeto): id, os dois hashes em hex, o autor
/// do envio, quem excluiu e o status na hora — o S-M03 guarda os hashes e o autor para inteligência de malware mesmo depois de o registro sumir. O arquivo em
/// <c>midia/</c> é apagado pelo Worker como órfão (E4-D11): a API não tem <c>s3:DeleteObject</c> lá (S-A04). Mídia em análise não se exclui (o Worker a tem).
/// </summary>
public sealed class ExcluirMidiaHandler(IRadioDbContext db, ILogger<ExcluirMidiaHandler> logger) : IRequestHandler<ExcluirMidiaCommand, ResultadoDaExclusao>
{
    public async Task<ResultadoDaExclusao> Handle(ExcluirMidiaCommand cmd, CancellationToken ct)
    {
        var midia = await db.ArquivosMidia.FirstOrDefaultAsync(m => m.Id == cmd.MidiaId, ct) ?? throw new NaoEncontradoException("Mídia");
        if (midia.StatusSanitizacao == StatusSanitizacao.EmAnalise) throw new TransicaoInvalidaException("A mídia está em análise pelo Worker; tente de novo depois.");

        if (await db.Reproducoes.AnyAsync(r => r.MidiaId == midia.Id, ct))
        {
            midia.Desativar();
            await db.SaveChangesAsync(ct);
            return new ResultadoDaExclusao(false, MidiaDto.De(midia, null, admin: true));
        }

        logger.LogInformation(
            "MIDIA_EXCLUIDA midia {MidiaId} status {Status} hashSha256 {HashSha256} hashOriginalSha256 {HashOriginalSha256} enviadoPor {EnviadoPorUsuarioId} excluidaPor {SolicitanteId}",
            midia.Id, midia.StatusSanitizacao, Hex(midia.HashSHA256), Hex(midia.HashOriginalSHA256), midia.EnviadoPorUsuarioId, cmd.SolicitanteId);
        db.ArquivosMidia.Remove(midia);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Uma reprodução nasceu entre a conferência e o DELETE (a FK recusa): a mídia começou a tocar agora.
            throw new TransicaoInvalidaException("A mídia começou a tocar agora; desative-a em vez de excluir.");
        }

        return new ResultadoDaExclusao(true, null);
    }

    private static string Hex(byte[]? hash) => hash is null ? "-" : Convert.ToHexStringLower(hash);
}
