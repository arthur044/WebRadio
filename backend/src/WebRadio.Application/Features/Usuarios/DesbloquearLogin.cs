using MediatR;
using Microsoft.EntityFrameworkCore;
using WebRadio.Application.Abstracoes;
using WebRadio.Application.Features.Auth;
using WebRadio.Domain.Erros;

namespace WebRadio.Application.Features.Usuarios;

public sealed record DesbloquearLoginCommand(Guid UsuarioId) : IRequest;

/// <summary>02 §3: limpa os contadores de falha de login da conta (todos os IPs e o contador por conta).</summary>
public sealed class DesbloquearLoginHandler(IRadioDbContext db, ILoginThrottle throttle) : IRequestHandler<DesbloquearLoginCommand>
{
    public async Task Handle(DesbloquearLoginCommand cmd, CancellationToken ct)
    {
        var email = await db.Usuarios.AsNoTracking().Where(u => u.Id == cmd.UsuarioId).Select(u => u.Email).FirstOrDefaultAsync(ct)
            ?? throw new NaoEncontradoException("Usuário");

        // A mesma chave que o login usa (mais grossa que a conta, ver EmailChave).
        await throttle.DesbloquearContaAsync(EmailChave.Normalizar(email), ct);
    }
}
