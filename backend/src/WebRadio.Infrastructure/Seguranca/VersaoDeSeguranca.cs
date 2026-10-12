using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using WebRadio.Application.Abstracoes;
using WebRadio.Infrastructure.Persistencia;

namespace WebRadio.Infrastructure.Seguranca;

/// <summary>
/// Consultada em toda requisição autenticada, então fica 15 s em cache por instância (00 §segurança). A chave é o id de um
/// token com assinatura válida: o cache não cresce com entrada inventada por quem não tem token.
/// </summary>
public sealed class VersaoDeSeguranca(RadioDbContext db, IMemoryCache cache) : IVersaoDeSeguranca
{
    public static readonly TimeSpan DuracaoDoCache = TimeSpan.FromSeconds(15);

    /// <summary>Usuário inexistente ou inativo (não há versão válida): também fica em cache, como valor próprio.</summary>
    private const int SemUsuario = -1;

    public async Task<int?> AtualAsync(Guid usuarioId, CancellationToken ct)
    {
        var chave = Chave(usuarioId);
        if (!cache.TryGetValue(chave, out int versao))
        {
            versao = await db.Usuarios.AsNoTracking()
                .Where(u => u.Id == usuarioId && u.Ativo)
                .Select(u => (int?)u.VersaoSeguranca)
                .FirstOrDefaultAsync(ct) ?? SemUsuario;
            cache.Set(chave, versao, DuracaoDoCache);
        }

        return versao == SemUsuario ? null : versao;
    }

    public void Esquecer(Guid usuarioId) => cache.Remove(Chave(usuarioId));

    private static string Chave(Guid usuarioId) => $"sv:{usuarioId:N}";
}
