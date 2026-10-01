using Microsoft.EntityFrameworkCore;
using WebRadio.Domain.Autenticacao;
using WebRadio.Domain.Grade;
using WebRadio.Domain.Interacao;
using WebRadio.Domain.Midia;
using WebRadio.Domain.Usuarios;

namespace WebRadio.Application.Abstracoes;

/// <summary>
/// 00-arquitetura.md §2.1: interface que a Application depende (nunca do DbContext concreto).
/// Implementada por WebRadio.Infrastructure.Persistencia.RadioDbContext (E1-F04).
/// </summary>
public interface IRadioDbContext
{
    DbSet<Usuario> Usuarios { get; }

    DbSet<RefreshToken> RefreshTokens { get; }

    DbSet<Programa> Programas { get; }

    DbSet<PedidoMusica> PedidosMusica { get; }

    DbSet<ArquivoMidia> ArquivosMidia { get; }

    DbSet<Divulgacao> Divulgacoes { get; }

    DbSet<Reproducao> Reproducoes { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
