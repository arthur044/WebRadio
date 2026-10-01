using System.Reflection;
using WebRadio.Application.Abstracoes;

namespace WebRadio.Infrastructure.Seguranca;

/// <summary>
/// Lista local de senhas vazadas/comuns (S-B02): top 100 mil do SecLists (MIT) mais entradas locais, embutida em
/// <c>senhas-vazadas.txt</c> (uma por linha, <c>#</c> comenta). Só entram na memória as de 12+ caracteres, o mínimo
/// da política: as menores já são recusadas pelo tamanho. A comparação ignora maiúsculas.
/// </summary>
public sealed class SenhasVazadas : ISenhasVazadas
{
    private const int MinimoDaPolitica = 12;
    private readonly HashSet<string> _lista;

    public SenhasVazadas()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("senhas-vazadas.txt")
                           ?? throw new InvalidOperationException("Recurso senhas-vazadas.txt não embutido.");
        using var leitor = new StreamReader(stream);
        _lista = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (leitor.ReadLine() is { } linha)
            if (linha.Length > 0 && linha[0] != '#' && linha.Trim() is { Length: >= MinimoDaPolitica } senha) _lista.Add(senha);
    }

    public bool EhVazada(string senha) => _lista.Contains(senha);
}
