namespace WebRadio.Application.Abstracoes;

/// <summary>
/// Versão de segurança atual do usuário (claim <c>sv</c>), com cache de 15 s por instância: é consultada em TODA
/// requisição autenticada. <see cref="Esquecer"/> faz a mudança valer na hora nesta instância; nas outras, em até 15 s.
/// </summary>
public interface IVersaoDeSeguranca
{
    /// <summary>null quando o usuário não existe ou está inativo.</summary>
    Task<int?> AtualAsync(Guid usuarioId, CancellationToken ct);

    void Esquecer(Guid usuarioId);
}
