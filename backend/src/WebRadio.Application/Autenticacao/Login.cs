using System.Net;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using WebRadio.Application.Abstracoes;
using WebRadio.Application.Comum;

namespace WebRadio.Application.Autenticacao;

public sealed record LoginCommand(string Email, string Senha, IPAddress? Ip) : IRequest<SessaoIniciada>;

public sealed class LoginValidator : AbstractValidator<LoginCommand>
{
    public LoginValidator()
    {
        // Só formato mínimo: a política de senha vale na criação/troca, nunca no login (não pode revelar nada).
        RuleFor(c => c.Email)
            .NotEmpty().WithMessage("Informe o e-mail.")
            .MaximumLength(254).WithMessage("O e-mail pode ter no máximo 254 caracteres.");
        RuleFor(c => c.Senha)
            .NotEmpty().WithMessage("Informe a senha.")
            .MaximumLength(RegrasDeSenha.Maximo).WithMessage($"A senha pode ter no máximo {RegrasDeSenha.Maximo} caracteres.");
    }
}

public sealed class LoginHandler(
    IRadioDbContext db,
    IPasswordHasher hasher,
    IJwtService jwt,
    IRefreshTokenService refresh,
    IListenerIdentity identidade,
    LoginBloqueio bloqueio) : IRequestHandler<LoginCommand, SessaoIniciada>
{
    public async Task<SessaoIniciada> Handle(LoginCommand request, CancellationToken ct)
    {
        var email = request.Email.Trim().ToUpperInvariant();
        var ipHash = request.Ip is null ? null : identidade.IpHash(request.Ip);

        // Bloqueado: mesmo caminho e mesmo custo de um e-mail inexistente (D21, S-A12). Nunca 423.
        if (await bloqueio.EstaBloqueadoAsync(email, ipHash, ct))
        {
            hasher.VerificarFicticio(request.Senha);
            throw new CredenciaisInvalidasException();
        }

        var usuario = await db.Usuarios.FirstOrDefaultAsync(u => u.EmailNormalizado == email, ct);

        if (usuario is null)
        {
            hasher.VerificarFicticio(request.Senha);
            throw await FalharAsync(email, ipHash, ct);
        }

        // Verifica a senha mesmo com a conta inativa: o tempo não pode distinguir os casos.
        var senhaOk = hasher.Verificar(usuario.SenhaHash, request.Senha);
        if (!senhaOk || !usuario.Ativo)
        {
            throw await FalharAsync(email, ipHash, ct);
        }

        await bloqueio.LimparAsync(email, ipHash, ct);

        var access = jwt.Emitir(usuario);
        var novoRefresh = await refresh.EmitirAsync(usuario, ipHash, ct);
        var dto = UsuarioDto.De(usuario);

        return new SessaoIniciada(new SessaoResposta(access.Token, access.ExpiraEmUtc, usuario.DeveTrocarSenha, dto), novoRefresh);
    }

    private async Task<Exception> FalharAsync(string email, byte[]? ipHash, CancellationToken ct)
    {
        var atraso = await bloqueio.RegistrarFalhaAsync(email, ipHash, ct);
        if (atraso > TimeSpan.Zero)
        {
            await Task.Delay(atraso, ct);
        }

        return new CredenciaisInvalidasException();
    }
}
