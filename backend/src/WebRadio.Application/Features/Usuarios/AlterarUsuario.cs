using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using WebRadio.Application.Abstracoes;
using WebRadio.Domain.Enums;
using WebRadio.Domain.Erros;

namespace WebRadio.Application.Features.Usuarios;

/// <summary><c>Versao</c> é o RowVersion que o cliente leu (cabeçalho <c>If-Match</c>).</summary>
public sealed record AlterarUsuarioCommand(Guid UsuarioId, Role? Role, bool? Ativo, byte[] Versao) : IRequest<UsuarioAdminDto>
{
    public override string ToString() => nameof(AlterarUsuarioCommand);
}

public sealed class AlterarUsuarioValidator : AbstractValidator<AlterarUsuarioCommand>
{
    public AlterarUsuarioValidator()
    {
        RuleFor(x => x.Role).IsInEnum().WithMessage("Papel inválido.");
        RuleFor(x => x).Must(x => x.Role is not null || x.Ativo is not null)
            .WithName("corpo").WithMessage("Informe role e/ou ativo.");
    }
}

public sealed class AlterarUsuarioHandler(
    IRadioDbContext db,
    ITransacaoComTrava transacao,
    IRefreshTokenService refresh,
    IOutbox outbox,
    IVersaoDeSeguranca versaoDeSeguranca,
    IClock clock) : IRequestHandler<AlterarUsuarioCommand, UsuarioAdminDto>
{
    public async Task<UsuarioAdminDto> Handle(AlterarUsuarioCommand cmd, CancellationToken ct)
    {
        // D22: "último Admin ativo" só é verdade enquanto ninguém mais mexe em Admins; a trava vale até o commit.
        var dto = await transacao.ExecutarAsync(Travas.Admins, async ct2 =>
        {
            var usuario = await db.Usuarios.FirstOrDefaultAsync(u => u.Id == cmd.UsuarioId, ct2)
                ?? throw new NaoEncontradoException("Usuário");
            if (!usuario.RowVersion.AsSpan().SequenceEqual(cmd.Versao)) throw new VersaoDesatualizadaException();

            var agora = clock.UtcNow;
            var versaoAntes = usuario.VersaoSeguranca;
            var ehUltimoAdminAtivo = usuario.Ativo
                && !await db.Usuarios.AnyAsync(u => u.Role == Role.Admin && u.Ativo && u.Id != usuario.Id, ct2);

            if (cmd.Role is { } role) usuario.AlterarRole(role, ehUltimoAdminAtivo, agora);
            if (cmd.Ativo is false) usuario.Desativar(ehUltimoAdminAtivo, agora);
            else if (cmd.Ativo is true) usuario.Reativar(agora);

            if (usuario.VersaoSeguranca != versaoAntes)
            {
                // S-M11 / S-M08: papel alterado ou desativação derrubam TODAS as sessões e o hub, na mesma transação.
                await refresh.RevogarTodasDoUsuarioAsync(usuario.Id, manterFamiliaDoToken: null, ct2);
                outbox.Enfileirar(EventosDoOutbox.UsuarioSessoesRevogadas, new { usuarioId = usuario.Id });
            }

            await db.SaveChangesAsync(ct2);
            return UsuarioAdminDto.De(usuario);
        }, ct);

        // Depois do commit: o access token antigo para de valer nesta instância agora, e nas outras em até 15 s.
        versaoDeSeguranca.Esquecer(cmd.UsuarioId);
        return dto;
    }
}
