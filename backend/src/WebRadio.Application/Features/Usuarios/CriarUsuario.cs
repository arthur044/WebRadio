using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using WebRadio.Application.Abstracoes;
using WebRadio.Application.Features.Auth;
using WebRadio.Domain.Enums;
using WebRadio.Domain.Erros;
using WebRadio.Domain.Usuarios;

namespace WebRadio.Application.Features.Usuarios;

public sealed record CriarUsuarioCommand(string Nome, string Email, string SenhaInicial, Role Role) : IRequest<UsuarioAdminDto>
{
    public override string ToString() => nameof(CriarUsuarioCommand);
}

public sealed class CriarUsuarioValidator : AbstractValidator<CriarUsuarioCommand>
{
    public CriarUsuarioValidator(ISenhasVazadas vazadas)
    {
        RuleFor(x => x.Nome).NotEmpty().MaximumLength(120);
        RuleFor(x => x.Email).NotEmpty().MaximumLength(254).EmailAddress();
        RuleFor(x => x.SenhaInicial).NotEmpty().SenhaNova(vazadas);
        RuleFor(x => x.Role).IsInEnum().WithMessage("Papel inválido.");
    }
}

public sealed class CriarUsuarioHandler(IRadioDbContext db, ISenhaHasher hasher, IClock clock) : IRequestHandler<CriarUsuarioCommand, UsuarioAdminDto>
{
    public async Task<UsuarioAdminDto> Handle(CriarUsuarioCommand cmd, CancellationToken ct)
    {
        var email = cmd.Email.Trim();
        var normalizado = email.ToUpperInvariant();
        if (await db.Usuarios.AnyAsync(u => u.EmailNormalizado == normalizado, ct))
            throw new EmailJaCadastradoException();

        // A senha inicial é conhecida pelo Admin que criou a conta: o dono troca no primeiro login (token restrito até lá).
        var usuario = new Usuario(Guid.CreateVersion7(), cmd.Nome.Trim(), email, hasher.Hash(cmd.SenhaInicial), cmd.Role,
            deveTrocarSenha: true, clock.UtcNow);
        db.Usuarios.Add(usuario);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Corrida entre o AnyAsync acima e o índice único: mesma resposta 409 de quem chegou depois.
            if (await db.Usuarios.AsNoTracking().AnyAsync(u => u.EmailNormalizado == normalizado && u.Id != usuario.Id, ct))
                throw new EmailJaCadastradoException();
            throw;
        }

        return UsuarioAdminDto.De(usuario);
    }
}
