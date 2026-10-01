using System.Reflection;
using WebRadio.Application.Abstracoes;

namespace WebRadio.Infrastructure.Seguranca;

/// <summary>
/// Lista local de senhas comuns (S-B02), carregada do recurso embutido <c>senhas-vazadas.txt</c>, uma por linha,
/// sem diferenciar maiúsculas. A lista atual é curta (só entradas com ≥ 12 caracteres, o mínimo da política);
/// trocar pelo top 100 mil é só substituir o arquivo.
/// </summary>
public sealed class SenhasVazadas : ISenhasVazadas
{
    private readonly HashSet<string> _lista;

    public SenhasVazadas()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("senhas-vazadas.txt")
                           ?? throw new InvalidOperationException("Recurso senhas-vazadas.txt não embutido.");
        using var leitor = new StreamReader(stream);
        _lista = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (leitor.ReadLine() is { } linha)
            if (linha.Length > 0 && linha[0] != '#') _lista.Add(linha.Trim());
    }

    public bool EhVazada(string senha) => _lista.Contains(senha);
}
