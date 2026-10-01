using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using WebRadio.Application.Abstracoes;
using WebRadio.Domain.Autenticacao;
using WebRadio.Domain.Erros;

namespace WebRadio.Application.Features.Auth;

public sealed record LoginCommand(string Email, string Senha, byte[] IpHash) : IRequest<LoginResultado>
{
    // Nunca imprimir e-mail nem senha (a máscara do Serilog só vale para {@Obj}; teste de arquitetura guarda isto).
    public override string ToString() => nameof(LoginCommand);
}

public sealed class LoginValidator : AbstractValidator<LoginCommand>
{
    public LoginValidator()
    {
        // Só formato/tamanho: não revela nada sobre contas. Teto de 128 evita PBKDF2 sobre entrada gigante (DoS).
        RuleFor(x => x.Email).NotEmpty().MaximumLength(254);
        RuleFor(x => x.Senha).NotEmpty().MaximumLength(128);
    }
}

public sealed class LoginHandler(
    IRadioDbContext db,
    ISenhaHasher hasher,
    IEmissorDeTokens tokens,
    ILoginThrottle throttle,
    IClock clock) : IRequestHandler<LoginCommand, LoginResultado>
{
    public async Task<LoginResultado> Handle(LoginCommand cmd, CancellationToken ct)
    {
        var emailNormalizado = cmd.Email.Trim().ToUpperInvariant();
        var avaliacao = await throttle.AvaliarAsync(emailNormalizado, cmd.IpHash, ct);
        if (avaliacao.Atraso > TimeSpan.Zero)
            await Task.Delay(avaliacao.Atraso, ct);

        // Bloqueado: nem consulta o banco; o custo de uma verificação de senha é pago do mesmo jeito (abaixo).
        var usuario = avaliacao.Bloqueado
            ? null
            : await db.Usuarios.FirstOrDefaultAsync(u => u.EmailNormalizado == emailNormalizado, ct);

        // Exatamente UMA verificação PBKDF2 em todos os caminhos (mesmo tempo, S-A12).
        var ok = false;
        if (usuario is { Ativo: true })
            ok = hasher.Verificar(usuario.SenhaHash, cmd.Senha);
        else
            hasher.VerificarFicticio(cmd.Senha);

        if (!ok || usuario is null)
        {
            if (!avaliacao.Bloqueado)
                await throttle.RegistrarFalhaAsync(emailNormalizado, cmd.IpHash, ct);
            throw new CredenciaisInvalidasException();
        }

        await throttle.LimparAsync(emailNormalizado, cmd.IpHash, ct);

        var agora = clock.UtcNow;
        var refresh = tokens.NovoRefresh();
        db.RefreshTokens.Add(new RefreshToken(Guid.NewGuid(), usuario.Id, refresh.Hash, Guid.NewGuid(), agora,
            tokens.DuracaoDoRefresh, cmd.IpHash));
        await db.SaveChangesAsync(ct);

        var access = tokens.EmitirAccessToken(usuario);
        return new LoginResultado(access.Valor, access.ExpiraEmUtc, usuario.DeveTrocarSenha, UsuarioDto.De(usuario), refresh);
    }
}
