using FluentValidation;
using WebRadio.Application.Abstracoes;
using WebRadio.Application.Features.Divulgacoes;
using Xunit;

namespace WebRadio.Application.Tests.Divulgacoes;

/// <summary>07 E3-F06: nos 400 o decodificador NÃO é chamado (dublê na costura); no 201 grava WebP com o Content-Type do servidor.</summary>
public class EnviarImagemDeDivulgacaoTests
{
    private sealed class ValidadorFalso(CabecalhoDaImagem resposta) : IValidadorDeCabecalhoDeImagem
    {
        public int Chamadas { get; private set; }

        public CabecalhoDaImagem Validar(byte[] bytes)
        {
            Chamadas++;
            return resposta;
        }
    }

    private sealed class RecodificadorFalso : IRecodificadorDeImagem
    {
        public int Chamadas { get; private set; }

        public Task<byte[]> RecodificarParaWebpAsync(byte[] origem, CancellationToken ct)
        {
            Chamadas++;
            return Task.FromResult<byte[]>([.. "RIFF"u8, 0, 0, 0, 0, .. "WEBP"u8]);
        }
    }

    private readonly StorageDeTeste _storage = new();
    private readonly RecodificadorFalso _recodificador = new();

    private EnviarImagemDeDivulgacaoHandler Novo(CabecalhoDaImagem cabecalho) => new(new ValidadorFalso(cabecalho), _recodificador, _storage);

    [Theory]
    [InlineData(MotivoDaImagemRecusada.FormatoNaoAceito)]
    [InlineData(MotivoDaImagemRecusada.Animada)]
    [InlineData(MotivoDaImagemRecusada.Dimensoes)]
    [InlineData(MotivoDaImagemRecusada.Ilegivel)]
    public async Task Imagem_recusada_pelo_cabecalho_e_400_sem_chamar_o_decodificador_nem_gravar(MotivoDaImagemRecusada motivo)
    {
        var erro = await Assert.ThrowsAsync<ValidationException>(() =>
            Novo(CabecalhoDaImagem.Recusada(motivo)).Handle(new EnviarImagemDeDivulgacaoCommand([1, 2, 3]), default));

        Assert.Equal("file", Assert.Single(erro.Errors).PropertyName);
        Assert.Equal(0, _recodificador.Chamadas);
        Assert.Empty(_storage.Objetos);
    }

    private sealed class RecodificadorQueNaoLe : IRecodificadorDeImagem
    {
        public Task<byte[]> RecodificarParaWebpAsync(byte[] origem, CancellationToken ct) => throw new ImagemIlegivelException("truncada");
    }

    [Fact]
    public async Task Arquivo_truncado_que_passou_no_cabecalho_mas_nao_decodifica_e_400_e_nada_vai_para_o_bucket()
    {
        var h = new EnviarImagemDeDivulgacaoHandler(new ValidadorFalso(new CabecalhoDaImagem(true, null, 10, 10)), new RecodificadorQueNaoLe(), _storage);

        var erro = await Assert.ThrowsAsync<ValidationException>(() => h.Handle(new EnviarImagemDeDivulgacaoCommand([1, 2, 3]), default));

        Assert.Equal("file", Assert.Single(erro.Errors).PropertyName);
        Assert.Empty(_storage.Objetos);
    }

    [Fact]
    public async Task Imagem_aceita_vira_WebP_gravado_em_publico_com_chave_nova_e_Content_Type_do_servidor()
    {
        var r = await Novo(new CabecalhoDaImagem(true, null, 10, 10)).Handle(new EnviarImagemDeDivulgacaoCommand([1, 2, 3]), default);

        Assert.Matches(@"^divulgacoes/[0-9a-f]{32}\.webp\z", r.Chave);
        Assert.Equal($"http://localhost:8080/storage/publico/{r.Chave}", r.ImagemUrl);
        var (bytes, contentType) = _storage.Objetos[$"publico/{r.Chave}"];
        Assert.Equal("image/webp", contentType);
        Assert.Equal("WEBP", System.Text.Encoding.ASCII.GetString(bytes, 8, 4));
        Assert.Equal(1, _recodificador.Chamadas);
    }

    [Fact]
    public async Task Cada_envio_ganha_uma_chave_diferente()
    {
        var h = Novo(new CabecalhoDaImagem(true, null, 10, 10));

        var a = await h.Handle(new EnviarImagemDeDivulgacaoCommand([1]), default);
        var b = await h.Handle(new EnviarImagemDeDivulgacaoCommand([1]), default);

        Assert.NotEqual(a.Chave, b.Chave);
    }

    [Fact]
    public void O_ToString_do_comando_nao_imprime_o_conteudo_do_arquivo()
    {
        Assert.DoesNotContain("1", new EnviarImagemDeDivulgacaoCommand([1, 2, 3]).ToString());
    }
}
