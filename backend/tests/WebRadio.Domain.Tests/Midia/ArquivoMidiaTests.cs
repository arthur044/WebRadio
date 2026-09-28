using WebRadio.Domain.Enums;
using WebRadio.Domain.Erros;
using WebRadio.Domain.Midia;
using Xunit;

namespace WebRadio.Domain.Tests.Midia;

public class ArquivoMidiaTests
{
    private static readonly DateTime Agora = new(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc);
    private static readonly LimiteMidia LimiteMusica = new(TamanhoMaximoBytes: 250_000_000, DuracaoMinima: TimeSpan.FromSeconds(30), DuracaoMaxima: TimeSpan.FromMinutes(20));

    private static byte[] NovoHash() => new byte[32];

    private static ArquivoMidia Criar(TipoMidia tipo = TipoMidia.Musica, string? titulo = "Faixa", long tamanho = 1000) =>
        new(Guid.NewGuid(), "original.mp3", titulo, "Artista", tipo, "audio/mpeg", tamanho, Guid.NewGuid(), Agora);

    private static ArquivoMidia CriarEmAnalise(long tamanho = 1000)
    {
        var midia = Criar(tamanho: tamanho);
        midia.ConcluirUpload(tamanho, "etag-1", Agora.AddMinutes(1));
        midia.IniciarAnalise(Agora.AddMinutes(2));
        return midia;
    }

    [Fact]
    public void Construtor_comeca_AguardandoUpload_no_bucket_quarentena_com_chave_gerada()
    {
        var midia = Criar();
        Assert.Equal(StatusSanitizacao.AguardandoUpload, midia.StatusSanitizacao);
        Assert.Equal("quarentena", midia.Bucket);
        Assert.Equal($"{Agora:yyyy}/{Agora:MM}/{midia.Id}", midia.ChaveStorage);
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
        var midia = Criar(tamanho: 2048);

        midia.ConcluirUpload(2048, "etag-abc", Agora.AddMinutes(1));

        Assert.Equal(StatusSanitizacao.Pendente, midia.StatusSanitizacao);
        Assert.Equal("etag-abc", midia.EtagUpload);
        Assert.Equal(2048, midia.TamanhoBytes);
    }

    [Fact]
    public void ConcluirUpload_com_tamanho_maior_que_o_declarado_lanca_TransicaoInvalida()
    {
        var midia = Criar(tamanho: 1000);
        Assert.Throws<TransicaoInvalidaException>(() => midia.ConcluirUpload(1001, "etag", Agora.AddMinutes(1)));
    }

    [Fact]
    public void ConcluirUpload_apos_a_janela_de_1h_lanca_TransicaoInvalida()
    {
        var midia = Criar(tamanho: 1000);
        Assert.Throws<TransicaoInvalidaException>(() => midia.ConcluirUpload(1000, "etag", Agora.AddHours(1).AddSeconds(1)));
    }

