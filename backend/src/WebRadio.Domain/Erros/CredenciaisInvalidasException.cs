namespace WebRadio.Domain.Erros;

/// <summary>
/// Login falhou: senha errada, e-mail inexistente, conta inativa ou bloqueada. A mensagem é única de propósito:
/// qualquer diferença revelaria quais e-mails existem (D21 / S-A12).
/// </summary>
public sealed class CredenciaisInvalidasException() : DomainException("Credenciais inválidas.");
