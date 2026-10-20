using System.Reflection;
using Minio;
using Minio.DataModel;
using Minio.DataModel.Args;
using Minio.Exceptions;
using WebRadio.Application.Abstracoes;
using WebRadio.Infrastructure.Armazenamento;
using WebRadio.Infrastructure.Tests.Suporte;
using Xunit;

namespace WebRadio.Infrastructure.Tests;

/// <summary>
/// S-M02 sem servidor: um <see cref="IMinioClient"/> falso que IGNORA a condição de ETag (o cenário que motivou a defesa em
/// profundidade: SDK, proxy ou outro S3 descartando o header). O comportamento do MinIO de verdade está em
/// <see cref="MinioObjectStorageRealTests"/>.
/// </summary>
public class MinioObjectStorageMoverTests
{
    private const string Esperado = "11111111111111111111111111111111";
    private const string Trocado = "22222222222222222222222222222222";

    private static MinioObjectStorage Novo(ClienteFalso cliente) =>
        new(cliente.Proxy, cliente.Proxy, new StorageOptions("minio:9000", "http://localhost:8080/storage", "acesso-teste", "segredo-de-teste-123456"),
            new RelogioFalso(new DateTime(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc)));

    [Fact]
    public async Task Copia_que_ignorou_a_condicao_e_apagada_do_destino_e_a_origem_fica()
    {
        var cliente = new ClienteFalso { EtagDoDestino = Trocado };

        await Assert.ThrowsAsync<EtagDivergenteException>(() => Novo(cliente).MoverAsync("quarentena", "a", "midia", "b", Esperado));

        Assert.Equal(["midia/b"], cliente.Removidos);
    }

    [Fact]
    public async Task Resposta_412_engolida_pelo_SDK_deixa_o_destino_ausente_e_nada_e_apagado()
    {
        var cliente = new ClienteFalso { EtagDoDestino = null };

        await Assert.ThrowsAsync<EtagDivergenteException>(() => Novo(cliente).MoverAsync("quarentena", "a", "midia", "b", Esperado));

        Assert.Empty(cliente.Removidos);
    }

    [Fact]
    public async Task Minio_7_lanca_o_412_e_ele_vira_EtagDivergente_sem_apagar_nada()
    {
        var cliente = new ClienteFalso { EtagDoDestino = Esperado, CopiaLancaPrecondicao = true };

        var e = await Assert.ThrowsAsync<EtagDivergenteException>(() => Novo(cliente).MoverAsync("quarentena", "a", "midia", "b", Esperado));

        Assert.IsType<PreconditionFailedException>(e.InnerException);
        Assert.Empty(cliente.Removidos);
    }

    [Fact]
    public async Task Falha_na_limpeza_do_destino_e_sinalizada_na_excecao()
    {
        var cliente = new ClienteFalso { EtagDoDestino = Trocado, FalharAoRemover = true };

        var e = await Assert.ThrowsAsync<EtagDivergenteException>(() => Novo(cliente).MoverAsync("quarentena", "a", "midia", "b", Esperado));

        Assert.Contains("limpeza do destino", e.Message, StringComparison.Ordinal);
        Assert.NotNull(e.InnerException);
    }

    [Fact]
    public async Task Copia_conferida_apaga_so_a_origem_e_manda_o_header_de_condicao()
    {
        var cliente = new ClienteFalso { EtagDoDestino = Esperado };

        await Novo(cliente).MoverAsync("quarentena", "a", "midia", "b", "\"" + Esperado + "\"");

        Assert.Equal(["quarentena/a"], cliente.Removidos);
        Assert.Equal(Esperado, cliente.HeaderIfMatch);
    }

    /// <summary>Só as três operações que o MoverAsync usa; qualquer outra chamada é erro do teste.</summary>
    public sealed class ClienteFalso
    {
        public string? EtagDoDestino { get; init; }

        public bool FalharAoRemover { get; init; }

        public bool CopiaLancaPrecondicao { get; init; }

        public List<string> Removidos { get; } = [];

        public string? HeaderIfMatch { get; private set; }

        public IMinioClient Proxy
        {
            get
            {
                var proxy = DispatchProxy.Create<IMinioClient, Despachante>();
                ((Despachante)(object)proxy).Alvo = this;
                return proxy;
            }
        }

        // As propriedades dos Args do SDK são internas/protegidas: lê por reflexão, subindo a hierarquia.
        private static T? Ler<T>(object alvo, string nome)
        {
            for (var tipo = alvo.GetType(); tipo is not null; tipo = tipo.BaseType)
            {
                var p = tipo.GetProperty(nome, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (p is not null) return (T?)p.GetValue(alvo);
            }

            throw new MissingMemberException(alvo.GetType().Name, nome);
        }

        public object? Tratar(string metodo, object?[] args)
        {
            switch (metodo)
            {
                case nameof(IMinioClient.CopyObjectAsync):
                    HeaderIfMatch = Ler<Dictionary<string, string>>(args[0]!, "Headers")?.GetValueOrDefault("x-amz-copy-source-if-match");
                    return CopiaLancaPrecondicao
                        ? Task.FromException(new PreconditionFailedException("condição não atendida"))
                        : Task.CompletedTask;

                case nameof(IMinioClient.StatObjectAsync):
                    // Só o destino é conferido; a origem não é consultada pelo MoverAsync.
                    if (EtagDoDestino is null) return Task.FromException<ObjectStat>(new ObjectNotFoundException("destino ausente"));
                    return Task.FromResult(ObjectStat.FromResponseHeaders("b", new Dictionary<string, string>
                    {
                        ["etag"] = "\"" + EtagDoDestino + "\"",
                        ["content-length"] = "100",
                        ["content-type"] = "audio/mpeg",
                    }));

                case nameof(IMinioClient.RemoveObjectAsync):
                    var remover = (RemoveObjectArgs)args[0]!;
                    if (FalharAoRemover) return Task.FromException(new InvalidOperationException("sem permissão"));
                    Removidos.Add(Ler<string>(remover, "BucketName") + "/" + Ler<string>(remover, "ObjectName"));
                    return Task.CompletedTask;

                case nameof(IDisposable.Dispose):
                    return null;

                default:
                    throw new NotSupportedException("Chamada inesperada ao cliente falso: " + metodo);
            }
        }
    }

    public class Despachante : DispatchProxy
    {
        public ClienteFalso? Alvo { get; set; }

        protected override object? Invoke(MethodInfo? metodo, object?[]? args) => Alvo!.Tratar(metodo!.Name, args ?? []);
    }
}
