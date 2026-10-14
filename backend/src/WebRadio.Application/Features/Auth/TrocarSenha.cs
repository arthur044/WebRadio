using FluentValidation;
using FluentValidation.Results;
using MediatR;
using Microsoft.EntityFrameworkCore;
using WebRadio.Application.Abstracoes;
using WebRadio.Domain.Erros;

namespace WebRadio.Application.Features.Auth;

/// <summary>TokenRefreshAtual (cookie) identifica a família que sobrevive à troca; sem ele, todas as sessões caem.</summary>
public sealed record TrocarSenhaCommand(Guid UsuarioId, string SenhaAtual, string NovaSenha, string? TokenRefreshAtual, byte[] IpHash) : IRequest
{
    public override string ToString() => nameof(TrocarSenhaCommand);
}

public sealed class TrocarSenhaValidator : AbstractValidator<TrocarSenhaCommand>
{
    public TrocarSenhaValidator(ISenhasVazadas vazadas)
    {
        RuleFor(x => x.SenhaAtual).NotEmpty().WithMessage("Informe a senha atual.")
            .MaximumLength(128).WithMessage("A senha pode ter no máximo 128 caracteres.");
        RuleFor(x => x.NovaSenha)
            .NotEmpty().WithMessage("Informe a nova senha.")
            .MinimumLength(12).WithMessage("A senha precisa ter de 12 a 128 caracteres.")
            .MaximumLength(128).WithMessage("A senha precisa ter de 12 a 128 caracteres.")
            .Must(s => !vazadas.EhVazada(s)).WithMessage("Esta senha é muito comum. Escolha outra.")
            .Must((c, nova) => nova != c.SenhaAtual).WithMessage("A nova senha precisa ser diferente da atual.");
    }
}

public sealed class TrocarSenhaHandler(
    IRadioDbContext db,
    ISenhaHasher hasher,
    IRefreshTokenService refresh,
    ILoginThrottle throttle,
    IVersaoDeSeguranca versaoDeSeguranca,
    IOutbox outbox,
    IClock clock) : IRequestHandler<TrocarSenhaCommand>
{
    public async Task Handle(TrocarSenhaCommand cmd, CancellationToken ct)
    {
        var usuario = await db.Usuarios.FirstOrDefaultAsync(u => u.Id == cmd.UsuarioId && u.Ativo, ct)
            ?? throw new CredenciaisInvalidasException();

        // Esta rota não pode ser oráculo da senha para quem só tem o access token (15 min): mesmo bloqueio do login (D21).
        var chave = EmailChave.Normalizar(usuario.Email);
        var avaliacao = await throttle.AvaliarAsync(chave, cmd.IpHash, ct);
        if (avaliacao.Atraso > TimeSpan.Zero) await Task.Delay(avaliacao.Atraso, ct);

        var resultado = ResultadoSenha.Falhou;
        if (avaliacao.Bloqueado) hasher.VerificarFicticio(cmd.SenhaAtual);
        else resultado = hasher.Verificar(usuario.SenhaHash, cmd.SenhaAtual);

        if (resultado == ResultadoSenha.Falhou)
        {
            if (!avaliacao.Bloqueado) await throttle.RegistrarFalhaAsync(chave, cmd.IpHash, ct);
            // Bloqueado e senha errada saem iguais: o cliente não distingue.
            throw new ValidationException([new ValidationFailure(nameof(cmd.SenhaAtual), "A senha atual está incorreta.")]);
        }

        // S-M11: as outras famílias caem primeiro (a atual continua; o cliente chama /auth/refresh para perder a restrição).
        // Nesta ordem, uma falha no meio deixa a senha antiga com sessões revogadas, nunca a nova com sessões roubadas vivas.
        await refresh.RevogarTodasDoUsuarioAsync(usuario.Id, cmd.TokenRefreshAtual, ct);

        usuario.TrocarSenha(hasher.Hash(cmd.NovaSenha), clock.UtcNow);
        // S-M08 / S-M11: a sv só é conferida ao conectar. Um WebSocket já aberto com o token antigo (inclusive roubado)
        // continuaria recebendo os eventos de `locutores` até o exp; o dispatcher derruba as conexões do usuário. Vai no
        // MESMO SaveChanges da senha nova: ou as duas coisas, ou nenhuma.
        outbox.EnfileirarSessoesRevogadas(usuario.Id);
        await db.SaveChangesAsync(ct);

        // A troca incrementa a versão de segurança: o access token em uso (e qualquer cópia roubada) para de valer agora
        // nesta instância. O cliente já chama /auth/refresh logo depois do 204 (02 §2).
        versaoDeSeguranca.Esquecer(usuario.Id);

        // Só depois de gravar: se o SaveChanges falhar, o contador de falhas continua valendo.
        await throttle.LimparAsync(chave, cmd.IpHash, ct);
    }
}
