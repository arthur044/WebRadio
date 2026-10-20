using Microsoft.Extensions.Logging;
using WebRadio.Application.Abstracoes;
using WebRadio.Application.Features.Transmissao;
using WebRadio.Application.Tests.Auth;
using WebRadio.Domain.Enums;
using WebRadio.Domain.Erros;
using WebRadio.Domain.Transmissao;
using WebRadio.Infrastructure.Persistencia;
using WebRadio.Infrastructure.Seguranca;
using Xunit;

namespace WebRadio.Application.Tests.Transmissao;

/// <summary>Log capturado em memória: o segredo não pode aparecer em nenhuma linha.</summary>
public sealed class LogCapturado<T> : ILogger<T>
{
    public List<string> Linhas { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => Linhas.Add(formatter(state, exception));
}

public sealed class ThrottleQueFalha : IRequestThrottle
{
    public Task<bool> TentarReservarAsync(string chave, TimeSpan ttl, CancellationToken ct = default) => throw new InvalidOperationException("redis fora");
    public Task<long> IncrementarAsync(string chave, TimeSpan ttl, CancellationToken ct = default) => throw new InvalidOperationException("redis fora");
    public Task<long> LerAsync(string chave, CancellationToken ct = default) => throw new InvalidOperationException("redis fora");
    public Task RemoverAsync(string chave, CancellationToken ct = default) => throw new InvalidOperationException("redis fora");
    public Task<TimeSpan?> TempoRestanteAsync(string chave, CancellationToken ct = default) => throw new InvalidOperationException("redis fora");
}

public class CredencialDeTransmissaoHandlersTests
{
    private readonly RadioDbContext _db = AuthFixture.NovoDb();
    private readonly HasherContador _hasher = new();
    private readonly RelogioFalso _relogio = new(AuthFixture.Agora);
    private readonly LogCapturado<GerarCredencialHandler> _log = new();
    private IRequestThrottle _throttle;

    public CredencialDeTransmissaoHandlersTests() => _throttle = new MemoryRequestThrottle(_relogio);

    private GerarCredencialHandler Gerar() =>
        new(_db, _hasher, _throttle, new IpHasher(new byte[32]), _relogio, _log);

    private Task<CredencialGeradaDto> GerarAsync(Guid usuarioId, string senha = AuthFixture.SenhaOk) =>
        Gerar().Handle(new GerarCredencialCommand(usuarioId, senha), CancellationToken.None);

    [Fact]
    public async Task Gera_identificador_v4_e_segredo_de_43_e_grava_so_o_hash()
    {
        var u = AuthFixture.CriarUsuario(_db, _hasher);

        var r = await GerarAsync(u.Id);

        Assert.Equal(4, r.Identificador.Version);
        Assert.True(SegredoDeTransmissao.SenhaValida(r.Segredo));
        var c = Assert.Single(_db.CredenciaisTransmissao);
        Assert.Equal((u.Id, r.Identificador, AuthFixture.Agora), (c.UsuarioId, c.Identificador, c.CriadaEmUtc));
        Assert.True(SegredoDeTransmissao.Confere(c.Salt, c.SegredoHash, r.Segredo));
        Assert.NotEqual(u.Id, r.Identificador);
    }

    [Fact]
    public async Task Gerar_de_novo_revoga_a_anterior()
    {
        var u = AuthFixture.CriarUsuario(_db, _hasher);
        var primeira = await GerarAsync(u.Id);
        _relogio.UtcNow = AuthFixture.Agora.AddMinutes(1);

        var segunda = await GerarAsync(u.Id);

        var ativas = _db.CredenciaisTransmissao.Where(c => c.RevogadaEmUtc == null).ToList();
        Assert.Equal(segunda.Identificador, Assert.Single(ativas).Identificador);
        Assert.Equal(AuthFixture.Agora.AddMinutes(1), _db.CredenciaisTransmissao.Single(c => c.Identificador == primeira.Identificador).RevogadaEmUtc);
    }

    [Fact]
    public async Task Senha_atual_errada_e_401()
    {
        var u = AuthFixture.CriarUsuario(_db, _hasher);

        await Assert.ThrowsAsync<CredenciaisInvalidasException>(() => GerarAsync(u.Id, "senha-errada-bem-longa"));
        Assert.Empty(_db.CredenciaisTransmissao);
    }

    [Fact]
    public async Task Sexta_tentativa_na_hora_e_429_mesmo_com_a_senha_certa_e_conta_as_erradas()
    {
        var u = AuthFixture.CriarUsuario(_db, _hasher);
        for (var i = 0; i < 3; i++) await Assert.ThrowsAsync<CredenciaisInvalidasException>(() => GerarAsync(u.Id, "senha-errada-bem-longa"));
        await GerarAsync(u.Id);
        await GerarAsync(u.Id);

        var ex = await Assert.ThrowsAsync<LimiteExcedidoException>(() => GerarAsync(u.Id));
        Assert.True(ex.TenteDeNovoEm > TimeSpan.Zero && ex.TenteDeNovoEm <= TimeSpan.FromHours(1));

        _relogio.UtcNow = AuthFixture.Agora.AddHours(1).AddSeconds(1);
        await GerarAsync(u.Id); // a janela passou
    }

