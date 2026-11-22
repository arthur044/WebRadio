using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using WebRadio.Application.Abstracoes;
using WebRadio.Domain.Enums;
using WebRadio.Domain.Midia;
using WebRadio.Domain.Usuarios;
using WebRadio.Infrastructure.Persistencia;
using WebRadio.Infrastructure.Tests.Suporte;
using WebRadio.Infrastructure.Trabalhos;
using Xunit;

namespace WebRadio.Infrastructure.Tests;

/// <summary>
/// E4-F09 contra SQL Server de verdade (o <c>DELETE</c> condicionado ao status e o <c>ExecuteDelete</c> não existem no InMemory): cancelados, <c>AguardandoUpload</c> vencido, órfãos da
/// quarentena (a cada 30 min) e de <c>midia/</c> com a trava contra apagão. O storage é um dicionário (o comportamento do MinIO e da política do <c>svc-worker</c> tem teste próprio).
/// </summary>
public class LimpezaDeMidiaSqlTests(BancoDeTeste banco) : IClassFixture<BancoDeTeste>
{
    private sealed class StorageEmMemoria : IObjectStorage
    {
        public Dictionary<(string Bucket, string Chave), DateTime> Objetos { get; } = [];

        public bool FalharAListar { get; set; }

        public List<string> Listagens { get; } = [];

        public void Por(string bucket, string chave, DateTime quando) => Objetos[(bucket, chave)] = quando;

        public bool Tem(string bucket, string chave) => Objetos.ContainsKey((bucket, chave));

        public Task<IReadOnlyList<ObjetoListado>> ListarAsync(string bucket, string prefixo, CancellationToken ct = default)
        {
            Listagens.Add($"{bucket}/{prefixo}");
            if (FalharAListar) throw new InvalidOperationException("Access Denied");
            IReadOnlyList<ObjetoListado> r = Objetos.Where(o => o.Key.Bucket == bucket && o.Key.Chave.StartsWith(prefixo, StringComparison.Ordinal))
                .Select(o => new ObjetoListado(o.Key.Chave, o.Value)).ToList();
            return Task.FromResult(r);
        }

        public Task DeleteAsync(string bucket, string chave, CancellationToken ct = default)
        {
            Objetos.Remove((bucket, chave));
            return Task.CompletedTask;
        }

