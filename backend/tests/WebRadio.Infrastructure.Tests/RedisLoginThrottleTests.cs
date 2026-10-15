using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using WebRadio.Infrastructure.DependencyInjection;
using StackExchange.Redis;
using WebRadio.Infrastructure.Seguranca;
using WebRadio.Infrastructure.Tests.Suporte;
using Xunit;

namespace WebRadio.Infrastructure.Tests;

/// <summary>D21 em Redis de verdade: o script Lua, as chaves com hash tag e o SCAN do desbloqueio.</summary>
public class RedisLoginThrottleTests
{
    private static readonly byte[] Ip1 = Enumerable.Repeat((byte)1, 32).ToArray();
    private static readonly byte[] Ip2 = Enumerable.Repeat((byte)2, 32).ToArray();

    private static RedisLoginThrottle Novo() => new(RedisDeTeste.Multiplexador, Hasher, NullLogger<RedisLoginThrottle>.Instance);

    // Cada teste usa e-mails próprios: o Redis é compartilhado e nenhum teste apaga o que não é dele.
    private static string Email() => $"redis-{Guid.NewGuid():N}@x.com".ToUpperInvariant();

    [RedisFact]
    public async Task Cinco_falhas_do_par_bloqueiam_e_Limpar_libera_so_o_par()
    {
        var t = Novo();
        var email = Email();

        for (var i = 0; i < 4; i++) await t.RegistrarFalhaAsync(email, Ip1, default);
        Assert.False((await t.AvaliarAsync(email, Ip1, default)).Bloqueado);
        await t.RegistrarFalhaAsync(email, Ip1, default);
        Assert.True((await t.AvaliarAsync(email, Ip1, default)).Bloqueado);
        Assert.False((await t.AvaliarAsync(email, Ip2, default)).Bloqueado); // outro IP, mesma conta: livre

        await t.LimparAsync(email, Ip1, default);
        Assert.False((await t.AvaliarAsync(email, Ip1, default)).Bloqueado);
    }

    private static readonly IpHasher Hasher = new(Enumerable.Repeat((byte)7, 32).ToArray());

    private static async Task<RedisKey[]> ChavesDaConta(string email)
    {
        var padrao = $"wr:login:{{{Hasher.ChaveOpaca(email)}}}:*";
        var chaves = new List<RedisKey>();
        foreach (var endpoint in RedisDeTeste.Multiplexador.GetEndPoints())
            await foreach (var chave in RedisDeTeste.Multiplexador.GetServer(endpoint).KeysAsync(pattern: padrao))
                chaves.Add(chave);
        return [.. chaves];
    }

    [RedisFact]
    public async Task As_tres_chaves_de_uma_conta_caem_no_mesmo_slot_do_Cluster()
    {
        var t = Novo();
        var email = Email();
        for (var i = 0; i < 5; i++) await t.RegistrarFalhaAsync(email, Ip1, default); // cria par, bloqueio e contador

        var chaves = await ChavesDaConta(email);

        Assert.Equal(3, chaves.Length); // par, bloqueio e conta
        // Sem a hash tag o EVAL dessas três chaves daria CROSSSLOT num Redis Cluster.
        Assert.Single(chaves.Select(c => RedisDeTeste.Multiplexador.HashSlot(c)).Distinct());
    }

    [RedisFact]
    public async Task Desbloquear_a_conta_apaga_par_bloqueio_e_contador_so_dela()
    {
        var t = Novo();
        var minha = Email();
        var outra = Email();
        for (var i = 0; i < 5; i++)
        {
            await t.RegistrarFalhaAsync(minha, Ip1, default);
            await t.RegistrarFalhaAsync(minha, Ip2, default);
            await t.RegistrarFalhaAsync(outra, Ip1, default);
        }

        Assert.True((await t.AvaliarAsync(minha, Ip1, default)).Bloqueado);
        await t.DesbloquearContaAsync(minha, default);

        Assert.False((await t.AvaliarAsync(minha, Ip1, default)).Bloqueado);
        Assert.False((await t.AvaliarAsync(minha, Ip2, default)).Bloqueado);
        Assert.True((await t.AvaliarAsync(outra, Ip1, default)).Bloqueado); // a outra conta segue bloqueada
    }

