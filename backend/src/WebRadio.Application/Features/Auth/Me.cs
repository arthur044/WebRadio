using MediatR;
using Microsoft.EntityFrameworkCore;
using WebRadio.Application.Abstracoes;
using WebRadio.Domain.Erros;

namespace WebRadio.Application.Features.Auth;

public sealed record MeQuery(Guid UsuarioId) : IRequest<UsuarioDto>;

public sealed class MeHandler(IRadioDbContext db) : IRequestHandler<MeQuery, UsuarioDto>
{
    public async Task<UsuarioDto> Handle(MeQuery q, CancellationToken ct)
    {
        var usuario = await db.Usuarios.AsNoTracking().FirstOrDefaultAsync(u => u.Id == q.UsuarioId && u.Ativo, ct);
        // Token válido de usuário removido/desativado: tratado como não autenticado.
        return usuario is null ? throw new CredenciaisInvalidasException() : UsuarioDto.De(usuario);
    }
}
