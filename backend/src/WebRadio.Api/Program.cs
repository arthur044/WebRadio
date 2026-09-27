var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();

app.MapGet("/health/live", () => Results.Ok());

app.Run();

// Ponto de entrada exposto para WebApplicationFactory<Program> nos testes de integracao (E1-F06+).
public partial class Program;
