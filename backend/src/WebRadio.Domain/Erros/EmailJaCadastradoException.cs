namespace WebRadio.Domain.Erros;

public sealed class EmailJaCadastradoException() : DomainException("Já existe um cadastro com este e-mail.");
