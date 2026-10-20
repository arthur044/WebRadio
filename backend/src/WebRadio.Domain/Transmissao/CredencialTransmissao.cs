using WebRadio.Domain.Common;

namespace WebRadio.Domain.Transmissao;

/// <summary>
/// schema seg (S-A08, 06 "Credencial de transmissão"). O <see cref="Identificador"/> é o "usuário" que o locutor digita no source
/// client: aleatório e opaco, nunca o Id da linha nem o do usuário. O segredo nunca é guardado: só o SHA-256(salt ‖ ASCII).
/// "Uma ativa por locutor" é do índice filtrado no banco; a geração revoga a anterior na mesma transação (Application).
/// </summary>
public sealed class CredencialTransmissao : Entidade
{
    public const int TamanhoHashBytes = 32;
    public const int TamanhoSaltBytes = 16;

    public Guid UsuarioId { get; private set; }

    public Guid Identificador { get; private set; }

    public byte[] SegredoHash { get; private set; } = [];

    public byte[] Salt { get; private set; } = [];

    public DateTime CriadaEmUtc { get; private set; }

    public DateTime? RevogadaEmUtc { get; private set; }

    public DateTime? UltimoUsoEmUtc { get; private set; }

    public bool EstaAtiva => RevogadaEmUtc is null;

    private CredencialTransmissao()
    {
    }

    public CredencialTransmissao(Guid id, Guid usuarioId, Guid identificador, byte[] segredoHash, byte[] salt, DateTime criadaEmUtc)
        : base(id)
    {
        if (usuarioId == Guid.Empty) throw new ArgumentException("O usuário não pode ser Guid.Empty.", nameof(usuarioId));
        if (identificador == Guid.Empty) throw new ArgumentException("O identificador não pode ser Guid.Empty.", nameof(identificador));
        if (segredoHash.Length != TamanhoHashBytes)
            throw new ArgumentException($"SegredoHash precisa ter {TamanhoHashBytes} bytes (SHA-256).", nameof(segredoHash));
        if (salt.Length != TamanhoSaltBytes)
            throw new ArgumentException($"Salt precisa ter {TamanhoSaltBytes} bytes.", nameof(salt));
        UtcGuard.Exigir(criadaEmUtc, nameof(criadaEmUtc));

        UsuarioId = usuarioId;
        Identificador = identificador;
        SegredoHash = (byte[])segredoHash.Clone();
        Salt = (byte[])salt.Clone();
        CriadaEmUtc = criadaEmUtc;
    }

    // Nunca imprimir hash, salt nem identificador.
    public override string ToString() => $"CredencialTransmissao {Id}";

    public void Revogar(DateTime agora)
    {
        UtcGuard.Exigir(agora, nameof(agora));

        if (!EstaAtiva)
        {
            return; // idempotente: revogar duas vezes não é erro.
        }

        RevogadaEmUtc = agora;
    }

    public void RegistrarUso(DateTime agora)
    {
        UtcGuard.Exigir(agora, nameof(agora));
        UltimoUsoEmUtc = agora;
    }
}
