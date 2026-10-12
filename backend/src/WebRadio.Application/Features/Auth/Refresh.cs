using MediatR;
using Microsoft.EntityFrameworkCore;
using WebRadio.Application.Abstracoes;
using WebRadio.Domain.Erros;

namespace WebRadio.Application.Features.Auth;

/// <summary>Token vem do cookie __Secure-wr_refresh; o endpoint nem chama o handler se o cookie não existe.</summary>
public sealed record RefreshCommand(string Token, byte[] IpHash) : IRequest<LoginResultado>
{
    public override string ToString() => nameof(RefreshCommand);
}

public sealed class RefreshHandler(IRadioDbContext db, IRefreshTokenService refresh, IEmissorDeTokens tokens)
    : IRequestHandler<RefreshCommand, LoginResultado>
{
    public async Task<LoginResultado> Handle(RefreshCommand cmd, CancellationToken ct)
    {
        var rotacao = await refresh.RotacionarAsync(cmd.Token, cmd.IpHash, ct);

        if (rotacao.Concorrente) throw new RefreshConcorrenteException();
        if (!rotacao.Sucesso || rotacao.Novo is null) throw new CredenciaisInvalidasException();

        var usuario = await db.Usuarios.FirstOrDefaultAsync(u => u.Id == rotacao.UsuarioId, ct);

        // Desativado depois do login: o refresh seguinte falha e nenhuma sessão sobra (S-M11).
        if (usuario is not { Ativo: true })
        {
            await refresh.RevogarTodasDoUsuarioAsync(rotacao.UsuarioId, manterFamiliaDoToken: null, ct);
            throw new CredenciaisInvalidasException();
        }

        var access = tokens.EmitirAccessToken(usuario);
        return new LoginResultado(access.Token, access.ExpiraEmUtc, usuario.DeveTrocarSenha, UsuarioDto.De(usuario), rotacao.Novo);
    }
}
