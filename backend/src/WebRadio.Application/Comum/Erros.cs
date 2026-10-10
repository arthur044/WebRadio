namespace WebRadio.Application.Comum;

/// <summary>01-dominio.md §3: credencial errada, usuário inativo, conta bloqueada ou e-mail inexistente. Sempre a mesma mensagem (D21).</summary>
public sealed class CredenciaisInvalidasException : Exception
{
    public CredenciaisInvalidasException()
        : base("E-mail ou senha inválidos.")
    {
    }
}

/// <summary>Refresh recusado por corrida entre abas (nao e credencial invalida): o 401 NAO pode apagar o cookie, que ja e o novo.</summary>
public sealed class RefreshConcorrenteException : Exception
{
    public RefreshConcorrenteException()
        : base("Refresh concorrente: use o cookie mais recente.")
    {
    }
}

public sealed class NaoEncontradoException(string recurso) : Exception($"{recurso} não encontrado.")
{
    public string Recurso { get; } = recurso;
}

public sealed class EmailJaCadastradoException : Exception
{
    public EmailJaCadastradoException()
        : base("Já existe uma conta com esse e-mail.")
    {
    }
}

/// <summary>01-dominio.md §3: RowVersion divergente (If-Match) → 412. A Infrastructure traduz a concorrência do EF para este erro.</summary>
public sealed class VersaoDesatualizadaException : Exception
{
    public VersaoDesatualizadaException()
        : base("O recurso foi alterado por outra pessoa. Recarregue e tente de novo.")
    {
    }
}

/// <summary>01-dominio.md §3: throttle de pedido → 429 com Retry-After.</summary>
public sealed class MuitosPedidosException(TimeSpan tentarNovamenteEm) : Exception("Muitos pedidos em pouco tempo.")
{
    public TimeSpan TentarNovamenteEm { get; } = tentarNovamenteEm;
}
