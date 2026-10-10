using WebRadio.Domain.Usuarios;

namespace WebRadio.Application.Abstracoes;

public interface IJwtService
{
    AccessTokenEmitido Emitir(Usuario usuario);
}

public sealed record AccessTokenEmitido(string Token, DateTime ExpiraEmUtc);
