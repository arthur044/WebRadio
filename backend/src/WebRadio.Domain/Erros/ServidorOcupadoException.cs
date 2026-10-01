namespace WebRadio.Domain.Erros;

/// <summary>
/// Não houve vaga para uma operação cara de CPU (hash de senha) dentro do tempo de espera. Mapeia para 503 com
/// Retry-After; depende só da carga do servidor, nunca da conta, então não serve de oráculo (S-A12).
/// </summary>
public sealed class ServidorOcupadoException() : DomainException("Servidor ocupado. Tente novamente em instantes.");
