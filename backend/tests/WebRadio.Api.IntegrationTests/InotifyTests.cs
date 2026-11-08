using System.Runtime.InteropServices;
using Xunit;

namespace WebRadio.Api.IntegrationTests;

/// <summary>
/// #102: cada <c>WebApplicationFactory</c> abre um <c>FileSystemWatcher</c> por <c>appsettings*.json</c> (reloadOnChange), e cada um é uma instância
/// de inotify. O runner do GitHub limita a 128 por usuário: com dezenas de fábricas no mesmo processo, os testes caíam em
/// "The configured user limit (128) on the number of inotify instances has been reached". Os hosts de teste não recarregam configuração.
/// </summary>
[Collection(ColecaoDeLogs.Nome)] // sozinha: a contagem é do processo inteiro e outras classes abrem hosts em paralelo
public sealed class InotifyTests
{
    private static int InstanciasDeInotify()
        => Directory.EnumerateFileSystemEntries("/proc/self/fd")
            .Count(fd => new FileInfo(fd).LinkTarget?.Contains("inotify", StringComparison.Ordinal) == true);

    [Fact]
    public void Hosts_de_teste_nao_abrem_um_inotify_cada()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return; // /proc e inotify só existem no Linux (CI e Docker)

        var antes = InstanciasDeInotify();
        var fabricas = new List<ApiFactory>();
        try
        {
            for (var i = 0; i < 40; i++)
            {
                var f = new ApiFactory();
                fabricas.Add(f);
                _ = f.CreateClient(); // sobe o host: é aqui que o JsonConfigurationSource cria o watcher
            }

            // Com o reload ligado seriam ~40 a mais (e o limite do runner é 128).
            Assert.True(InstanciasDeInotify() - antes < 5, $"inotify subiu de {antes} para {InstanciasDeInotify()} com 40 hosts");
        }
        finally
        {
            foreach (var f in fabricas) f.Dispose();
        }
    }
}
