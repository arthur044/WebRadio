using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using WebRadio.Application.Abstracoes;
using WebRadio.Domain.Erros;
using WebRadio.Domain.Usuarios;

namespace WebRadio.Infrastructure.Seguranca;

/// <summary>
/// PBKDF2-HMAC-SHA512 do Identity (formato V3) com custo FIXO: não depende do padrão da versão do framework.
/// Hashes gerados com menos iterações verificam como <see cref="ResultadoSenha.OkRehash"/> e são regravados.
/// </summary>
public sealed class SenhaHasher : ISenhaHasher
{
    /// <summary>OWASP (2023): ≥ 210.000 para PBKDF2-HMAC-SHA512. Medido: ~100 ms por verificação num núcleo moderno.</summary>
    public const int Iteracoes = 210_000;

    private readonly PasswordHasher<Usuario> _hasher =
        new(Options.Create(new PasswordHasherOptions { IterationCount = Iteracoes, CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV3 }));

    // Hash de uma senha aleatória descartada, com as MESMAS opções: gasta o mesmo PBKDF2 quando não há conta (S-A12).
    private readonly string _hashFicticio;

    // Semáforo GLOBAL (R1 do Sentinel): todo PBKDF2 (login, registro, rehash, fictício) passa por aqui. Sem isso,
    // N requisições simultâneas de IPs distintos ocupariam N núcleos e a API (cpus limitado no compose) pararia.
    private readonly SemaphoreSlim _vagas;
    private readonly TimeSpan _esperaMaxima;
    private int _emUso;
    private int _pico;

    /// <summary>Maior número de hashes simultâneos já observado (diagnóstico e testes).</summary>
    public int PicoDeConcorrencia => Volatile.Read(ref _pico);

    /// <summary>Padrão: uma vaga por CPU visível (o .NET respeita o limite de CPU do cgroup) e 5 s de espera.</summary>
    public SenhaHasher() : this(Math.Max(1, Environment.ProcessorCount), TimeSpan.FromSeconds(5))
    {
    }

    public SenhaHasher(int limiteConcorrencia, TimeSpan esperaMaxima)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limiteConcorrencia, 1);
        _vagas = new SemaphoreSlim(limiteConcorrencia, limiteConcorrencia);
        _esperaMaxima = esperaMaxima;
        _hashFicticio = _hasher.HashPassword(null!, Guid.NewGuid().ToString("N"));
    }

    public string Hash(string senha) => ComVaga(() => _hasher.HashPassword(null!, senha));

    public ResultadoSenha Verificar(string hash, string senha)
        => ComVaga(() => _hasher.VerifyHashedPassword(null!, hash, senha)) switch
        {
            PasswordVerificationResult.Success => ResultadoSenha.Ok,
            PasswordVerificationResult.SuccessRehashNeeded => ResultadoSenha.OkRehash,
            _ => ResultadoSenha.Falhou,
        };

    public void VerificarFicticio(string senha) => ComVaga(() => _hasher.VerifyHashedPassword(null!, _hashFicticio, senha));

    private T ComVaga<T>(Func<T> trabalho)
    {
        // Espera limitada: fila infinita de threads bloqueadas esgotaria o ThreadPool. Estourou → 503, não 401.
        if (!_vagas.Wait(_esperaMaxima)) throw new ServidorOcupadoException();
        try
        {
            var agora = Interlocked.Increment(ref _emUso);
            int pico;
            while (agora > (pico = Volatile.Read(ref _pico)) && Interlocked.CompareExchange(ref _pico, agora, pico) != pico) { }
            return trabalho();
        }
        finally
        {
            Interlocked.Decrement(ref _emUso);
            _vagas.Release();
        }
    }
}
