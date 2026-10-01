using System.Reflection;
using System.Runtime.CompilerServices;
using MediatR;
using WebRadio.Api.Infra;
using WebRadio.Application;
using Xunit;

namespace WebRadio.Architecture.Tests;

/// <summary>
/// A máscara do Serilog só vale para {@Obj}. Um request com senha/token/e-mail logado com {Obj} imprimiria
/// tudo via ToString(); então todo request sensível precisa redefinir ToString (ou PrintMembers do record) e
/// esconder esses campos (Guardian m3 / Sentinel 2 no PR #7).
/// Escopo: SÓ os IBaseRequest da assembly Application. DTOs e comandos fora disso seguem sem esta guarda.
/// </summary>
public class RequestsSensiveisTests
{
    private const BindingFlags Declarado = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

    // Todo record já tem ToString/PrintMembers SINTETIZADOS no próprio tipo: só conta como "redefinido"
    // o método escrito à mão, isto é, sem [CompilerGenerated].
    private static bool Redefine(Type t, string metodo)
        => t.GetMethods(Declarado).Any(m => m.Name == metodo && m.GetCustomAttribute<CompilerGeneratedAttribute>() is null);

    internal static List<string?> Violadores(IEnumerable<Type> tipos)
        => tipos
            .Where(t => !t.IsAbstract && typeof(IBaseRequest).IsAssignableFrom(t))
            .Where(t => t.GetProperties().Any(p => MascaraSegredosPolicy.EhSensivel(p.Name)))
            .Where(t => !Redefine(t, "ToString") && !Redefine(t, "PrintMembers"))
            .Select(t => t.FullName)
            .ToList();

    [Fact]
    public void Requests_da_Application_com_propriedade_sensivel_redefinem_ToString_ou_PrintMembers()
    {
        var violadores = Violadores(typeof(AssemblyMarker).Assembly.GetTypes());
        Assert.True(violadores.Count == 0, "Requests sensíveis sem ToString/PrintMembers: " + string.Join(", ", violadores));
    }

    private sealed record SemOverride(string Password) : IRequest;

    private sealed record ComToString(string Password) : IRequest
    {
        public override string ToString() => "ComToString { Password = *** }";
    }

    private sealed record ComPrintMembers(string Token) : IRequest
    {
        private bool PrintMembers(System.Text.StringBuilder sb)
        {
            sb.Append("Token = ***");
            return true;
        }
    }

    private sealed record Inofensivo(string Nome) : IRequest;

    [Fact]
    public void A_checagem_pega_record_sensivel_sem_override_e_aceita_os_demais()
    {
        Assert.Equal([typeof(SemOverride).FullName], Violadores([typeof(SemOverride)]));
        Assert.Empty(Violadores([typeof(ComToString), typeof(ComPrintMembers), typeof(Inofensivo)]));
    }

    // ---- I5: guarda ampla (não só IBaseRequest) ----

    internal static List<string?> TiposSensiveisSemToString(IEnumerable<Type> tipos)
        => tipos
            .Where(t => t is { IsClass: true, IsAbstract: false } && t.GetCustomAttribute<CompilerGeneratedAttribute>() is null)
            .Where(t => !typeof(Exception).IsAssignableFrom(t) && !typeof(Delegate).IsAssignableFrom(t))
            // Só propriedades que CARREGAM segredo (texto ou bytes); um TimeSpan "DuracaoDoRefresh" ou um DbSet
            // "RefreshTokens" não vazam nada ao imprimir.
            .Where(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Any(p => (p.PropertyType == typeof(string) || p.PropertyType == typeof(byte[])) && MascaraSegredosPolicy.EhSensivel(p.Name)))
            .Where(t => !Redefine(t, "ToString") && !Redefine(t, "PrintMembers"))
            .Select(t => t.FullName)
            .ToList();

    [Fact]
    public void Todo_tipo_de_Application_Infrastructure_e_Api_com_propriedade_sensivel_redefine_ToString()
    {
        var tipos = new[] { typeof(AssemblyMarker).Assembly, typeof(WebRadio.Infrastructure.Persistencia.RadioDbContext).Assembly, typeof(MascaraSegredosPolicy).Assembly }
            .SelectMany(a => a.GetTypes());
        var violadores = TiposSensiveisSemToString(tipos);
        Assert.True(violadores.Count == 0, "Tipos com campo sensível e ToString sintetizado (vazam com {Obj} sem @): " + string.Join(", ", violadores));
    }

    private sealed record Credencial(string Token);

    private sealed record CredencialSegura(string Token)
    {
        public override string ToString() => nameof(CredencialSegura);
    }

    private sealed class ClasseSensivel
    {
        public string Senha { get; set; } = "";
    }

    [Fact]
    public void A_guarda_ampla_pega_records_e_classes_sensiveis_sem_override()
    {
        Assert.Equal(
            [typeof(Credencial).FullName, typeof(ClasseSensivel).FullName],
            TiposSensiveisSemToString([typeof(Credencial), typeof(CredencialSegura), typeof(ClasseSensivel)]));
    }
}
