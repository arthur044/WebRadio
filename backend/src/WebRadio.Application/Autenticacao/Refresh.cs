using System.Net;
using MediatR;
using Microsoft.EntityFrameworkCore;
using WebRadio.Application.Abstracoes;
using WebRadio.Application.Comum;

namespace WebRadio.Application.Autenticacao;

/// <summary>TokenBruto vem do cookie __Secure-wr_refresh; ausente → o endpoint já responde 401 sem chamar o handler.</summary>
public sealed record RefreshCommand(string TokenBruto, IPAddress? Ip) : IRequest<SessaoIniciada>;

public sealed class RefreshHandler(
    IRadioDbContext db,
    IJwtService jwt,
    IRefreshTokenService refresh,
    IListenerIdentity identidade) : IRequestHandler<RefreshCommand, SessaoIniciada>
{
    public async Task<SessaoIniciada> Handle(RefreshCommand request, CancellationToken ct)
    {
        var ipHash = request.Ip is null ? null : identidade.IpHash(request.Ip);
        var rotacao = await refresh.RotacionarAsync(request.TokenBruto, ipHash, ct);

        if (rotacao.Concorrente)
        {
            throw new RefreshConcorrenteException();
        }

        if (!rotacao.Sucesso || rotacao.Novo is null)
        {
            throw new CredenciaisInvalidasException();
        }

        var usuario = await db.Usuarios.FirstOrDefaultAsync(u => u.Id == rotacao.UsuarioId, ct);

        // Desativado depois do login: o refresh seguinte falha e nenhuma sessão sobra (S-M11).
        if (usuario is null || !usuario.Ativo)
        {
            await refresh.RevogarTodasDoUsuarioAsync(rotacao.UsuarioId, manterFamiliaDoToken: null, ct);
            throw new CredenciaisInvalidasException();
        }

        var access = jwt.Emitir(usuario);
        return new SessaoIniciada(
            new SessaoResposta(access.Token, access.ExpiraEmUtc, usuario.DeveTrocarSenha, UsuarioDto.De(usuario)),
            rotacao.Novo);
    }
}
