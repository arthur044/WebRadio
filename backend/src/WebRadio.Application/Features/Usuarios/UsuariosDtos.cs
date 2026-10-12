using WebRadio.Domain.Usuarios;

namespace WebRadio.Application.Features.Usuarios;

/// <summary>
/// 02 §3: <c>UsuarioDto &amp; { ativo, criadoEmUtc }</c>. <c>Versao</c> é o RowVersion em base64, o mesmo valor do
/// <c>ETag</c>: a lista não tem ETag por item, e o PATCH precisa dele no <c>If-Match</c>.
/// </summary>
public sealed record UsuarioAdminDto(Guid Id, string Nome, string Email, string Role, bool DeveTrocarSenha, bool Ativo, DateTime CriadoEmUtc, string Versao)
{
    public static UsuarioAdminDto De(Usuario u) =>
        new(u.Id, u.Nome, u.Email, u.Role.ToString(), u.DeveTrocarSenha, u.Ativo, u.CriadoEmUtc, Convert.ToBase64String(u.RowVersion));

    // Sem e-mail no ToString: um {Obj} sem @ no log não pode vazar PII.
    public override string ToString() => $"UsuarioAdminDto {Id}";
}

/// <summary>Envelope de paginação da API (02, cabeçalho): <c>{ itens, total, pagina, tamanho }</c>.</summary>
public sealed record ResultadoPaginado<T>(IReadOnlyList<T> Itens, int Total, int Pagina, int Tamanho)
{
    public const int TamanhoPadrao = 20;
    public const int TamanhoMaximo = 100;
}
