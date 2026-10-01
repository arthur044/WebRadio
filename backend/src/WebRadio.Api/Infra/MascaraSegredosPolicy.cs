using System.Reflection;
using Serilog.Core;
using Serilog.Events;

namespace WebRadio.Api.Infra;

/// <summary>
/// Destructuring policy do Serilog (05 §5.3): propriedades chamadas *Password*, *Senha*, *Token*, *Key*,
/// *Pepper* (e Email, sem PII em log) saem como "***" ao logar objetos com <c>{@Obj}</c>.
/// </summary>
public sealed class MascaraSegredosPolicy : IDestructuringPolicy
{
    private static readonly string[] Sensiveis = ["password", "senha", "token", "key", "pepper", "email"];

    public static bool EhSensivel(string nome) => Sensiveis.Any(s => nome.Contains(s, StringComparison.OrdinalIgnoreCase));

    public bool TryDestructure(object value, ILogEventPropertyValueFactory factory, out LogEventPropertyValue result)
    {
        result = null!;
        var tipo = value.GetType();
        // Só tipos de aplicação: primitivos, strings, coleções e tipos do framework seguem o caminho padrão.
        if (tipo.IsPrimitive || tipo.IsEnum || value is string || value is System.Collections.IEnumerable
            || tipo.Namespace is null || tipo.Namespace.StartsWith("System", StringComparison.Ordinal))
            return false;

        var props = tipo.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.GetIndexParameters().Length == 0);
        var lista = new List<LogEventProperty>();
        foreach (var p in props)
        {
            var valor = EhSensivel(p.Name) ? "***" : p.GetValue(value);
            lista.Add(new LogEventProperty(p.Name, factory.CreatePropertyValue(valor, destructureObjects: true)));
        }

        result = new StructureValue(lista, tipo.Name);
        return true;
    }
}
