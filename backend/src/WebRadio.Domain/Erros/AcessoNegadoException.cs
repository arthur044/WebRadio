namespace WebRadio.Domain.Erros;

/// <summary>
/// O usuário está autenticado e o papel serve para a rota, mas o recurso não é dele (ex.: um Locutor encerrando o programa de
/// outro). Mapeia para 403 <c>/erros/acesso-negado</c>.
/// </summary>
public sealed class AcessoNegadoException(string detalhe) : DomainException(detalhe);
