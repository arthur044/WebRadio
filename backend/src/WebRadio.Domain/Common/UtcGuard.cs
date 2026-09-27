namespace WebRadio.Domain.Common;

/// <summary>
/// D8: todo instante de negócio trafega em UTC dentro do Domain. O fuso local só existe na exibição (frontend).
/// </summary>
internal static class UtcGuard
{
    public static void Exigir(DateTime valor, string nomeParametro)
    {
        if (valor.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("A data precisa ser UTC (DateTime.Kind == Utc).", nomeParametro);
        }
    }
}
