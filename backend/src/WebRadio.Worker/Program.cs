using WebRadio.Infrastructure.DependencyInjection;

var builder = Host.CreateApplicationBuilder(args);

// Mesmo esquema da Api: segredos por arquivo (/run/secrets, 05 §5.3) vencem as variáveis de ambiente.
builder.Configuration.AddKeyPerFile("/run/secrets", optional: true);

// Log JSON em UTC, uma linha por evento. Nenhuma mensagem do Worker leva connection string, senha ou dado de ouvinte.
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(o => o.UseUtcTimestamp = true);
// Cada comando SQL do ciclo de 10 s sairia como Information.
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning);

// Liderança (sp_getapplock), ciclo de 10 s com heartbeat e manutenção diária (E1-F11).
builder.Services.AddWorkerInfrastructure(builder.Configuration, exigirSenhaDeSegredo: !builder.Environment.IsDevelopment());

var host = builder.Build();

host.Run();

// Ponto de entrada exposto para o teste de arquitetura (E1-F02) e testes de integração futuros.
public partial class Program;
