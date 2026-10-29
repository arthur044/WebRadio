namespace WebRadio.Domain.Erros;

/// <summary>A imagem já está ligada a outra divulgação (07, E3-D5): o <c>DELETE</c> de uma apagaria a imagem da outra. Mapeia para 409 <c>/erros/imagem-em-uso</c>.</summary>
public sealed class ImagemEmUsoException() : DomainException("Esta imagem já está em uso por outra divulgação.");
