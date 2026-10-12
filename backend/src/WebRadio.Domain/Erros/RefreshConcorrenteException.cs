namespace WebRadio.Domain.Erros;

/// <summary>
/// Refresh recusado porque outra aba já rotacionou o sucessor dentro da janela de 10 s (S-M10). Não é credencial inválida:
/// a sessão está viva e o cookie novo já foi gravado pela outra aba, então o 401 NÃO pode apagar o cookie.
/// </summary>
public sealed class RefreshConcorrenteException() : DomainException("Refresh concorrente: use o cookie mais recente.");
