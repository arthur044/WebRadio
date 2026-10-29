using Microsoft.Extensions.Logging.Abstractions;
using WebRadio.Application.Abstracoes;
using WebRadio.Application.Features.Divulgacoes;
using WebRadio.Application.Tests.Auth;
using WebRadio.Infrastructure.Persistencia;
using static WebRadio.Application.Tests.Auth.AuthFixture;

namespace WebRadio.Application.Tests.Divulgacoes;

/// <summary>O storage de mentira: objetos em memória, mais a falha de <c>DeleteAsync</c> sob demanda.</summary>
public sealed class StorageDeTeste : IObjectStorage
{
    public Dictionary<string, (byte[] Bytes, string ContentType)> Objetos { get; } = [];

    public bool FalharAoApagar { get; set; }

    public List<string> Apagados { get; } = [];

    public void Existe(string chave) => Objetos[chave] = ([1], "image/webp");

    public Task PutAsync(string bucket, string chave, Stream conteudo, long tamanhoBytes, string contentType, CancellationToken ct = default)
    {
        using var m = new MemoryStream();
        conteudo.CopyTo(m);
        Objetos[$"{bucket}/{chave}"] = (m.ToArray(), contentType);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string bucket, string chave, CancellationToken ct = default)
    {
        if (FalharAoApagar) throw new InvalidOperationException("storage fora");
        Apagados.Add(chave);
        Objetos.Remove($"{bucket}/{chave}");
        return Task.CompletedTask;
    }

    public Task<InfoObjeto?> HeadAsync(string bucket, string chave, CancellationToken ct = default) =>
        Task.FromResult(Objetos.TryGetValue($"{bucket}/{chave}", out var o) ? new InfoObjeto(o.Bytes.Length, o.ContentType, "etag") : null);

    public Task<IReadOnlyList<ObjetoListado>> ListarAsync(string bucket, string prefixo, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ObjetoListado>>([.. Objetos.Keys.Where(k => k.StartsWith($"{bucket}/{prefixo}")).Select(k => new ObjetoListado(k[(bucket.Length + 1)..], DateTime.UtcNow))]);

    public string UrlPublica(string bucket, string chave) => $"http://localhost:8080/storage/{bucket}/{chave}";

    public Task<PostPolicyAssinada> CriarPostPolicyAsync(string bucket, string chave, string contentType, long tamanhoMaximoBytes, TimeSpan validade, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<string> UrlGetPreAssinadaAsync(string bucket, string chave, TimeSpan validade, string? contentType = null, string? contentDisposition = null, CancellationToken ct = default) => throw new NotSupportedException();

    public Task MoverAsync(string bucketOrigem, string chaveOrigem, string bucketDestino, string chaveDestino, string etagEsperado, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<bool> VerificarAsync(CancellationToken ct = default) => Task.FromResult(true);
}

/// <summary>Banco em memória + relógio falso + storage falso + os handlers de divulgação montados como na Api (E3-F05).</summary>
public sealed class DivulgacoesFixture
{
    public DivulgacoesFixture()
    {
        Db = NovoDb();
        Relogio = new RelogioFalso(Agora);
    }

    public RadioDbContext Db { get; }

    public RelogioFalso Relogio { get; }

    public StorageDeTeste Storage { get; } = new();

    public Guid Admin { get; } = Guid.NewGuid();

    public CriarDivulgacaoHandler Criar => new(Db, Relogio, Storage);

    public EditarDivulgacaoHandler Editar => new(Db, Relogio, NullLogger<EditarDivulgacaoHandler>.Instance, Storage);

    public RemoverDivulgacaoHandler Remover => new(Db, NullLogger<RemoverDivulgacaoHandler>.Instance, Storage);

    public DivulgacoesAtivasHandler Ativas => new(Db, Relogio, Storage);

    public ListarDivulgacoesHandler Listar => new(Db, Storage);

    public DivulgacaoPorIdHandler PorId => new(Db, Storage);

    public string NovaImagem()
    {
        var chave = ChaveDeImagem.Nova();
        Storage.Objetos[$"publico/{chave}"] = ([1, 2, 3], "image/webp");
        return chave;
    }

    public Task<DivulgacaoDto> CriarAsync(string titulo = "Promo", string? imagem = null, string? link = null, byte prioridade = 50,
        DateTime? inicio = null, DateTime? fim = null, bool ativo = true) =>
        Criar.Handle(new CriarDivulgacaoCommand(Admin, titulo, "Mensagem", imagem, link, prioridade, inicio, fim, ativo), default);
}
