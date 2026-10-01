using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using Serilog.Core;
using Serilog.Events;

namespace WebRadio.Api.Infra;

/// <summary>
/// Destructuring policy do Serilog (05 §5.3): propriedades e chaves de dicionário chamadas *Password*, *Senha*,
/// *Token*, *Key*, *Pepper*, *Secret*, *Authorization*, *Refresh*, *Cookie*, *Hash* e *Email* (sem PII em log)
/// saem como "***" ao logar com <c>{@Obj}</c>. Vale SÓ para <c>{@Obj}</c>: com <c>{Obj}</c> o Serilog usa
/// ToString() e esta policy não roda — por isso os requests sensíveis redefinem o ToString (teste de arquitetura).
/// </summary>
public sealed class MascaraSegredosPolicy : IDestructuringPolicy
{
    private static readonly string[] Sensiveis =
        ["password", "senha", "token", "key", "pepper", "secret", "authorization", "refresh", "cookie", "hash", "email"];

    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> Propriedades = new();

    public static bool EhSensivel(string nome) => Sensiveis.Any(s => nome.Contains(s, StringComparison.OrdinalIgnoreCase));

    public bool TryDestructure(object value, ILogEventPropertyValueFactory factory, out LogEventPropertyValue result)
    {
        result = null!;
        if (value is IDictionary dicionario)
        {
            var itens = new List<KeyValuePair<ScalarValue, LogEventPropertyValue>>();
            foreach (DictionaryEntry e in dicionario)
            {
                var chave = e.Key.ToString() ?? "";
                var v = EhSensivel(chave) ? "***" : e.Value;
                itens.Add(new(new ScalarValue(chave), factory.CreatePropertyValue(v, destructureObjects: true)));
            }

            result = new DictionaryValue(itens);
            return true;
        }

        var tipo = value.GetType();
        var anonimo = tipo.Name.StartsWith("<>f__AnonymousType", StringComparison.Ordinal);
        // Só tipos de aplicação (e anônimos): primitivos, strings, coleções e tipos do framework seguem o padrão.
        if (tipo.IsPrimitive || tipo.IsEnum || value is string || value is IEnumerable
            || (!anonimo && (tipo.Namespace is null || tipo.Namespace.StartsWith("System", StringComparison.Ordinal)
                             || tipo.Namespace.StartsWith("Microsoft", StringComparison.Ordinal))))
            return false;

        var props = Propriedades.GetOrAdd(tipo, t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetIndexParameters().Length == 0).ToArray());
        var lista = new List<LogEventProperty>(props.Length);
        foreach (var p in props)
        {
            var valor = EhSensivel(p.Name) ? "***" : p.GetValue(value);
            lista.Add(new LogEventProperty(p.Name, factory.CreatePropertyValue(valor, destructureObjects: true)));
        }

        result = new StructureValue(lista, anonimo ? null : tipo.Name);
        return true;
    }
}
