using WebRadio.Application.Abstracoes;
using WebRadio.Domain.Usuarios;

namespace WebRadio.Application.Autenticacao;

/// <summary>02-api-rest §2: UsuarioDto = { id, nome, email, role, deveTrocarSenha }.</summary>
public sealed record UsuarioDto(Guid Id, string Nome, string Email, string Role, bool DeveTrocarSenha)
{
    public static UsuarioDto De(Usuario u) => new(u.Id, u.Nome, u.Email, u.Role.ToString(), u.DeveTrocarSenha);
}

/// <summary>Corpo do 200 de login/refresh.</summary>
public sealed record SessaoResposta(string AccessToken, DateTime ExpiraEmUtc, bool DeveTrocarSenha, UsuarioDto Usuario);

/// <summary>A Api devolve <see cref="Resposta"/> no corpo e <see cref="Refresh"/> só no cookie HttpOnly.</summary>
public sealed record SessaoIniciada(SessaoResposta Resposta, RefreshEmitido Refresh);
