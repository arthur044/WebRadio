using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using WebRadio.Application.Abstracoes;
using WebRadio.Domain.Enums;
using WebRadio.Domain.Erros;
using WebRadio.Domain.Usuarios;

namespace WebRadio.Application.Features.Auth;

public sealed record RegistrarCommand(string Nome, string Email, string Senha) : IRequest<UsuarioDto>
{
    public override string ToString() => nameof(RegistrarCommand);
}

public sealed class RegistrarValidator : AbstractValidator<RegistrarCommand>
{
    public RegistrarValidator(ISenhasVazadas vazadas)
    {
        RuleFor(x => x.Nome).NotEmpty().MaximumLength(120);
        RuleFor(x => x.Email).NotEmpty().MaximumLength(254).EmailAddress();
        RuleFor(x => x.Senha).NotEmpty().MinimumLength(12).MaximumLength(128)
            .Must(s => !vazadas.EhVazada(s)).WithMessage("Esta senha é muito comum. Escolha outra.");
    }
}

public sealed class RegistrarHandler(IRadioDbContext db, ISenhaHasher hasher, IClock clock) : IRequestHandler<RegistrarCommand, UsuarioDto>
{
    public async Task<UsuarioDto> Handle(RegistrarCommand cmd, CancellationToken ct)
    {
        var email = cmd.Email.Trim();
        var normalizado = email.ToUpperInvariant();
        if (await db.Usuarios.AnyAsync(u => u.EmailNormalizado == normalizado, ct))
            throw new EmailJaCadastradoException();

        // Role = Ouvinte SEMPRE: o corpo não escolhe papel (escalada de privilégio).
        var usuario = new Usuario(Guid.NewGuid(), cmd.Nome.Trim(), email, hasher.Hash(cmd.Senha), Role.Ouvinte,
            deveTrocarSenha: false, clock.UtcNow);
        db.Usuarios.Add(usuario);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Corrida entre o AnyAsync acima e o índice único: mesma resposta 409 de quem chegou depois.
            // Qualquer outra falha de banco segue como erro (500 genérico).
            if (await db.Usuarios.AsNoTracking().AnyAsync(u => u.EmailNormalizado == normalizado && u.Id != usuario.Id, ct))
                throw new EmailJaCadastradoException();
            throw;
        }

        return UsuarioDto.De(usuario);
    }
}
