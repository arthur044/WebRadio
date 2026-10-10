using System.Net;
using FluentValidation;
using MediatR;
using FluentValidation.Results;
using Microsoft.EntityFrameworkCore;
using WebRadio.Application.Abstracoes;
using WebRadio.Application.Comum;

namespace WebRadio.Application.Autenticacao;

/// <summary>TokenRefreshAtual (cookie) identifica a família que sobrevive à troca; sem ele, todas as sessões caem.</summary>
public sealed record TrocarSenhaCommand(Guid UsuarioId, string SenhaAtual, string NovaSenha, string? TokenRefreshAtual, IPAddress? Ip) : IRequest;

public sealed class TrocarSenhaValidator : AbstractValidator<TrocarSenhaCommand>
{
    public TrocarSenhaValidator()
    {
        RuleFor(c => c.SenhaAtual)
            .NotEmpty().WithMessage("Informe a senha atual.")
            .MaximumLength(RegrasDeSenha.Maximo).WithMessage($"A senha pode ter no máximo {RegrasDeSenha.Maximo} caracteres.");
        RuleFor(c => c.NovaSenha).SenhaValida();
        RuleFor(c => c.NovaSenha)
            .Must((c, nova) => nova != c.SenhaAtual)
            .WithMessage("A nova senha precisa ser diferente da atual.");
    }
}

public sealed class TrocarSenhaHandler(
    IRadioDbContext db,
    IPasswordHasher hasher,
    IRefreshTokenService refresh,
    IClock clock,
    IListenerIdentity identidade,
    LoginBloqueio bloqueio) : IRequestHandler<TrocarSenhaCommand>
{
    public async Task Handle(TrocarSenhaCommand request, CancellationToken ct)
    {
        var usuario = await db.Usuarios.FirstOrDefaultAsync(u => u.Id == request.UsuarioId && u.Ativo, ct)
            ?? throw new NaoEncontradoException("Usuário");

        // Quem so tem o access token (15 min) nao pode usar esta rota como oraculo da senha: mesmo bloqueio do login (D21).
        var ipHash = request.Ip is null ? null : identidade.IpHash(request.Ip);
        var senhaIncorreta = new ValidationException([new ValidationFailure(nameof(request.SenhaAtual), "A senha atual está incorreta.")]);

        if (await bloqueio.EstaBloqueadoAsync(usuario.EmailNormalizado, ipHash, ct))
        {
            hasher.VerificarFicticio(request.SenhaAtual);
            throw senhaIncorreta;
        }

        if (!hasher.Verificar(usuario.SenhaHash, request.SenhaAtual))
        {
            await bloqueio.RegistrarFalhaAsync(usuario.EmailNormalizado, ipHash, ct);
            throw senhaIncorreta;
        }

        await bloqueio.LimparAsync(usuario.EmailNormalizado, ipHash, ct);

        // S-M11: as outras familias caem primeiro (a atual continua; o cliente chama /auth/refresh para perder a restricao).
        // Nesta ordem, uma falha no meio deixa a senha antiga com sessoes revogadas, nunca a nova com sessoes roubadas vivas.
        await refresh.RevogarTodasDoUsuarioAsync(usuario.Id, request.TokenRefreshAtual, ct);

        usuario.TrocarSenha(hasher.Hash(request.NovaSenha), clock.UtcAgora);
        await db.SaveChangesAsync(ct);
    }
}
