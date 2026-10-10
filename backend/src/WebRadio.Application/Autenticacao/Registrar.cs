using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using WebRadio.Application.Abstracoes;
using WebRadio.Application.Comum;
using WebRadio.Domain.Enums;
using WebRadio.Domain.Usuarios;

namespace WebRadio.Application.Autenticacao;

public sealed record RegistrarCommand(string Nome, string Email, string Senha) : IRequest<UsuarioDto>;

public sealed class RegistrarValidator : AbstractValidator<RegistrarCommand>
{
    public RegistrarValidator()
    {
        RuleFor(c => c.Nome)
            .NotEmpty().WithMessage("Informe o nome.")
            .MaximumLength(120).WithMessage("O nome pode ter no máximo 120 caracteres.");
        RuleFor(c => c.Email)
            .NotEmpty().WithMessage("Informe o e-mail.")
            .MaximumLength(254).WithMessage("O e-mail pode ter no máximo 254 caracteres.")
            .EmailAddress().WithMessage("Informe um e-mail válido.");
        RuleFor(c => c.Senha).SenhaValida();
    }
}

public sealed class RegistrarHandler(IRadioDbContext db, IPasswordHasher hasher, IClock clock)
    : IRequestHandler<RegistrarCommand, UsuarioDto>
{
    public async Task<UsuarioDto> Handle(RegistrarCommand request, CancellationToken ct)
    {
        var email = request.Email.Trim();
        var normalizado = email.ToUpperInvariant();

        if (await db.Usuarios.AnyAsync(u => u.EmailNormalizado == normalizado, ct))
        {
            throw new EmailJaCadastradoException();
        }

        // O papel nunca vem do corpo: auto-cadastro é sempre Ouvinte.
        var usuario = new Usuario(Guid.CreateVersion7(), request.Nome.Trim(), email, hasher.Hash(request.Senha), Role.Ouvinte, deveTrocarSenha: false, clock.UtcAgora);
        db.Usuarios.Add(usuario);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ViolouUnicidade(ex))
        {
            // Corrida contra o índice único UX_Usuario_EmailNormalizado.
            throw new EmailJaCadastradoException();
        }

        return UsuarioDto.De(usuario);
    }

    /// <summary>SQL Server 2601 (indice unico) / 2627 (constraint). Qualquer outra falha de banco segue como erro real (500).</summary>
    private static bool ViolouUnicidade(DbUpdateException ex) =>
        ex.InnerException?.GetType().GetProperty("Number")?.GetValue(ex.InnerException) is 2601 or 2627;
}
