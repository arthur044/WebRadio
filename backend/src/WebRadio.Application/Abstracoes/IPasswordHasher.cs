namespace WebRadio.Application.Abstracoes;

public interface IPasswordHasher
{
    string Hash(string senha);

    bool Verificar(string hashArmazenado, string senha);

    /// <summary>Gasta o tempo de uma verificação real contra um hash fictício (e-mail inexistente, conta bloqueada; D21).</summary>
    void VerificarFicticio(string senha);
}
