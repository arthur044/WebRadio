using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WebRadio.Application.Abstracoes;
using WebRadio.Application.Features.Midias;
using WebRadio.Application.Tests.Auth;
using WebRadio.Application.Tests.Pedidos;
using WebRadio.Domain.Enums;
using WebRadio.Domain.Midia;
using WebRadio.Infrastructure.Persistencia;
using WebRadio.Infrastructure.Seguranca;
using static WebRadio.Application.Tests.Auth.AuthFixture;

namespace WebRadio.Application.Tests.Midias;

/// <summary>Storage de mentira para a biblioteca: registra a policy pedida e responde o HEAD de um mapa.</summary>
public sealed class StorageDeMidia : IObjectStorage
{
    public List<(string Bucket, string Chave, string ContentType, long Maximo, TimeSpan Validade)> Policies { get; } = [];

    public Dictionary<string, InfoObjeto> Objetos { get; } = [];

    public bool FalharNaPolicy { get; set; }

    public List<(string Bucket, string Chave, TimeSpan Validade, string? ContentType, string? Disposition)> Previews { get; } = [];

    public Task<PostPolicyAssinada> CriarPostPolicyAsync(string bucket, string chave, string contentType, long tamanhoMaximoBytes, TimeSpan validade, CancellationToken ct = default)
    {
        if (FalharNaPolicy) throw new InvalidOperationException("storage fora");
        Policies.Add((bucket, chave, contentType, tamanhoMaximoBytes, validade));
        return Task.FromResult(new PostPolicyAssinada($"http://localhost/storage/{bucket}", new Dictionary<string, string> { ["key"] = chave, ["Content-Type"] = contentType }, DateTime.UtcNow.Add(validade)));
    }

    public Task<InfoObjeto?> HeadAsync(string bucket, string chave, CancellationToken ct = default) =>
        Task.FromResult(Objetos.TryGetValue($"{bucket}/{chave}", out var o) ? o : null);

    public Task<string> UrlGetPreAssinadaAsync(string bucket, string chave, TimeSpan validade, string? contentType = null, string? contentDisposition = null, CancellationToken ct = default)
    {
        Previews.Add((bucket, chave, validade, contentType, contentDisposition));
        return Task.FromResult($"http://localhost/storage/{bucket}/{chave}?X-Amz-Signature=abc");
    }