    [Fact]
    public void ConcluirUpload_duas_vezes_lanca_TransicaoInvalida()
    {
        var midia = Criar(tamanho: 1000);
        midia.ConcluirUpload(1000, "etag", Agora.AddMinutes(1));

        Assert.Throws<TransicaoInvalidaException>(() => midia.ConcluirUpload(1000, "etag-2", Agora.AddMinutes(2)));
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
    public void LiberarLeaseExpirado_apos_o_timeout_volta_para_Pendente_antes_da_3a_tentativa()
    {
        var midia = CriarEmAnalise(); // tentativa 1
        midia.LiberarLeaseExpirado(Agora.AddMinutes(10), TimeSpan.FromMinutes(5));

        Assert.Equal(StatusSanitizacao.Pendente, midia.StatusSanitizacao);
        Assert.Null(midia.EmAnaliseDesdeUtc);
    }

    [Fact]
    public void LiberarLeaseExpirado_na_3a_tentativa_rejeita_H1()
    {
        // H1: um lease vencido conta como falha, com a mesma regra de 3 tentativas — nunca fica preso
        // em EmAnalise nem deixa TentativasSanitizacao estourar a CK 0-10.
        var midia = Criar(tamanho: 1000);
        midia.ConcluirUpload(1000, "etag", Agora.AddMinutes(1));

        midia.IniciarAnalise(Agora.AddMinutes(2)); // tentativa 1
        midia.LiberarLeaseExpirado(Agora.AddMinutes(10), TimeSpan.FromMinutes(5));
        midia.IniciarAnalise(Agora.AddMinutes(11)); // tentativa 2
        midia.LiberarLeaseExpirado(Agora.AddMinutes(20), TimeSpan.FromMinutes(5));
        midia.IniciarAnalise(Agora.AddMinutes(21)); // tentativa 3
        midia.LiberarLeaseExpirado(Agora.AddMinutes(30), TimeSpan.FromMinutes(5));

        Assert.Equal(StatusSanitizacao.Rejeitado, midia.StatusSanitizacao);
        Assert.Equal("falha de processamento", midia.MotivoRejeicao);
        Assert.Equal(3, midia.TentativasSanitizacao);
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
    public void Rejeitar_a_partir_de_estado_terminal_lanca_TransicaoInvalida()
    {
        var midia = CriarEmAnalise();
        midia.Rejeitar("motivo", Agora.AddMinutes(3));

        Assert.Throws<TransicaoInvalidaException>(() => midia.Rejeitar("outro motivo", Agora.AddMinutes(4)));
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
    public void RegistrarFalhaProcessamento_fora_de_EmAnalise_lanca_TransicaoInvalida()
    {
        var midia = Criar();
        Assert.Throws<TransicaoInvalidaException>(() => midia.RegistrarFalhaProcessamento(Agora.AddMinutes(1)));
    }

    [Fact]
    public void MarcarQuarentena_a_partir_de_EmAnalise_funciona()
    {
        var midia = CriarEmAnalise();

        midia.MarcarQuarentena("malware detectado", Agora.AddMinutes(3));

        Assert.Equal(StatusSanitizacao.Quarentena, midia.StatusSanitizacao);
    }

    [Fact]
    public void MarcarQuarentena_fora_de_EmAnalise_lanca_TransicaoInvalida()
    {
        var midia = Criar();
        Assert.Throws<TransicaoInvalidaException>(() => midia.MarcarQuarentena("motivo", Agora.AddMinutes(1)));
    }

    [Fact]
    public void Aprovar_dentro_do_limite_funciona_e_move_para_bucket_midia_com_extensao()
    {
        var midia = CriarEmAnalise(tamanho: 1000);
        var hash = NovoHash();
        var chaveAntesDeAprovar = midia.ChaveStorage;

        midia.Aprovar("audio/mpeg", hash, 120m, 900, ".mp3", LimiteMusica, Agora.AddMinutes(3));

        Assert.Equal(StatusSanitizacao.Aprovado, midia.StatusSanitizacao);
        Assert.Equal("midia", midia.Bucket);
        Assert.NotNull(midia.SanitizadoEmUtc);
        Assert.Equal(120m, midia.DuracaoSegundos);
        Assert.Equal("audio/mpeg", midia.MimeType);
        Assert.Equal(hash, midia.HashSHA256);
        Assert.Equal(900, midia.TamanhoBytes);
        Assert.Equal(chaveAntesDeAprovar + ".mp3", midia.ChaveStorage);
    }

    [Fact]
    public void Aprovar_com_duracao_fora_do_limite_rejeita_em_vez_de_lancar_H2()
    {
        var midia = CriarEmAnalise();

        midia.Aprovar("audio/mpeg", NovoHash(), 5m, 900, ".mp3", LimiteMusica, Agora.AddMinutes(3)); // < 30s mínimo

        Assert.Equal(StatusSanitizacao.Rejeitado, midia.StatusSanitizacao);
        Assert.Contains("duração", midia.MotivoRejeicao);
    }

    [Fact]
    public void Aprovar_com_tamanho_acima_do_limite_rejeita_em_vez_de_lancar_H2()
    {
        var midia = CriarEmAnalise();

        midia.Aprovar("audio/mpeg", NovoHash(), 120m, 300_000_000, ".mp3", LimiteMusica, Agora.AddMinutes(3));

        Assert.Equal(StatusSanitizacao.Rejeitado, midia.StatusSanitizacao);
        Assert.Contains("tamanho", midia.MotivoRejeicao);
    }

    [Fact]
    public void Aprovar_com_mimeType_fora_da_lista_permitida_rejeita_L3()
    {
        var midia = CriarEmAnalise();

        midia.Aprovar("video/mp4", NovoHash(), 120m, 900, ".mp3", LimiteMusica, Agora.AddMinutes(3));

        Assert.Equal(StatusSanitizacao.Rejeitado, midia.StatusSanitizacao);
        Assert.Contains("não permitido", midia.MotivoRejeicao);
    }

    [Fact]
    public void Aprovar_fora_de_EmAnalise_lanca_TransicaoInvalida()
    {
        var midia = Criar();
        Assert.Throws<TransicaoInvalidaException>(() =>
            midia.Aprovar("audio/mpeg", NovoHash(), 120m, 900, ".mp3", LimiteMusica, Agora.AddMinutes(1)));
    }

    [Fact]
    public void RegistrarHashOriginal_nao_muda_status()
    {
        var midia = CriarEmAnalise();
        var hash = NovoHash();

        midia.RegistrarHashOriginal(hash);

        Assert.Equal(hash, midia.HashOriginalSHA256);
        Assert.Equal(StatusSanitizacao.EmAnalise, midia.StatusSanitizacao);
    }

    [Fact]
    public void RegistrarHashOriginal_copia_o_array_M6()
    {
        var midia = CriarEmAnalise();
        var hash = NovoHash();

        midia.RegistrarHashOriginal(hash);
        hash[0] = 0xFF;

        Assert.NotEqual((byte)0xFF, midia.HashOriginalSHA256![0]);
    }

    [Fact]
    public void RegistrarHashOriginal_chamado_duas_vezes_lanca_TransicaoInvalida_M5()
    {
        var midia = CriarEmAnalise();
        midia.RegistrarHashOriginal(NovoHash());

        Assert.Throws<TransicaoInvalidaException>(() => midia.RegistrarHashOriginal(NovoHash()));
    }

    [Fact]
    public void RegistrarHashOriginal_fora_de_EmAnalise_lanca_TransicaoInvalida_M5()
    {
        var midia = Criar();
        Assert.Throws<TransicaoInvalidaException>(() => midia.RegistrarHashOriginal(NovoHash()));
    }

    [Fact]
    public void RegistrarHashOriginal_com_null_lanca_ArgumentNullException_L6()
    {
        var midia = CriarEmAnalise();
        Assert.Throws<ArgumentNullException>(() => midia.RegistrarHashOriginal(null!));
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
