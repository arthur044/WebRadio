using FluentValidation;
using FluentValidation.Results;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WebRadio.Application.Abstracoes;
using WebRadio.Application.Comum;
using WebRadio.Domain.Enums;
using WebRadio.Domain.Erros;
using WebRadio.Domain.Midia;

namespace WebRadio.Application.Features.Midias;

/// <summary>
/// <c>PATCH /midias/{id}</c> + <c>If-Match</c> (02 §6). <c>TituloPresente</c>/<c>ArtistaPresente</c> distinguem "campo ausente" (não muda) de "vazio" (limpa; a
/// Música não pode ficar sem título). <c>ativo</c> só o Admin altera e o Locutor só edita a PRÓPRIA mídia (<see cref="AcessoNegadoException"/> → 403); mídia desativada não existe para o Locutor (404).
/// A autorização vem ANTES do <c>If-Match</c>: o 412 nunca vaza para quem não pode editar.
/// </summary>
public sealed record EditarMidiaCommand(Guid MidiaId, byte[] Versao, string? Titulo, string? Artista, bool? Ativo, bool EhAdmin, bool TituloPresente, bool ArtistaPresente, Guid UsuarioId)
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
        // Existência, visibilidade e dono antes do If-Match (08 E4-F03). Mídia desativada some para o Locutor (404, como no GET e na lista); de outro dono → 403
        // (o Locutor já vê as ativas dos outros).
        if (!cmd.EhAdmin)
        {
            if (!midia.Ativo) throw new NaoEncontradoException("Mídia");
            if (midia.EnviadoPorUsuarioId != cmd.UsuarioId) throw new AcessoNegadoException("Só quem enviou a mídia (ou o Admin) pode editá-la.");
            if (cmd.Ativo is not null) throw new AcessoNegadoException("Só o Admin ativa ou desativa uma mídia.");
        }

        if (!midia.RowVersion.AsSpan().SequenceEqual(cmd.Versao)) throw new VersaoDesatualizadaException();

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
        return MidiaDto.De(midia, nome, cmd.EhAdmin, cmd.UsuarioId);
    }

    private static string? Normalizar(string? texto) => string.IsNullOrWhiteSpace(texto) ? null : TextoSeguro.Normalizar(texto);
}

/// <summary>
/// <c>DELETE /midias/{id}</c> (02 §6). Admin (E4-D14): sem reprodução, apaga o registro (204); com reprodução, só desativa (200 + DTO). <b>Desistência do envio</b> (08 v1.5): em mídia
/// <c>AguardandoUpload</c>, o DONO (ou o Admin) a cancela (204): vai a <c>Rejeitado</c> com <see cref="MotivosRejeicao.EnvioCancelado"/>, a linha fica e o slot dos 5 abertos volta.
/// </summary>
public sealed record ExcluirMidiaCommand(Guid MidiaId, Guid SolicitanteId, bool EhAdmin) : IRequest<ResultadoDaExclusao>;

public sealed record ResultadoDaExclusao(bool Excluida, MidiaDto? Midia);

