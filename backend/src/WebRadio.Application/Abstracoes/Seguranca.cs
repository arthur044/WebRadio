using System.Net;
using WebRadio.Domain.Usuarios;

namespace WebRadio.Application.Abstracoes;

public interface IClock
{
    DateTime UtcNow { get; }
}

public enum ResultadoSenha
{
    Falhou,
    Ok,

    /// <summary>Senha certa, mas o hash foi gerado com parâmetros antigos: regravar com os atuais no login.</summary>
    OkRehash,
}

/// <summary>PBKDF2 do ASP.NET Core Identity (<c>PasswordHasher&lt;Usuario&gt;</c>), escondido atrás de uma porta.</summary>
public interface ISenhaHasher
{
    string Hash(string senha);

    ResultadoSenha Verificar(string hash, string senha);

    /// <summary>Uma verificação completa contra um hash fictício, para igualar o tempo quando não há conta (S-A12).</summary>
    void VerificarFicticio(string senha);
}

public sealed record AccessToken(string Token, DateTime ExpiraEmUtc)
{
    // O nome "Token" cai na máscara do Serilog; o ToString não imprime o JWT nem com {Obj} sem @.
    public override string ToString() => nameof(AccessToken);
}

/// <summary>Valor opaco do refresh (vai no cookie) e o SHA-256 dele (o que fica no banco).</summary>
public sealed record RefreshOpaco(string Token, byte[] Hash)
{
    public override string ToString() => nameof(RefreshOpaco);
}

public interface IEmissorDeTokens
{
    AccessToken EmitirAccessToken(Usuario usuario);

    /// <summary>256 bits de <c>RandomNumberGenerator</c>; o banco guarda só o SHA-256 (05 §6).</summary>
    RefreshOpaco NovoRefresh();

    TimeSpan DuracaoDoRefresh { get; }
}

/// <summary>HMAC-SHA256 do IP com o <c>Seguranca__Pepper</c>: o IP em claro nunca é guardado nem logado.</summary>
public interface IIpHasher
{
    /// <summary>
    /// HMAC do IP; IPv6 entra pelo prefixo /64 (a mesma granularidade dos limiters): um cliente controla um /64
    /// inteiro e não pode escapar do bloqueio trocando de endereço dentro dele.
    /// </summary>
    byte[] Hash(IPAddress? ip);

    /// <summary>Identificador curto e estável de um valor sensível para log (HMAC com o pepper, 8 hex), sem expô-lo.</summary>
    string Pseudonimo(string valor);
}

public sealed record AvaliacaoDeLogin(bool Bloqueado, TimeSpan Atraso);

/// <summary>
/// D21: bloqueio de 15 min por (conta, IpHash) após 5 falhas, e por conta só alerta + atraso progressivo (≥ 50/h).
/// A implementação em memória é o modo degradado do S-A12; a de Redis entra com a F09.
/// </summary>
public interface ILoginThrottle
{
    Task<AvaliacaoDeLogin> AvaliarAsync(string emailNormalizado, byte[] ipHash, CancellationToken ct);

    Task RegistrarFalhaAsync(string emailNormalizado, byte[] ipHash, CancellationToken ct);

    Task LimparAsync(string emailNormalizado, byte[] ipHash, CancellationToken ct);
}

public interface ISenhasVazadas
{
    bool EhVazada(string senha);
}
