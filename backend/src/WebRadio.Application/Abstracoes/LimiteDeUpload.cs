namespace WebRadio.Application.Abstracoes;

/// <summary>
/// Limite de envio de imagem de divulgação: 30 por hora por Admin, contando TENTATIVAS (inclusive as recusadas) e ANTES de ler o corpo
/// (07, E3-F06). Com o Redis fora o limite cai para a memória desta instância (nunca fica sem limite).
/// </summary>
public interface ILimiteDeUploadDeImagem
{
    public const int PorHora = 30;

    /// <summary>Registra a tentativa. <c>null</c> = pode seguir; senão, quanto falta para a janela de 1 h virar.</summary>
    Task<TimeSpan?> RegistrarTentativaAsync(Guid adminId, CancellationToken ct);
}
