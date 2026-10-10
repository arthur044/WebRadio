using System.Diagnostics;
using System.Net;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using WebRadio.Domain.Enums;
using WebRadio.Domain.Usuarios;
using WebRadio.Infrastructure.Opcoes;
using WebRadio.Infrastructure.Seguranca;
using WebRadio.Infrastructure.Tests.Suporte;
using Xunit;

namespace WebRadio.Infrastructure.Tests;

public class PasswordHasherTests
{
    [Fact]
    public void Hash_tem_formato_v1_com_100k_iteracoes_salt16_hash32()
    {
        var hasher = new PasswordHasher(Segredos.Seguranca());

        var partes = hasher.Hash("uma-senha-longa-123").Split('$');

        Assert.Equal(["v1", "100000"], partes[..2]);
        Assert.Equal(16, Convert.FromBase64String(partes[2]).Length);
        Assert.Equal(32, Convert.FromBase64String(partes[3]).Length);
    }

    [Fact]
    public void Verifica_senha_certa_e_recusa_errada()
    {
        var hasher = new PasswordHasher(Segredos.Seguranca());
        var hash = hasher.Hash("senha-correta-123");

        Assert.True(hasher.Verificar(hash, "senha-correta-123"));
        Assert.False(hasher.Verificar(hash, "senha-errada-1234"));
    }

    [Fact]
    public void Mesma_senha_gera_hashes_diferentes_por_causa_do_salt()
    {
        var hasher = new PasswordHasher(Segredos.Seguranca());

        Assert.NotEqual(hasher.Hash("senha-correta-123"), hasher.Hash("senha-correta-123"));
    }

