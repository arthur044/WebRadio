using WebRadio.Application.Abstracoes;
using WebRadio.Domain.Usuarios;

namespace WebRadio.Application.Features.Auth;

public sealed record UsuarioDto(Guid Id, string Nome, string Email, string Role, bool DeveTrocarSenha)
{
    public static UsuarioDto De(Usuario u) => new(u.Id, u.Nome, u.Email, u.Role.ToString(), u.DeveTrocarSenha);

    // Sem e-mail no ToString: um {Obj} sem @ no log não pode vazar PII.
    public override string ToString() => $"UsuarioDto {Id}";
}

public sealed record LoginResultado(string AccessToken, DateTime ExpiraEmUtc, bool DeveTrocarSenha, UsuarioDto Usuario, RefreshOpaco Refresh)
{
    public override string ToString() => nameof(LoginResultado);
}