/// <summary>
/// A auditoria <c>MIDIA_EXCLUIDA</c> sai pelo log estruturado DEPOIS do <c>DELETE</c> confirmado (como as outras trilhas do projeto): id, os dois hashes em hex, o autor
/// do envio, quem excluiu e o status na hora — o S-M03 guarda os hashes e o autor para inteligência de malware mesmo depois de o registro sumir. O arquivo em
/// <c>midia/</c> é apagado pelo Worker como órfão (E4-D11): a API não tem <c>s3:DeleteObject</c> lá (S-A04). Mídia em análise não se exclui (o Worker a tem).
/// </summary>
public sealed class ExcluirMidiaHandler(IRadioDbContext db, IClock clock, ILogger<ExcluirMidiaHandler> logger) : IRequestHandler<ExcluirMidiaCommand, ResultadoDaExclusao>
{
    public async Task<ResultadoDaExclusao> Handle(ExcluirMidiaCommand cmd, CancellationToken ct)
    {
        var midia = await db.ArquivosMidia.FirstOrDefaultAsync(m => m.Id == cmd.MidiaId, ct) ?? throw new NaoEncontradoException("Mídia");
        if (!cmd.EhAdmin)
        {
            // Locutor: 404 se ele não vê a mídia (desativada), 403 se vê e não é dele ou não está em AguardandoUpload. Dono = EnviadoPorUsuarioId contra o `sub` do token.
            if (!midia.Ativo) throw new NaoEncontradoException("Mídia");
            if (midia.EnviadoPorUsuarioId != cmd.SolicitanteId || midia.StatusSanitizacao != StatusSanitizacao.AguardandoUpload)
                throw new AcessoNegadoException("Só o Admin exclui mídia; o dono só desiste de um envio que ainda aguarda o arquivo.");
        }

        if (midia.StatusSanitizacao == StatusSanitizacao.AguardandoUpload) return await CancelarEnvioAsync(midia, cmd.SolicitanteId, ct);

        if (midia.StatusSanitizacao == StatusSanitizacao.EmAnalise) throw new TransicaoInvalidaException("A mídia está em análise pelo Worker; tente de novo depois.");

        if (await db.Reproducoes.AnyAsync(r => r.MidiaId == midia.Id, ct))
        {
            midia.Desativar();
            await db.SaveChangesAsync(ct);
            return new ResultadoDaExclusao(false, MidiaDto.De(midia, null, admin: true, cmd.SolicitanteId));
        }

        // Os valores são capturados ANTES do DELETE (depois dele a entidade some do tracker), mas a auditoria só sai DEPOIS de o SaveChanges dar certo: a trilha nunca
        // registra uma exclusão que não aconteceu.
        var (id, status, hashSha, hashOriginal, enviadoPor) = (midia.Id, midia.StatusSanitizacao, Hex(midia.HashSHA256), Hex(midia.HashOriginalSHA256), midia.EnviadoPorUsuarioId);
        db.ArquivosMidia.Remove(midia);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // O mais provável: o Worker capturou o item (Pendente -> EmAnalise) e trocou o RowVersion entre a leitura e o DELETE.
            throw new TransicaoInvalidaException("A mídia mudou de status enquanto era excluída; tente de novo.");
        }
        catch (DbUpdateException)
        {
            // Uma reprodução nasceu entre a conferência e o DELETE (a FK recusa): a mídia começou a tocar agora.
            throw new TransicaoInvalidaException("A mídia começou a tocar agora; desative-a em vez de excluir.");
        }

        logger.LogInformation(
            "MIDIA_EXCLUIDA midia {MidiaId} status {Status} hashSha256 {HashSha256} hashOriginalSha256 {HashOriginalSha256} enviadoPor {EnviadoPorUsuarioId} excluidaPor {SolicitanteId}",
            id, status, hashSha, hashOriginal, enviadoPor, cmd.SolicitanteId);
        return new ResultadoDaExclusao(true, null);
    }

    private async Task<ResultadoDaExclusao> CancelarEnvioAsync(ArquivoMidia midia, Guid solicitante, CancellationToken ct)
    {
        var (id, dono, tamanho) = (midia.Id, midia.EnviadoPorUsuarioId, midia.TamanhoBytes);
        try
        {
            midia.CancelarEnvio(clock.UtcNow);
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Um concluir ganhou a corrida (RowVersion): exatamente um vence.
            throw new TransicaoInvalidaException("O envio já foi concluído; não dá mais para cancelar.");
        }

        // Sem auditoria MidiaExcluida (não há hash); campos fechados, nunca o nome do arquivo.
        logger.LogInformation("MIDIA_ENVIO_CANCELADO midia {MidiaId} usuario {UsuarioId} excluidaPor {SolicitanteId} tamanhoDeclarado {TamanhoDeclarado}", id, dono, solicitante, tamanho);
        return new ResultadoDaExclusao(true, null);
    }

    private static string Hex(byte[]? hash) => hash is null ? "-" : Convert.ToHexStringLower(hash);
}