        public Task<PostPolicyAssinada> CriarPostPolicyAsync(string bucket, string chave, string contentType, long tamanhoMaximoBytes, TimeSpan validade, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<InfoObjeto?> HeadAsync(string bucket, string chave, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<string> UrlGetPreAssinadaAsync(string bucket, string chave, TimeSpan validade, string? contentType = null, string? contentDisposition = null, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<long> BaixarAsync(string bucket, string chave, string etagEsperado, long limiteBytes, Stream destino, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<bool> VerificarAsync(CancellationToken ct = default) => throw new NotSupportedException();

        public Task PutAsync(string bucket, string chave, Stream conteudo, long tamanhoBytes, string contentType, CancellationToken ct = default) => throw new NotSupportedException();

        public string UrlPublica(string bucket, string chave) => throw new NotSupportedException();
    }

    private sealed class Marcador : IMarcadorDeDegradacao
    {
        public List<string> Degradados { get; } = [];

        public List<string> Recuperados { get; } = [];

        public void Degradado(string motivo) => Degradados.Add(motivo);

        public void Recuperado(string motivo) => Recuperados.Add(motivo);
    }

    private sealed class Cena
    {
        public StorageEmMemoria Storage { get; } = new();

        public Marcador Marcador { get; } = new();

        public RelogioFalso Relogio { get; } = new(DateTime.UtcNow);

        public required RadioDbContext Db { get; init; }

        public required Guid Dono { get; init; }

        public LimpezaDeMidia Limpeza(RadioDbContext? db = null) =>
            new(db ?? Db, Storage, Relogio, Marcador, new LimpezaDeMidiaOptions { PrimeiroAno = DateTime.UtcNow.Year }, NullLogger<LimpezaDeMidia>.Instance);

        /// <summary>Uma mídia no status pedido, criada <paramref name="idade"/> atrás; <c>Aprovado</c> traz os campos obrigatórios do CHECK.</summary>
        public ArquivoMidia Midia(StatusSanitizacao status, TimeSpan idade, bool cancelada = false)
        {
            var criada = Relogio.UtcNow - idade;
            var m = new ArquivoMidia(Guid.CreateVersion7(), "a.mp3", null, null, TipoMidia.Vinheta, "audio/mpeg", 1000, Dono, criada);
            if (cancelada) m.CancelarEnvio(criada);
            if (status >= StatusSanitizacao.Pendente && !cancelada) m.ConcluirUpload(1000, "etag", criada);
            if (status >= StatusSanitizacao.EmAnalise && !cancelada) m.IniciarAnalise(criada);
            if (status == StatusSanitizacao.Aprovado)
            {
                m.RegistrarHashOriginal(new byte[32]);
                m.Aprovar("audio/mpeg", Guid.NewGuid().ToByteArray().Concat(Guid.NewGuid().ToByteArray()).ToArray(), 20m, 900, ".mp3", new WebRadio.Application.Features.Midias.MidiaOptions().Limite(TipoMidia.Vinheta), criada);
            }
            else if (status == StatusSanitizacao.Quarentena) m.MarcarQuarentena("infectado", criada);
            else if (status == StatusSanitizacao.Rejeitado && !cancelada) m.Rejeitar("x", criada);

            Db.ArquivosMidia.Add(m);
            Db.SaveChanges();
            return m;
        }

        public string ChaveDaQuarentena(ArquivoMidia m) => m.Aprovado() ? m.ChaveStorage[..^".mp3".Length] : m.ChaveStorage;
    }

    private async Task<Cena> NovaCenaAsync()
    {
        var dono = (await new Cenario(banco).CriarUsuarioAsync(Cenario.Email(), Role.Locutor)).Id;
        return new Cena { Db = banco.CriarContexto(), Dono = dono };
    }

    private bool ExisteRegistro(Cena c, Guid id)
    {
        using var db = banco.CriarContexto();
        return db.ArquivosMidia.Any(m => m.Id == id);
    }

    // ---- diária: registros ----

    [SqlFact]
    public async Task Cancelado_ha_mais_de_24h_perde_o_registro_e_o_de_menos_de_24h_fica()
    {
        var c = await NovaCenaAsync();
        var velho = c.Midia(StatusSanitizacao.AguardandoUpload, TimeSpan.FromHours(30), cancelada: true);
        var recente = c.Midia(StatusSanitizacao.AguardandoUpload, TimeSpan.FromHours(2), cancelada: true);
        var rejeitadoComum = c.Midia(StatusSanitizacao.Rejeitado, TimeSpan.FromHours(30)); // Rejeitado por outro motivo: o registro fica (auditoria)

        var r = await c.Limpeza().ExecutarDiariaAsync(default);

        Assert.Equal(1, r.Cancelados);
        Assert.False(ExisteRegistro(c, velho.Id));
        Assert.True(ExisteRegistro(c, recente.Id));
        Assert.True(ExisteRegistro(c, rejeitadoComum.Id));
    }

    [SqlFact]
    public async Task AguardandoUpload_vencido_perde_registro_e_objeto_e_o_nao_vencido_fica()
    {
        var c = await NovaCenaAsync();
        var vencido = c.Midia(StatusSanitizacao.AguardandoUpload, TimeSpan.FromHours(3)); // venceu há 1 h (prazo de 2 h)
        var vivo = c.Midia(StatusSanitizacao.AguardandoUpload, TimeSpan.FromMinutes(10));
        c.Storage.Por("quarentena", vencido.ChaveStorage, c.Relogio.UtcNow.AddHours(-3));
        c.Storage.Por("quarentena", vivo.ChaveStorage, c.Relogio.UtcNow.AddMinutes(-10));

        var r = await c.Limpeza().ExecutarDiariaAsync(default);

        Assert.Equal(1, r.AguardandoVencidos);
        Assert.False(ExisteRegistro(c, vencido.Id));
        Assert.False(c.Storage.Tem("quarentena", vencido.ChaveStorage));
        Assert.True(ExisteRegistro(c, vivo.Id));
        Assert.True(c.Storage.Tem("quarentena", vivo.ChaveStorage));
    }

    [SqlFact]
    public async Task Concluir_que_venceu_a_corrida_deixa_o_registro_e_o_objeto_em_paz()
    {
        var c = await NovaCenaAsync();
        var m = c.Midia(StatusSanitizacao.AguardandoUpload, TimeSpan.FromHours(3));
        c.Storage.Por("quarentena", m.ChaveStorage, c.Relogio.UtcNow.AddHours(-3));
        // O concluir ganhou: a linha já é Pendente (concluída dentro do prazo, antes de vencer), mesmo com o UploadExpiraEmUtc no passado.
        await using (var outro = banco.CriarContexto())
        {
            var x = await outro.ArquivosMidia.SingleAsync(a => a.Id == m.Id);
            x.ConcluirUpload(1000, "etag", x.DataUploadUtc.AddMinutes(1));
            await outro.SaveChangesAsync();
        }

        var r = await c.Limpeza().ExecutarDiariaAsync(default);

        Assert.Equal(0, r.AguardandoVencidos);
        Assert.True(ExisteRegistro(c, m.Id));
        Assert.True(c.Storage.Tem("quarentena", m.ChaveStorage));
    }

    // ---- órfãos da quarentena (30 min) ----

    [SqlFact]
    public async Task Orfaos_da_quarentena_so_saem_quando_nenhum_registro_ainda_deve_ter_objeto()
    {
        var c = await NovaCenaAsync();
        var antigo = c.Relogio.UtcNow.AddHours(-3);
        var chaves = new Dictionary<string, (ArquivoMidia M, bool Fica)>();
        void Obj(string nome, ArquivoMidia m, bool fica) { c.Storage.Por("quarentena", m.ChaveStorage, antigo); chaves[nome] = (m, fica); }

        Obj("aguardando", c.Midia(StatusSanitizacao.AguardandoUpload, TimeSpan.FromMinutes(30)), true);
        Obj("pendente", c.Midia(StatusSanitizacao.Pendente, TimeSpan.FromHours(3)), true);
        Obj("emAnalise", c.Midia(StatusSanitizacao.EmAnalise, TimeSpan.FromHours(3)), true);
        Obj("quarentenaRecente", c.Midia(StatusSanitizacao.Quarentena, TimeSpan.FromDays(5)), true);
        Obj("quarentena40d", c.Midia(StatusSanitizacao.Quarentena, TimeSpan.FromDays(40)), false);
        Obj("rejeitado", c.Midia(StatusSanitizacao.Rejeitado, TimeSpan.FromHours(3)), false);
        Obj("cancelado", c.Midia(StatusSanitizacao.AguardandoUpload, TimeSpan.FromHours(3), cancelada: true), false);
        // POST refeito na mesma chave depois de Aprovado: o registro é Aprovado, então o objeto na QUARENTENA é órfão
        var aprovado = c.Midia(StatusSanitizacao.Aprovado, TimeSpan.FromHours(3));
        c.Storage.Por("quarentena", aprovado.ChaveStorage[..^".mp3".Length], antigo);
        // sem registro algum, e objeto novo (menos de 1 h) e chave fora do formato
        var semRegistro = $"{c.Relogio.UtcNow.Year}/{c.Relogio.UtcNow.Month:00}/{Guid.NewGuid()}";
        c.Storage.Por("quarentena", semRegistro, antigo);
        var novo = $"{c.Relogio.UtcNow.Year}/{c.Relogio.UtcNow.Month:00}/{Guid.NewGuid()}";
        c.Storage.Por("quarentena", novo, c.Relogio.UtcNow.AddMinutes(-10));
        var estranha = $"{c.Relogio.UtcNow.Year}/estranha.bin";
        c.Storage.Por("quarentena", estranha, antigo);

        var apagados = await c.Limpeza().VarrerOrfasDaQuarentenaAsync(default);

        foreach (var (nome, (m, fica)) in chaves) Assert.True(c.Storage.Tem("quarentena", m.ChaveStorage) == fica, $"{nome}: fica={fica}");
        Assert.False(c.Storage.Tem("quarentena", aprovado.ChaveStorage[..^".mp3".Length]));
        Assert.False(c.Storage.Tem("quarentena", semRegistro));
        Assert.True(c.Storage.Tem("quarentena", novo));
        Assert.True(c.Storage.Tem("quarentena", estranha));
        Assert.Equal(5, apagados); // quarentena40d, rejeitado, cancelado, aprovado refeito, sem registro
        Assert.All(c.Storage.Listagens, l => Assert.Matches(@"^quarentena/\d{4}/$", l)); // só o prefixo de ano, nunca sem prefixo
    }

    // ---- órfãos de midia/ e a trava contra apagão ----

    private static void ObjetoDeMidia(Cena c, ArquivoMidia m, DateTime quando) => c.Storage.Por("midia", m.ChaveStorage, quando);

    [SqlFact]
    public async Task Orfao_de_midia_e_o_objeto_sem_registro_ou_com_registro_nao_Aprovado_com_mais_de_24h()
    {
        var c = await NovaCenaAsync();
        var antigo = c.Relogio.UtcNow.AddDays(-2);
        // 30 aprovadas (viva, intocável) para a trava de 10% não disparar
        for (var i = 0; i < 30; i++) ObjetoDeMidia(c, c.Midia(StatusSanitizacao.Aprovado, TimeSpan.FromDays(3)), antigo);
        var rejeitada = c.Midia(StatusSanitizacao.Rejeitado, TimeSpan.FromDays(3));
        c.Storage.Por("midia", rejeitada.ChaveStorage + ".mp3", antigo);
        var recente = c.Midia(StatusSanitizacao.Rejeitado, TimeSpan.FromDays(3));
        c.Storage.Por("midia", recente.ChaveStorage + ".mp3", c.Relogio.UtcNow.AddHours(-3)); // menos de 24 h: espera
        var estranha = $"{c.Relogio.UtcNow.Year}/outro.txt";
        c.Storage.Por("midia", estranha, antigo);

        var r = await c.Limpeza().ExecutarDiariaAsync(default);

        Assert.False(r.TravaDeApagao);
        Assert.Equal(1, r.OrfaosDeMidia);
        Assert.False(c.Storage.Tem("midia", rejeitada.ChaveStorage + ".mp3"));
        Assert.True(c.Storage.Tem("midia", recente.ChaveStorage + ".mp3"));
        Assert.True(c.Storage.Tem("midia", estranha));
        Assert.Equal(31, c.Storage.Objetos.Count(o => o.Key.Bucket == "midia" && o.Key.Chave.EndsWith(".mp3")));
        Assert.Contains("trava_apagao", c.Marcador.Recuperados);
        Assert.Empty(c.Marcador.Degradados);
    }

    [SqlFact]
    public async Task Banco_vazio_com_o_bucket_cheio_nao_apaga_nada_e_emite_a_trava_contra_apagao()
    {
        var c = await NovaCenaAsync(); // nenhuma mídia no banco
        var antigo = c.Relogio.UtcNow.AddDays(-2);
        for (var i = 0; i < 50; i++) c.Storage.Por("midia", $"{c.Relogio.UtcNow.Year}/{c.Relogio.UtcNow.Month:00}/{Guid.NewGuid()}.mp3", antigo);

        var r = await c.Limpeza().ExecutarDiariaAsync(default);

        Assert.True(r.TravaDeApagao);
        Assert.Equal(0, r.OrfaosDeMidia);
        Assert.Equal(50, c.Storage.Objetos.Count);
        Assert.Equal(["trava_apagao"], c.Marcador.Degradados);
    }

    [SqlFact]
    public async Task Mais_de_20_orfaos_ou_mais_de_10_por_cento_aborta_sem_apagar_e_ate_o_teto_apaga()
    {
        // 100 aprovadas + 21 órfãs: 21 > 20 objetos
        var c = await NovaCenaAsync();
        var antigo = c.Relogio.UtcNow.AddDays(-2);
        for (var i = 0; i < 100; i++) ObjetoDeMidia(c, c.Midia(StatusSanitizacao.Aprovado, TimeSpan.FromDays(3)), antigo);
        for (var i = 0; i < 21; i++) c.Storage.Por("midia", $"{c.Relogio.UtcNow.Year}/{c.Relogio.UtcNow.Month:00}/{Guid.NewGuid()}.mp3", antigo);
        var r = await c.Limpeza().ExecutarDiariaAsync(default);
        Assert.True(r.TravaDeApagao);
        Assert.Equal(121, c.Storage.Objetos.Count);

        // 100 aprovadas + 11 órfãs: 11 de 111 passa de 10%
        var d = await NovaCenaAsync();
        for (var i = 0; i < 100; i++) ObjetoDeMidia(d, d.Midia(StatusSanitizacao.Aprovado, TimeSpan.FromDays(3)), antigo);
        for (var i = 0; i < 12; i++) d.Storage.Por("midia", $"{d.Relogio.UtcNow.Year}/{d.Relogio.UtcNow.Month:00}/{Guid.NewGuid()}.mp3", antigo);
        Assert.True((await d.Limpeza().ExecutarDiariaAsync(default)).TravaDeApagao);

        // 100 aprovadas + 10 órfãs: 10 de 110 (9%) e abaixo de 20: apaga
        var e = await NovaCenaAsync();
        for (var i = 0; i < 100; i++) ObjetoDeMidia(e, e.Midia(StatusSanitizacao.Aprovado, TimeSpan.FromDays(3)), antigo);
        for (var i = 0; i < 10; i++) e.Storage.Por("midia", $"{e.Relogio.UtcNow.Year}/{e.Relogio.UtcNow.Month:00}/{Guid.NewGuid()}.mp3", antigo);
        var ok = await e.Limpeza().ExecutarDiariaAsync(default);
        Assert.False(ok.TravaDeApagao);
        Assert.Equal(10, ok.OrfaosDeMidia);
    }

    [SqlFact]
    public async Task Consulta_ao_banco_que_falha_aborta_a_rodada_de_midia_sem_apagar_e_emite_a_trava()
    {
        var c = await NovaCenaAsync();
        var antigo = c.Relogio.UtcNow.AddDays(-2);
        for (var i = 0; i < 3; i++) c.Storage.Por("midia", $"{c.Relogio.UtcNow.Year}/{c.Relogio.UtcNow.Month:00}/{Guid.NewGuid()}.mp3", antigo);
        var semBanco = new RadioDbContext(new DbContextOptionsBuilder<RadioDbContext>()
            .UseSqlServer("Server=127.0.0.1,1;Database=nao-existe;User Id=x;Password=x;Connect Timeout=2;TrustServerCertificate=true").Options);

        var (apagados, trava) = (await c.Limpeza(semBanco).ExecutarDiariaAsyncSoMidia());

        Assert.True(trava);
        Assert.Equal(0, apagados);
        Assert.Equal(3, c.Storage.Objetos.Count);
        Assert.Equal(["trava_apagao"], c.Marcador.Degradados);
    }

    // ---- #136: piso absoluto da trava de midia/ ----

    private static void Orfaos(Cena c, int quantos, DateTime quando)
    {
        for (var i = 0; i < quantos; i++) c.Storage.Por("midia", $"{c.Relogio.UtcNow.Year}/{c.Relogio.UtcNow.Month:00}/{Guid.NewGuid()}.mp3", quando);
    }

    [SqlFact]
    public async Task Biblioteca_pequena_apaga_ate_o_piso_de_orfaos_mesmo_acima_de_10_por_cento_e_acima_do_piso_aborta()
    {
        var antigo = DateTime.UtcNow.AddDays(-2);

        // 8 aprovadas + 2 órfãs = 20% (antes: aborta para sempre, com o alerta eterno); com o piso, apaga
        var a = await NovaCenaAsync();
        for (var i = 0; i < 8; i++) ObjetoDeMidia(a, a.Midia(StatusSanitizacao.Aprovado, TimeSpan.FromDays(3)), antigo);
        Orfaos(a, 2, antigo);
        var ra = await a.Limpeza().ExecutarDiariaAsync(default);
        Assert.False(ra.TravaDeApagao);
        Assert.Equal(2, ra.OrfaosDeMidia);
        Assert.Equal(8, a.Storage.Objetos.Count);

        // exatamente o piso (5) numa biblioteca de 8: ainda apaga
        var b = await NovaCenaAsync();
        for (var i = 0; i < 8; i++) ObjetoDeMidia(b, b.Midia(StatusSanitizacao.Aprovado, TimeSpan.FromDays(3)), antigo);
        Orfaos(b, LimpezaDeMidiaOptions.PisoDeExclusaoPadrao, antigo);
        Assert.Equal(LimpezaDeMidiaOptions.PisoDeExclusaoPadrao, (await b.Limpeza().ExecutarDiariaAsync(default)).OrfaosDeMidia);

        // piso + 1: aborta sem apagar e emite a trava (um banco restaurado não vira licença para apagar a biblioteca)
        var c = await NovaCenaAsync();
        for (var i = 0; i < 8; i++) ObjetoDeMidia(c, c.Midia(StatusSanitizacao.Aprovado, TimeSpan.FromDays(3)), antigo);
        Orfaos(c, LimpezaDeMidiaOptions.PisoDeExclusaoPadrao + 1, antigo);
        var rc = await c.Limpeza().ExecutarDiariaAsync(default);
        Assert.True(rc.TravaDeApagao);
        Assert.Equal(8 + LimpezaDeMidiaOptions.PisoDeExclusaoPadrao + 1, c.Storage.Objetos.Count);
        Assert.Equal(["trava_apagao"], c.Marcador.Degradados);
    }

    [SqlFact]
    public async Task O_limite_percentual_continua_valendo_acima_do_piso()
    {
        // 100 aprovadas + 11 órfãs = 9,9% de 111 (teto 11): apaga; +1 passa do teto e aborta
        var antigo = DateTime.UtcNow.AddDays(-2);
        var ok = await NovaCenaAsync();
        for (var i = 0; i < 100; i++) ObjetoDeMidia(ok, ok.Midia(StatusSanitizacao.Aprovado, TimeSpan.FromDays(3)), antigo);
        Orfaos(ok, 11, antigo);
        Assert.Equal(11, (await ok.Limpeza().ExecutarDiariaAsync(default)).OrfaosDeMidia);

        var acima = await NovaCenaAsync();
        for (var i = 0; i < 100; i++) ObjetoDeMidia(acima, acima.Midia(StatusSanitizacao.Aprovado, TimeSpan.FromDays(3)), antigo);
        Orfaos(acima, 12, antigo);
        Assert.True((await acima.Limpeza().ExecutarDiariaAsync(default)).TravaDeApagao);
    }

    // ---- #137: a mesma trava na varredura de 30 min da quarentena ----

    private static void ObjetosDaQuarentena(Cena c, int quantos, DateTime quando)
    {
        for (var i = 0; i < quantos; i++) c.Storage.Por("quarentena", $"{c.Relogio.UtcNow.Year}/{c.Relogio.UtcNow.Month:00}/{Guid.NewGuid()}", quando);
    }

    [SqlFact]
    public async Task Varredura_da_quarentena_com_banco_vazio_ou_restaurado_aborta_sem_apagar_e_emite_a_trava()
    {
        var c = await NovaCenaAsync(); // nenhum registro: todo upload em andamento pareceria órfão
        ObjetosDaQuarentena(c, 50, c.Relogio.UtcNow.AddHours(-3));

        var apagados = await c.Limpeza().VarrerOrfasDaQuarentenaAsync(default);

        Assert.Equal(0, apagados);
        Assert.Equal(50, c.Storage.Objetos.Count);
        Assert.Equal(["trava_apagao"], c.Marcador.Degradados);
    }

    [SqlFact]
    public async Task Varredura_da_quarentena_com_a_consulta_ao_banco_falhando_aborta_sem_apagar_e_sem_excecao()
    {
        var c = await NovaCenaAsync();
        ObjetosDaQuarentena(c, 3, c.Relogio.UtcNow.AddHours(-3));
        var semBanco = new RadioDbContext(new DbContextOptionsBuilder<RadioDbContext>()
            .UseSqlServer("Server=127.0.0.1,1;Database=nao-existe;User Id=x;Password=x;Connect Timeout=2;TrustServerCertificate=true").Options);

        var apagados = await c.Limpeza(semBanco).VarrerOrfasDaQuarentenaAsync(default);

        Assert.Equal(0, apagados);
        Assert.Equal(3, c.Storage.Objetos.Count);
        Assert.Equal(["trava_apagao"], c.Marcador.Degradados);
    }

    [SqlFact]
    public async Task Varredura_da_quarentena_apaga_ate_o_piso_e_ate_10_por_cento_acima_dele_e_o_resto_aborta()
    {
        var antigo = DateTime.UtcNow.AddHours(-3);

        // 8 uploads vivos (Pendente) + 2 órfãos (20%): dentro do piso, apaga
        var a = await NovaCenaAsync();
        for (var i = 0; i < 8; i++) a.Storage.Por("quarentena", a.Midia(StatusSanitizacao.Pendente, TimeSpan.FromHours(3)).ChaveStorage, antigo);
        ObjetosDaQuarentena(a, 2, antigo);
        Assert.Equal(2, await a.Limpeza().VarrerOrfasDaQuarentenaAsync(default));
        Assert.Empty(a.Marcador.Degradados);

        // 100 vivos + 21 órfãos: passa de 20 objetos e de 10%: aborta
        var b = await NovaCenaAsync();
        for (var i = 0; i < 100; i++) b.Storage.Por("quarentena", b.Midia(StatusSanitizacao.Pendente, TimeSpan.FromHours(3)).ChaveStorage, antigo);
        ObjetosDaQuarentena(b, 21, antigo);
        Assert.Equal(0, await b.Limpeza().VarrerOrfasDaQuarentenaAsync(default));
        Assert.Equal(121, b.Storage.Objetos.Count);
        Assert.Equal(["trava_apagao"], b.Marcador.Degradados);
    }

    [SqlFact]
    public async Task Varredura_da_quarentena_nunca_recupera_o_trava_apagao_que_e_da_rodada_diaria_de_midia()
    {
        // O marcador é um só: se a varredura de 30 min "recuperasse", apagaria o alerta da rodada diária de midia/ em minutos.
        var c = await NovaCenaAsync();
        c.Storage.Por("quarentena", c.Midia(StatusSanitizacao.Pendente, TimeSpan.FromHours(3)).ChaveStorage, c.Relogio.UtcNow.AddHours(-3));

        await c.Limpeza().VarrerOrfasDaQuarentenaAsync(default);

        Assert.Empty(c.Marcador.Recuperados);
    }

    [SqlFact]
    public async Task Falha_de_permissao_na_listagem_vira_aviso_e_nao_excecao()
    {
        var c = await NovaCenaAsync();
        c.Storage.FalharAListar = true;

        var r = await c.Limpeza().ExecutarDiariaAsync(default);
        Assert.Equal(0, r.OrfaosDeMidia);
        Assert.Equal(0, await c.Limpeza().VarrerOrfasDaQuarentenaAsync(default));
    }
}

internal static class ExtensoesDeTeste
{
    public static bool Aprovado(this ArquivoMidia m) => m.StatusSanitizacao == StatusSanitizacao.Aprovado;

    /// <summary>A rodada de <c>midia/</c> isolada: com o banco fora, a parte de registros da diária (que também usa o banco) lançaria antes; a trava é da parte de <c>midia/</c>.</summary>
    public static async Task<(int Apagados, bool Trava)> ExecutarDiariaAsyncSoMidia(this LimpezaDeMidia limpeza)
    {
        var m = typeof(LimpezaDeMidia).GetMethod("LimparOrfasDeMidiaAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        return await (Task<(int, bool)>)m.Invoke(limpeza, [CancellationToken.None])!;
    }
}
