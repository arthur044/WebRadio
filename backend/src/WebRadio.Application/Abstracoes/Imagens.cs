namespace WebRadio.Application.Abstracoes;

public enum MotivoDaImagemRecusada
{
    /// <summary>Não é JPEG nem PNG pelos primeiros bytes (WebP, GIF, SVG, HTML renomeado, vazio...).</summary>
    FormatoNaoAceito,

    /// <summary>Mais de um quadro (PNG animado).</summary>
    Animada,

    /// <summary>Algum lado acima de <see cref="LimitesDaImagem.LadoMaximo"/> px (recusa, não redimensiona).</summary>
    Dimensoes,

    /// <summary>Os magic bytes dizem JPEG/PNG, mas o cabeçalho não é legível.</summary>
    Ilegivel,
}

public sealed record CabecalhoDaImagem(bool Aceita, MotivoDaImagemRecusada? Motivo, int Largura = 0, int Altura = 0)
{
    public static CabecalhoDaImagem Recusada(MotivoDaImagemRecusada motivo) => new(false, motivo);
}

public static class LimitesDaImagem
{
    /// <summary>2 MiB: o arquivo inteiro (E3-F06).</summary>
    public const long TamanhoMaximoBytes = 2 * 1024 * 1024;

    public const int LadoMaximo = 4096;

    /// <summary>Teto de tempo do decode + encode (o Skia não é cancelável: ao estourar, a requisição recebe 503 e a tarefa é abandonada).</summary>
    public static readonly TimeSpan TempoMaximo = TimeSpan.FromSeconds(10);
}

/// <summary>
/// Passo 1 do upload (S-B10): confere formato e dimensões só pelo CABEÇALHO, sem decodificar pixel. Separado do <see cref="IRecodificadorDeImagem"/>
/// para os testes provarem que, nos casos de 400, o decodificador não é chamado.
/// </summary>
public interface IValidadorDeCabecalhoDeImagem
{
    CabecalhoDaImagem Validar(byte[] bytes);
}

/// <summary>
/// Passo 2: decodifica, aplica a orientação do EXIF, descarta todo metadado e codifica WebP (qualidade 85). Uma de cada vez
/// (<c>SemaphoreSlim(1)</c>); sem vaga ou acima do teto de tempo → <see cref="Domain.Erros.ServidorOcupadoException"/> (503).
/// </summary>
public interface IRecodificadorDeImagem
{
    /// <exception cref="ImagemIlegivelException">O cabeçalho passou mas os pixels não decodificam (arquivo truncado ou corrompido): vira 400.</exception>
    Task<byte[]> RecodificarParaWebpAsync(byte[] origem, CancellationToken ct);
}

/// <summary>A imagem tem cabeçalho de JPEG/PNG mas não decodifica (truncada ou corrompida). É erro do arquivo, não do servidor: o envio responde 400.</summary>
public sealed class ImagemIlegivelException(string message, Exception? inner = null) : Exception(message, inner);
