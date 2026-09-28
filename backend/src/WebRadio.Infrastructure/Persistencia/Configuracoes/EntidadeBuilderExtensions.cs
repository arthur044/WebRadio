using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WebRadio.Domain.Common;

namespace WebRadio.Infrastructure.Persistencia.Configuracoes;

/// <summary>DomainEvents (D22) nunca é uma coluna — só existe em memória até o SaveChangesInterceptor (F06/F10) despachar.</summary>
internal static class EntidadeBuilderExtensions
{
    public static void IgnorarEventosDominio<TEntidade>(this EntityTypeBuilder<TEntidade> builder)
        where TEntidade : Entidade
    {
        builder.Ignore(e => e.DomainEvents);
    }
}

/// <summary>
/// EF Core não tem um método fluente dedicado para nomear constraints DEFAULT (só PK/FK/CK/índices têm
/// HasName/HasConstraintName/HasDatabaseName). A anotação "Relational:DefaultConstraintName" é o único jeito
/// de sair do nome anônimo que o SQL Server atribuiria sozinho — obrigatório aqui porque 03-schema-sqlserver.sql
/// nomeia toda DF_ explicitamente e o Atlas compara o script gerado contra esse arquivo (E1-A03).
/// </summary>
internal static class DefaultConstraintNameExtensions
{
    public static PropertyBuilder<T> HasDefaultConstraintName<T>(this PropertyBuilder<T> builder, string name) =>
        builder.HasAnnotation("Relational:DefaultConstraintName", name);
}