    public Task MoverAsync(string bucketOrigem, string chaveOrigem, string bucketDestino, string chaveDestino, string etagEsperado, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<bool> VerificarAsync(CancellationToken ct = default) => Task.FromResult(true);

    public Task PutAsync(string bucket, string chave, Stream conteudo, long tamanhoBytes, string contentType, CancellationToken ct = default) => throw new NotSupportedException();

    public Task DeleteAsync(string bucket, string chave, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<IReadOnlyList<ObjetoListado>> ListarAsync(string bucket, string prefixo, CancellationToken ct = default) => throw new NotSupportedException();

    public string UrlPublica(string bucket, string chave) => throw new NotSupportedException();
}

/// <summary>Banco em memória + relógio falso + Redis que cai + os handlers de upload montados como na Api (E4-F02).</summary>
public sealed class MidiasFixture
{
    public MidiasFixture(MidiaOptions? opcoes = null)
    {
        Db = NovoDb();
        Relogio = new RelogioFalso(Agora);
        Opcoes = opcoes ?? new MidiaOptions();
        Redis = new RedisQueCai(Relogio);
        Hasher = new IpHasher(PedidosFixture.Pepper);
        Limite = new LimiteDeUploadDeMidia(Redis, Hasher, Relogio, Microsoft.Extensions.Options.Options.Create(Opcoes), NullLogger<LimiteDeUploadDeMidia>.Instance);
    }

    public RadioDbContext Db { get; }

    public RelogioFalso Relogio { get; }

    public MidiaOptions Opcoes { get; }

    public RedisQueCai Redis { get; }

    public IpHasher Hasher { get; }

    public StorageDeMidia Storage { get; } = new();

    public TransacaoSemTrava Transacao { get; } = new();

    public LimiteDeUploadDeMidia Limite { get; }

    public Guid Usuario { get; } = Guid.NewGuid();

    public IniciarUploadHandler Iniciar => new(Db, Transacao, Relogio, Hasher, Limite, Microsoft.Extensions.Options.Options.Create(Opcoes), Storage);

    public ConcluirUploadHandler Concluir => new(Db, Relogio, Storage);

    public ListaDeLogs Logs { get; } = new();

    public ListarMidiasHandler Listar => new(Db);

    public MidiaPorIdHandler PorId => new(Db);

    public PreviewDaMidiaHandler Preview => new(Db, Relogio, Storage);

    public EditarMidiaHandler Editar => new(Db);

    public ExcluirMidiaHandler Excluir => new(Db, Logs.Logger<ExcluirMidiaHandler>());

    /// <summary>Uma mídia <c>Aprovado</c> direto no banco, com os dois hashes (para a lista, o preview e a exclusão).</summary>
    public ArquivoMidia SemearAprovada(string? titulo = "Aurora", string? artista = "Banda", TipoMidia tipo = TipoMidia.Musica, Guid? usuario = null, string nome = "aurora.mp3", bool ativo = true)
    {
        var m = new ArquivoMidia(Guid.CreateVersion7(), nome, titulo, artista, tipo, "audio/mpeg", 1000, usuario ?? Usuario, Relogio.UtcNow);
        m.ConcluirUpload(1000, "etag", Relogio.UtcNow);
        m.IniciarAnalise(Relogio.UtcNow);
        m.RegistrarHashOriginal(Enumerable.Repeat((byte)0xAB, 32).ToArray());
        m.Aprovar("audio/mpeg", Enumerable.Repeat((byte)0xCD, 32).ToArray(), tipo == TipoMidia.Musica ? 200m : 20m, 900, ".mp3", Opcoes.Limite(tipo), Relogio.UtcNow);
        if (!ativo) m.Desativar();
        Db.ArquivosMidia.Add(m);
        Db.SaveChanges();
        return m;
    }

    public IniciarUploadCommand Comando(Guid? usuario = null, string nome = "vinheta.mp3", TipoMidia tipo = TipoMidia.Vinheta, string mime = "audio/mpeg",
        long tamanho = 1_000_000, string? titulo = null, string? artista = null)
        => new(usuario ?? Usuario, nome, titulo, artista, tipo, mime, tamanho);

    /// <summary>Uma mídia direto no banco, no status pedido (para os limites de abertos e de 20/h).</summary>
    public ArquivoMidia Semear(StatusSanitizacao status, Guid? usuario = null, DateTime? criadaEm = null)
    {
        var quando = criadaEm ?? Relogio.UtcNow;
        var m = new ArquivoMidia(Guid.CreateVersion7(), "a.mp3", null, null, TipoMidia.Vinheta, "audio/mpeg", 1000, usuario ?? Usuario, quando);
        if (status >= StatusSanitizacao.Pendente) m.ConcluirUpload(1000, "etag", quando);
        if (status == StatusSanitizacao.EmAnalise) m.IniciarAnalise(quando);
        if (status == StatusSanitizacao.Rejeitado)
        {
            m.IniciarAnalise(quando);
            m.Rejeitar("x", quando);
        }

        Db.ArquivosMidia.Add(m);
        Db.SaveChanges();
        return m;
    }
}

/// <summary>Logger que guarda as mensagens formatadas (a auditoria da exclusão sai pelo log estruturado).</summary>
public sealed class ListaDeLogs
{
    public List<string> Linhas { get; } = [];

    public Microsoft.Extensions.Logging.ILogger<T> Logger<T>() => new Captura<T>(Linhas);

    private sealed class Captura<T>(List<string> linhas) : Microsoft.Extensions.Logging.ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => linhas.Add(formatter(state, exception));
    }
}
