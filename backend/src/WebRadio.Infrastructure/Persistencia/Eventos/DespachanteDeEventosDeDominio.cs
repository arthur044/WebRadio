using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using WebRadio.Application.Abstracoes;
using WebRadio.Domain.Common;

namespace WebRadio.Infrastructure.Persistencia.Eventos;

/// <summary>
/// D22: despacha os eventos de domínio das entidades rastreadas para os <see cref="IDomainEventHandler{TEvento}"/> da Application,
/// ANTES de o EF montar os comandos: o EF detecta as mudanças depois deste ponto, então o que o handler altera entra no mesmo
/// <c>SaveChanges</c> (mesma transação). Um handler pode levantar outro evento; ele é despachado na rodada seguinte, até um teto.
/// Scoped: resolve os handlers no escopo do DbContext.
/// </summary>
public sealed class DespachanteDeEventosDeDominio(IServiceProvider servicos) : SaveChangesInterceptor
{
    public const int MaximoDeRodadas = 5;

    private static readonly ConcurrentDictionary<Type, Func<IServiceProvider, IDomainEvent, CancellationToken, Task>> Invocadores = new();

    private static readonly MethodInfo Invocar = typeof(DespachanteDeEventosDeDominio)
        .GetMethod(nameof(InvocarAsync), BindingFlags.NonPublic | BindingFlags.Static)!;

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is { } db) await DespacharAsync(db, cancellationToken);
        return result;
    }

    // Todo o código de aplicação grava com SaveChangesAsync. Um SaveChanges síncrono com evento pendente perderia a reação
    // (ex.: revogar a credencial na desativação): falha alto em vez disso.
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        if (eventData.Context is { } db && Pendentes(db).Count > 0)
            throw new InvalidOperationException("Há eventos de domínio pendentes: use SaveChangesAsync.");
        return result;
    }

    private async Task DespacharAsync(DbContext db, CancellationToken ct)
    {
        for (var rodada = 0; rodada < MaximoDeRodadas; rodada++)
        {
            var eventos = Pendentes(db);
            if (eventos.Count == 0) return;

            foreach (var evento in eventos)
            {
                var invocador = Invocadores.GetOrAdd(evento.GetType(),
                    t => Invocar.MakeGenericMethod(t).CreateDelegate<Func<IServiceProvider, IDomainEvent, CancellationToken, Task>>());
                await invocador(servicos, evento, ct);
            }
        }

        if (Pendentes(db).Count > 0)
            throw new InvalidOperationException($"Eventos de domínio ainda pendentes depois de {MaximoDeRodadas} rodadas (laço entre handlers?).");
    }

    /// <summary>Recolhe e limpa: cada evento é despachado uma vez, mesmo que o SaveChanges seja repetido.</summary>
    private static List<IDomainEvent> Pendentes(DbContext db)
    {
        var eventos = new List<IDomainEvent>();
        foreach (var entrada in db.ChangeTracker.Entries<Entidade>())
        {
            if (entrada.Entity.DomainEvents.Count == 0) continue;
            eventos.AddRange(entrada.Entity.DomainEvents);
            entrada.Entity.ClearDomainEvents();
        }

        return eventos;
    }

    private static async Task InvocarAsync<TEvento>(IServiceProvider servicos, IDomainEvent evento, CancellationToken ct)
        where TEvento : IDomainEvent
    {
        foreach (var handler in servicos.GetServices<IDomainEventHandler<TEvento>>())
            await handler.HandleAsync((TEvento)evento, ct);
    }
}

public static class EventosDeDominioExtensions
{
    /// <summary>Liga o despacho de eventos de domínio no DbContext do escopo (registro real e hosts de teste).</summary>
    public static DbContextOptionsBuilder ComEventosDeDominio(this DbContextOptionsBuilder options, IServiceProvider escopo) =>
        options.AddInterceptors(escopo.GetRequiredService<DespachanteDeEventosDeDominio>());
}