    [RedisFact]
    public async Task Token_de_cancelamento_ja_cancelado_solta_quem_chama()
    {
        var t = Novo();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => t.RegistrarFalhaAsync(Email(), Ip1, cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => t.AvaliarAsync(Email(), Ip1, cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => t.DesbloquearContaAsync(Email(), cts.Token));
    }

    [RedisFact]
    public async Task O_script_e_atomico_sob_concorrencia_o_contador_da_conta_nao_perde_incrementos()
    {
        var t = Novo();
        var email = Email();

        await Task.WhenAll(Enumerable.Range(0, 40).Select(i => t.RegistrarFalhaAsync(email, i % 2 == 0 ? Ip1 : Ip2, default)));

        var chaveDaConta = (await ChavesDaConta(email)).Single(c => c.ToString().EndsWith(":conta"));
        Assert.Equal(40, (long)await RedisDeTeste.Multiplexador.GetDatabase().StringGetAsync(chaveDaConta));
    }
}

/// <summary>#19: a conexão com o Redis nasce no boot (ConnectAsync) e a porta é validada, em vez de conectar de forma síncrona no 1º login.</summary>
public class ConexaoRedisTests
{
    private static IConfiguration Config(params (string, string?)[] itens) =>
        new ConfigurationBuilder().AddInMemoryCollection(itens.ToDictionary(i => i.Item1, i => i.Item2)).Build();

    [Fact]
    public void Sem_porta_vale_a_6379_e_a_senha_e_o_host_vem_da_configuracao()
    {
        var o = ConexaoRedis.CriarOpcoes(Config(("Redis:Host", "redis"), ("Redis:Password", "senha-do-redis-de-teste")));

        var destino = Assert.IsType<System.Net.DnsEndPoint>(o.EndPoints.Single());
        Assert.Equal(("redis", 6379), (destino.Host, destino.Port));
        Assert.Equal("senha-do-redis-de-teste", o.Password);
        Assert.False(o.AbortOnConnectFail); // a Api sobe e o login cai para a memória se o Redis estiver fora
    }

    [Fact]
    public void Porta_valida_e_usada()
        => Assert.Equal(6380, Assert.IsType<System.Net.DnsEndPoint>(ConexaoRedis.CriarOpcoes(Config(("Redis:Host", "redis"), ("Redis:Porta", "6380"))).EndPoints.Single()).Port);

    [Theory]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("65536")]
    [InlineData("6379;evil")]
    public void Porta_invalida_recusa_o_boot_em_vez_de_cair_em_6379_calado(string porta)
    {
        var e = Assert.Throws<InvalidOperationException>(() => ConexaoRedis.CriarOpcoes(Config(("Redis:Host", "redis"), ("Redis:Porta", porta))));

        Assert.Contains("Redis:Porta", e.Message);
    }

    [Fact]
    public async Task Redis_fora_do_ar_nao_derruba_o_boot_e_a_conexao_fica_disponivel()
    {
        var conexao = new ConexaoRedis(Config(("Redis:Host", "127.0.0.1"), ("Redis:Porta", "1")));

        await conexao.StartAsync(default).WaitAsync(TimeSpan.FromSeconds(30)); // não lança: AbortOnConnectFail desligado

        Assert.NotNull(conexao.Multiplexador);
        Assert.False(conexao.Multiplexador.IsConnected);
        await conexao.StopAsync(default);
    }

    [RedisFact]
    public async Task Com_Redis_no_ar_o_boot_ja_deixa_a_conexao_aberta()
    {
        var destino = Environment.GetEnvironmentVariable("WEBRADIO_TEST_REDIS")!.Split(':');
        var conexao = new ConexaoRedis(Config(("Redis:Host", destino[0]), ("Redis:Porta", destino[1])));

        await conexao.StartAsync(default);

        Assert.True(conexao.Multiplexador.IsConnected);
        await conexao.StopAsync(default);
    }
}
