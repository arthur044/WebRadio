using MediatR;
using Microsoft.EntityFrameworkCore;
using WebRadio.Application.Abstracoes;
using WebRadio.Application.Comum;

namespace WebRadio.Application.Autenticacao;

public sealed record LogoutCommand(string? TokenBruto) : IRequest;

public sealed class LogoutHandler(IRefreshTokenService refresh) : IRequestHandler<LogoutCommand>
{
    public async Task Handle(LogoutCommand request, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(request.TokenBruto))
        {
            await refresh.RevogarFamiliaAsync(request.TokenBruto, ct);
        }
    }
}

public sealed record ObterMeQuery(Guid UsuarioId) : IRequest<UsuarioDto>;

public sealed class ObterMeHandler(IRadioDbContext db) : IRequestHandler<ObterMeQuery, UsuarioDto>
{
    public async Task<UsuarioDto> Handle(ObterMeQuery request, CancellationToken ct)
    {
        var usuario = await db.Usuarios.AsNoTracking().FirstOrDefaultAsync(u => u.Id == request.UsuarioId && u.Ativo, ct)
            ?? throw new NaoEncontradoException("Usuário");

        return UsuarioDto.De(usuario);
    }
}