    [Fact]
    public async Task O_limite_e_por_usuario()
    {
        var a = AuthFixture.CriarUsuario(_db, _hasher, "a@example.com");
        var b = AuthFixture.CriarUsuario(_db, _hasher, "b@example.com");
        for (var i = 0; i < 5; i++) await GerarAsync(a.Id);

        await GerarAsync(b.Id);
    }

    [Fact]
    public async Task Throttle_fora_recusa_com_503_em_vez_de_liberar()
    {
        var u = AuthFixture.CriarUsuario(_db, _hasher);
        _throttle = new ThrottleQueFalha();

        await Assert.ThrowsAsync<ServidorOcupadoException>(() => GerarAsync(u.Id));
        Assert.Empty(_db.CredenciaisTransmissao);
    }

    [Fact]
    public async Task Conta_inativa_e_401_e_Ouvinte_e_403()
    {
        var inativo = AuthFixture.CriarUsuario(_db, _hasher, "i@example.com", ativo: false);
        var ouvinte = AuthFixture.CriarUsuario(_db, _hasher, "o@example.com", role: Role.Ouvinte);

        await Assert.ThrowsAsync<CredenciaisInvalidasException>(() => GerarAsync(inativo.Id));
        await Assert.ThrowsAsync<AcessoNegadoException>(() => GerarAsync(ouvinte.Id));
    }

    [Fact]
    public async Task Admin_tambem_gera()
    {
        var admin = AuthFixture.CriarUsuario(_db, _hasher, role: Role.Admin);

        await GerarAsync(admin.Id);
    }

    [Fact]
    public async Task Auditoria_registra_identificador_e_nunca_o_segredo()
    {
        var u = AuthFixture.CriarUsuario(_db, _hasher);

        var r = await GerarAsync(u.Id);

        Assert.Contains(_log.Linhas, l => l.Contains("CREDENCIAL_TRANSMISSAO_GERADA") && l.Contains(r.Identificador.ToString()));
        Assert.DoesNotContain(_log.Linhas, l => l.Contains(r.Segredo));
    }

    [Fact]
    public void Comando_e_resultado_nao_imprimem_senha_nem_segredo()
    {
        var segredo = SegredoDeTransmissao.Gerar().Segredo;

        Assert.DoesNotContain("senha-x", new GerarCredencialCommand(Guid.NewGuid(), "senha-x").ToString());
        Assert.DoesNotContain(segredo, new CredencialGeradaDto(Guid.NewGuid(), segredo, AuthFixture.Agora).ToString());
    }

    [Fact]
    public async Task Obter_devolve_a_ativa_sem_segredo_ou_null()
    {
        var u = AuthFixture.CriarUsuario(_db, _hasher);
        var handler = new ObterMinhaCredencialHandler(_db);

        Assert.Null(await handler.Handle(new ObterMinhaCredencialQuery(u.Id), CancellationToken.None));

        var r = await GerarAsync(u.Id);
        var dto = await handler.Handle(new ObterMinhaCredencialQuery(u.Id), CancellationToken.None);

        Assert.Equal(new CredencialDto(r.Identificador, AuthFixture.Agora, null), dto);
    }

    [Fact]
    public async Task Revogar_a_propria_e_idempotente()
    {
        var u = AuthFixture.CriarUsuario(_db, _hasher);
        await GerarAsync(u.Id);
        var log = new LogCapturado<RevogarCredencialHandler>();
        var handler = new RevogarCredencialHandler(_db, _relogio, log);

        await handler.Handle(new RevogarCredencialCommand(u.Id, u.Id), CancellationToken.None);
        await handler.Handle(new RevogarCredencialCommand(u.Id, u.Id), CancellationToken.None);

        Assert.All(_db.CredenciaisTransmissao, c => Assert.False(c.EstaAtiva));
        Assert.Single(log.Linhas, l => l.Contains("CREDENCIAL_TRANSMISSAO_REVOGADA"));
    }

    [Fact]
    public async Task Admin_revogando_usuario_inexistente_e_404()
    {
        var handler = new RevogarCredencialHandler(_db, _relogio, new LogCapturado<RevogarCredencialHandler>());

        await Assert.ThrowsAsync<NaoEncontradoException>(() =>
            handler.Handle(new RevogarCredencialCommand(Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None));
    }

    [Fact]
    public async Task Listar_traz_so_as_ativas_com_o_nome_do_dono()
    {
        var a = AuthFixture.CriarUsuario(_db, _hasher, "a@example.com");
        var b = AuthFixture.CriarUsuario(_db, _hasher, "b@example.com");
        await GerarAsync(a.Id);
        var ativaA = await GerarAsync(a.Id);
        var ativaB = await GerarAsync(b.Id);

        var lista = await new ListarCredenciaisAtivasHandler(_db).Handle(new ListarCredenciaisAtivasQuery(), CancellationToken.None);

        Assert.Equal(2, lista.Count);
        Assert.Contains(lista, x => x.UsuarioId == a.Id && x.Identificador == ativaA.Identificador && x.UsuarioNome == "Ana");
        Assert.Contains(lista, x => x.UsuarioId == b.Id && x.Identificador == ativaB.Identificador);
    }
}
