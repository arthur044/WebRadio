using WebRadio.Domain.Enums;
using WebRadio.Domain.Erros;
using WebRadio.Domain.Midia;
using Xunit;

namespace WebRadio.Domain.Tests.Midia;

public class ArquivoMidiaTests
{
    private static readonly DateTime Agora = new(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc);
    private static readonly byte[] Hash32 = new byte[32];
    private static readonly LimiteMidia LimiteMusica = new(TamanhoMaximoBytes: 250_000_000, DuracaoMinima: TimeSpan.FromSeconds(30), DuracaoMaxima: TimeSpan.FromMinutes(20));

    private static ArquivoMidia Criar(TipoMidia tipo = TipoMidia.Musica, string? titulo = "Faixa", long tamanho = 1000) =>
        new(Guid.NewGuid(), "original.mp3", titulo, "Artista", tipo, "2026/09/abc", "audio/mpeg", tamanho, Guid.NewGuid(), Agora);

    private static ArquivoMidia CriarEmAnalise(long tamanho = 1000)
    {
        var midia = Criar(tamanho: tamanho);
        midia.ConcluirUpload(tamanho, "etag-1", Agora.AddMinutes(1));
        midia.IniciarAnalise(Agora.AddMinutes(2));
        return midia;
    }

    [Fact]
    public void Construtor_comeca_AguardandoUpload_no_bucket_quarentena()
    {
        var midia = Criar();
        Assert.Equal(StatusSanitizacao.AguardandoUpload, midia.StatusSanitizacao);
        Assert.Equal("quarentena", midia.Bucket);
    }

    [Fact]
    public void Construtor_exige_titulo_para_Musica()
    {
        // ThrowIfNullOrWhiteSpace lança ArgumentNullException para null e ArgumentException para vazio/espaços;
        // ThrowsAny cobre a hierarquia (ArgumentNullException : ArgumentException).
        Assert.ThrowsAny<ArgumentException>(() => Criar(tipo: TipoMidia.Musica, titulo: null));
    }

    [Fact]
    public void Construtor_nao_exige_titulo_para_Vinheta()
    {
        var midia = Criar(tipo: TipoMidia.Vinheta, titulo: null);
        Assert.Null(midia.Titulo);
    }

    [Fact]
    public void ConcluirUpload_grava_etag_e_avanca_para_Pendente()
    {
        var midia = Criar();

        midia.ConcluirUpload(2048, "etag-abc", Agora.AddMinutes(1));

        Assert.Equal(StatusSanitizacao.Pendente, midia.StatusSanitizacao);
        Assert.Equal("etag-abc", midia.EtagUpload);
        Assert.Equal(2048, midia.TamanhoBytes);
    }

    [Fact]
    public void IniciarAnalise_incrementa_tentativas_e_grava_lease()
    {
        var midia = CriarEmAnalise();

        Assert.Equal(StatusSanitizacao.EmAnalise, midia.StatusSanitizacao);
        Assert.Equal(1, midia.TentativasSanitizacao);
        Assert.NotNull(midia.EmAnaliseDesdeUtc);
    }

    [Fact]
    public void IniciarAnalise_a_partir_de_AguardandoUpload_lanca_TransicaoInvalida()
    {
        var midia = Criar();
        Assert.Throws<TransicaoInvalidaException>(() => midia.IniciarAnalise(Agora.AddMinutes(1)));
    }

