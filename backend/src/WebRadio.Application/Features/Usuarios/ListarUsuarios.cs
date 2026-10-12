using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using WebRadio.Application.Abstracoes;
using WebRadio.Domain.Enums;

namespace WebRadio.Application.Features.Usuarios;

public sealed record ListarUsuariosQuery(Role? Role, string? Busca, int Pagina, int Tamanho) : IRequest<ResultadoPaginado<UsuarioAdminDto>>
{
    // A busca pode ser um e-mail: não vai para o log.
    public override string ToString() => nameof(ListarUsuariosQuery);
}

public sealed class ListarUsuariosValidator : AbstractValidator<ListarUsuariosQuery>
{
    public ListarUsuariosValidator()
    {
        RuleFor(x => x.Role).IsInEnum().WithMessage("Papel inválido.");
        RuleFor(x => x.Busca).MaximumLength(100).WithMessage("A busca pode ter no máximo 100 caracteres.");
        RuleFor(x => x.Pagina).InclusiveBetween(1, 1_000_000).WithMessage("A página começa em 1.");
        RuleFor(x => x.Tamanho).InclusiveBetween(1, ResultadoPaginado<UsuarioAdminDto>.TamanhoMaximo)
            .WithMessage($"O tamanho da página vai de 1 a {ResultadoPaginado<UsuarioAdminDto>.TamanhoMaximo}.");
    }
}

public sealed class ListarUsuariosHandler(IRadioDbContext db) : IRequestHandler<ListarUsuariosQuery, ResultadoPaginado<UsuarioAdminDto>>
{
    public async Task<ResultadoPaginado<UsuarioAdminDto>> Handle(ListarUsuariosQuery q, CancellationToken ct)
    {
        var consulta = db.Usuarios.AsNoTracking();
        if (q.Role is { } role) consulta = consulta.Where(u => u.Role == role);

        var busca = q.Busca?.Trim();
        if (!string.IsNullOrEmpty(busca))
        {
            // Contains com parâmetro vira CHARINDEX(@p, coluna) > 0 no SQL Server (não LIKE): %, _ e [ do texto são
            // literais. É busca no meio do texto (scan), aceitável para a tabela de usuários. O e-mail é comparado pelo normalizado
            // (maiúsculas, collation binária), para "ana@" achar "Ana@".
            var buscaNormalizada = busca.ToUpperInvariant();
            consulta = consulta.Where(u => u.Nome.Contains(busca) || u.EmailNormalizado.Contains(buscaNormalizada));
        }

        var total = await consulta.CountAsync(ct);
        var usuarios = await consulta
            .OrderBy(u => u.Nome).ThenBy(u => u.Id)
            .Skip((q.Pagina - 1) * q.Tamanho).Take(q.Tamanho)
            .ToListAsync(ct);

        return new ResultadoPaginado<UsuarioAdminDto>(usuarios.Select(UsuarioAdminDto.De).ToList(), total, q.Pagina, q.Tamanho);
    }
}
