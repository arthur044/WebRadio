using MediatR;
using WebRadio.Application.Abstracoes;

namespace WebRadio.Application.Features.Auth;

public sealed record LogoutCommand(string? Token) : IRequest
{
    public override string ToString() => nameof(LogoutCommand);
}

public sealed class LogoutHandler(IRefreshTokenService refresh) : IRequestHandler<LogoutCommand>
{
    public async Task Handle(LogoutCommand cmd, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(cmd.Token))
            await refresh.RevogarFamiliaAsync(cmd.Token, ct);
    }
}