    [Fact]
    public void LiberarLeaseExpirado_antes_do_timeout_lanca_TransicaoInvalida()
    {
        var midia = CriarEmAnalise();
        Assert.Throws<TransicaoInvalidaException>(() => midia.LiberarLeaseExpirado(Agora.AddMinutes(2).AddSeconds(30), TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void LiberarLeaseExpirado_apos_o_timeout_volta_para_Pendente()
    {
        var midia = CriarEmAnalise();
        midia.LiberarLeaseExpirado(Agora.AddMinutes(10), TimeSpan.FromMinutes(5));

        Assert.Equal(StatusSanitizacao.Pendente, midia.StatusSanitizacao);
        Assert.Null(midia.EmAnaliseDesdeUtc);
    }

    [Fact]
    public void Rejeitar_a_partir_de_EmAnalise_funciona()
    {
        var midia = CriarEmAnalise();

        midia.Rejeitar("formato inválido", Agora.AddMinutes(3));

        Assert.Equal(StatusSanitizacao.Rejeitado, midia.StatusSanitizacao);
        Assert.Equal("formato inválido", midia.MotivoRejeicao);
    }

    [Fact]
    public void RegistrarFalhaProcessamento_antes_da_3a_tentativa_volta_para_Pendente()
    {
        var midia = CriarEmAnalise(); // tentativa 1

        midia.RegistrarFalhaProcessamento(Agora.AddMinutes(3));

        Assert.Equal(StatusSanitizacao.Pendente, midia.StatusSanitizacao);
        Assert.Equal(1, midia.TentativasSanitizacao);
    }

    [Fact]
    public void RegistrarFalhaProcessamento_na_3a_tentativa_rejeita()
    {
        var midia = Criar();
        midia.ConcluirUpload(1000, "etag", Agora.AddMinutes(1));

        for (var tentativa = 1; tentativa <= 3; tentativa++)
        {
            midia.IniciarAnalise(Agora.AddMinutes(tentativa));
            midia.RegistrarFalhaProcessamento(Agora.AddMinutes(tentativa).AddSeconds(30));
        }

        Assert.Equal(StatusSanitizacao.Rejeitado, midia.StatusSanitizacao);
        Assert.Equal("falha de processamento", midia.MotivoRejeicao);
        Assert.Equal(3, midia.TentativasSanitizacao);
    }

    [Fact]
    public void MarcarQuarentena_a_partir_de_EmAnalise_funciona()
    {
        var midia = CriarEmAnalise();

        midia.MarcarQuarentena("malware detectado", Agora.AddMinutes(3));

        Assert.Equal(StatusSanitizacao.Quarentena, midia.StatusSanitizacao);
    }

    [Fact]
    public void Aprovar_dentro_do_limite_funciona_e_move_para_bucket_midia()
    {
        var midia = CriarEmAnalise(tamanho: 1000);

        midia.Aprovar("audio/mpeg", Hash32, 120m, LimiteMusica, Agora.AddMinutes(3));

        Assert.Equal(StatusSanitizacao.Aprovado, midia.StatusSanitizacao);
        Assert.Equal("midia", midia.Bucket);
        Assert.NotNull(midia.SanitizadoEmUtc);
        Assert.Equal(120m, midia.DuracaoSegundos);
    }

    [Fact]
    public void Aprovar_com_duracao_fora_do_limite_lanca_ArgumentOutOfRange()
    {
        var midia = CriarEmAnalise();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            midia.Aprovar("audio/mpeg", Hash32, 5m, LimiteMusica, Agora.AddMinutes(3))); // < 30s mínimo
    }

    [Fact]
    public void Aprovar_com_tamanho_acima_do_limite_lanca_ArgumentOutOfRange()
    {
        var midia = CriarEmAnalise(tamanho: 300_000_000); // acima do limite de Musica

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            midia.Aprovar("audio/mpeg", Hash32, 120m, LimiteMusica, Agora.AddMinutes(3)));
    }

    [Fact]
    public void RegistrarHashOriginal_nao_muda_status()
    {
        var midia = CriarEmAnalise();

        midia.RegistrarHashOriginal(Hash32);

        Assert.Equal(Hash32, midia.HashOriginalSHA256);
        Assert.Equal(StatusSanitizacao.EmAnalise, midia.StatusSanitizacao);
    }

    [Fact]
    public void Desativar_nao_muda_StatusSanitizacao()
    {
        var midia = Criar();

        midia.Desativar();

        Assert.False(midia.Ativo);
        Assert.Equal(StatusSanitizacao.AguardandoUpload, midia.StatusSanitizacao);
    }
}
