namespace WebRadio.Application.Abstracoes;

/// <summary>
/// Captura da fila de sanitização (E4-F05, Fluxo C passo 3): pega UMA mídia <c>Pendente</c> da mais antiga para a mais nova e a deixa <c>EmAnalise</c>, com o lease e a tentativa
/// +1. No SQL Server é a CTE <c>SELECT TOP (1) ... WITH (UPDLOCK, READPAST, ROWLOCK) ORDER BY DataUploadUtc</c> + <c>UPDATE</c> com <c>OUTPUT</c>: dois Workers nunca pegam o mesmo item.
/// </summary>
public interface ICapturaDeMidia
{
    /// <summary>O id da mídia capturada, ou <c>null</c> se a fila está vazia.</summary>
    Task<Guid?> CapturarProximaAsync(CancellationToken ct = default);
}

/// <summary>
/// "Health Degraded" do Worker (E4-D7, E4-D13): o antivírus está fora ou com a base velha, então a fila não anda. O Worker não tem HTTP: o marcador é um log <c>Error</c> e um arquivo
/// que o operador (e o smoke) enxergam; o healthcheck do contêiner continua sendo o heartbeat.
/// </summary>
public interface IMarcadorDeDegradacao
{
    void Degradado(string motivo);

    void Normal();
}