    [Fact]
    public void Pepper_diferente_nao_verifica_o_hash()
    {
        var hash = new PasswordHasher(Segredos.Seguranca()).Hash("senha-correta-123");

        Assert.False(new PasswordHasher(Segredos.Seguranca()).Verificar(hash, "senha-correta-123"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("lixo")]
    [InlineData("v1$100000$nao-base64$nao-base64")]
    [InlineData("v1$0$AAAA$AAAA")]
    public void Hash_armazenado_corrompido_e_recusado_sem_excecao(string armazenado)
    {
        var hasher = new PasswordHasher(Segredos.Seguranca());

        Assert.False(hasher.Verificar(armazenado, "qualquer-senha-12"));
    }

    [Fact]
    public void Verificacao_ficticia_custa_o_mesmo_que_a_real()
    {
        var hasher = new PasswordHasher(Segredos.Seguranca());
        var hash = hasher.Hash("senha-correta-123");
        hasher.Verificar(hash, "x"); // aquece o JIT

        var real = Medir(() => hasher.Verificar(hash, "senha-errada-1234"));
        var ficticio = Medir(() => hasher.VerificarFicticio("senha-errada-1234"));

        // Mesmo algoritmo e iterações: a razão fica perto de 1 (folga larga para ruído de CI).
        Assert.InRange(ficticio / real, 0.5, 2.0);
    }

    private static double Medir(Action acao)
    {
        var melhor = double.MaxValue;
        for (var i = 0; i < 5; i++)
        {
            var inicio = Stopwatch.GetTimestamp();
            acao();
            melhor = Math.Min(melhor, Stopwatch.GetElapsedTime(inicio).TotalMilliseconds);
        }

        return melhor;
    }
}

public class JwtServiceTests
{
    private static readonly DateTime Agora = new(2026, 9, 28, 12, 0, 0, DateTimeKind.Utc);

    private static Usuario NovoUsuario(bool deveTrocarSenha = false) =>
        new(Guid.NewGuid(), "Ana", "ana@example.com", "hash", Role.Locutor, deveTrocarSenha, Agora);

    [Fact]
    public async Task Emite_sub_name_role_jti_iat_exp_iss_aud_assinado_com_HS256()
    {
        var opcoes = Segredos.Jwt();
        var usuario = NovoUsuario();
        var jwt = new JwtService(opcoes, new RelogioFalso(Agora));

        var emitido = jwt.Emitir(usuario);

        var resultado = await new JsonWebTokenHandler().ValidateTokenAsync(emitido.Token, new TokenValidationParameters
        {
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            ValidIssuer = "webradio-api",
            ValidAudience = "webradio",
            IssuerSigningKey = new SymmetricSecurityKey(opcoes.Value.ChaveBytes()),
            ValidateLifetime = false,
        });

        Assert.True(resultado.IsValid, resultado.Exception?.Message);
        var token = (JsonWebToken)resultado.SecurityToken;
        Assert.Equal(usuario.Id.ToString(), token.Subject);
        Assert.Equal("Ana", token.GetClaim("name").Value);
        Assert.Equal("Locutor", token.GetClaim("role").Value);
        Assert.NotEmpty(token.Id);
        Assert.Equal(Agora, token.IssuedAt);
        Assert.Equal(Agora.AddMinutes(15), token.ValidTo);
        Assert.Equal(Agora.AddMinutes(15), emitido.ExpiraEmUtc);
        Assert.False(token.TryGetClaim(JwtService.ClaimDeveTrocarSenha, out _));
    }

    [Fact]
    public void Marca_a_claim_dts_so_quando_DeveTrocarSenha()
    {
        var jwt = new JwtService(Segredos.Jwt(), new RelogioFalso(Agora));

        var token = new JsonWebToken(jwt.Emitir(NovoUsuario(deveTrocarSenha: true)).Token);

        Assert.True(token.TryGetClaim(JwtService.ClaimDeveTrocarSenha, out _));
    }

    [Fact]
    public void Cada_token_tem_jti_proprio()
    {
        var jwt = new JwtService(Segredos.Jwt(), new RelogioFalso(Agora));
        var usuario = NovoUsuario();

        Assert.NotEqual(new JsonWebToken(jwt.Emitir(usuario).Token).Id, new JsonWebToken(jwt.Emitir(usuario).Token).Id);
    }
}

public class ListenerIdentityTests
{
    [Fact]
    public void Mesmo_IPv6_no_mesmo_64_gera_o_mesmo_hash()
    {
        var servico = new ListenerIdentityService(Segredos.Seguranca());

        var a = servico.IpHash(IPAddress.Parse("2001:db8:1:2:aaaa:bbbb:cccc:dddd"));
        var b = servico.IpHash(IPAddress.Parse("2001:db8:1:2:1111:2222:3333:4444"));
        var outro64 = servico.IpHash(IPAddress.Parse("2001:db8:1:3:aaaa:bbbb:cccc:dddd"));

        Assert.Equal(a, b);
        Assert.NotEqual(a, outro64);
        Assert.Equal(32, a.Length);
    }

    [Fact]
    public void IPv4_mapeado_em_IPv6_equivale_ao_IPv4()
    {
        var servico = new ListenerIdentityService(Segredos.Seguranca());

        Assert.Equal(servico.IpHash(IPAddress.Parse("203.0.113.9")), servico.IpHash(IPAddress.Parse("::ffff:203.0.113.9")));
    }

    [Fact]
    public void Pepper_diferente_muda_o_hash_e_ip_e_dispositivo_nao_colidem()
    {
        var pepper = Segredos.ChaveBase64Url();
        var a = new ListenerIdentityService(Segredos.Seguranca(pepper));
        var b = new ListenerIdentityService(Segredos.Seguranca());
        var id = Guid.NewGuid();

        Assert.Equal(a.DeviceHash(id), new ListenerIdentityService(Segredos.Seguranca(pepper)).DeviceHash(id));
        Assert.NotEqual(a.DeviceHash(id), b.DeviceHash(id));
        Assert.StartsWith("listener:", a.GrupoListener(id), StringComparison.Ordinal);
    }
}

public class ValidacaoDeOpcoesTests
{
    [Theory]
    [InlineData("")]
    [InlineData("__GERAR__")]
    [InlineData("CHANGE_ME")]
    [InlineData("curta")]
    [InlineData("isto-nao-e-base64url-com-espaco e simbolos!!")]
    public void Jwt_recusa_chave_vazia_de_exemplo_curta_ou_invalida(string chave)
    {
        var resultado = new JwtOptionsValidator().Validate(null, new JwtOptions { SigningKey = chave });

        Assert.True(resultado.Failed);

        // A mensagem cita o nome da configuração, nunca o valor (__GERAR__ aparece como instrução, não como valor).
        if (!ValidadorDeSegredos.EhPlaceholder(chave))
        {
            Assert.DoesNotContain(chave, resultado.FailureMessage, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Jwt_aceita_chave_com_hifen_e_underscore_sem_padding_medida_depois_de_decodificar()
    {
        // 32 bytes onde o base64url contém '-' e '_': Convert.FromBase64String quebraria.
        var bytes = Enumerable.Repeat((byte)0xFB, 32).ToArray();
        var chave = Base64Url(bytes);
        Assert.Contains('-', chave);
        Assert.DoesNotContain('=', chave);

        Assert.True(new JwtOptionsValidator().Validate(null, new JwtOptions { SigningKey = chave }).Succeeded);
    }

    [Fact]
    public void Jwt_recusa_chave_com_31_bytes_decodificados()
    {
        Assert.True(new JwtOptionsValidator().Validate(null, new JwtOptions { SigningKey = Segredos.ChaveBase64Url(31) }).Failed);
    }

    [Fact]
    public void Pepper_e_Redis_e_Storage_recusam_valor_de_exemplo()
    {
        Assert.True(new SegurancaOptionsValidator().Validate(null, new SegurancaOptions { Pepper = "__GERAR__" }).Failed);
        Assert.True(new RedisOptionsValidator().Validate(null, new RedisOptions { Password = "__GERAR__" }).Failed);
        Assert.True(new StorageOptionsValidator().Validate(null, new StorageOptions { AccessKey = "__GERAR__", SecretKey = "" }).Failed);
    }

    [Fact]
    public void Seed_so_exige_algo_quando_configurado()
    {
        var validador = new SeedOptionsValidator();

        Assert.True(validador.Validate(null, new SeedOptions()).Succeeded);
        Assert.True(validador.Validate(null, new SeedOptions { AdminEmail = "admin@example.com" }).Failed);
        Assert.True(validador.Validate(null, new SeedOptions { AdminEmail = "admin@example.com", AdminSenha = "__GERAR__" }).Failed);
        Assert.True(validador.Validate(null, new SeedOptions { AdminEmail = "admin@example.com", AdminSenha = "uma-senha-forte-1" }).Succeeded);
        Assert.True(validador.Validate(null, new SeedOptions { DevData = true }).Failed);
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
