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
        RuleFor(x => x.Senha).NotEmpty().MaximumLength(PoliticaDeSenha.Maximo);
    }
}

public sealed class LoginHandler(
    IRadioDbContext db,
    ISenhaHasher hasher,
    IEmissorDeTokens tokens,
    ILoginThrottle throttle,
    IClock clock) : IRequestHandler<LoginCommand, LoginResultado>
{
    /// <summary>
    /// Teto de sessões ativas por usuário (famílias de refresh token). Um login acima dele revoga as famílias MAIS ANTIGAS:
    /// sem teto, quem tem a senha (ou um script de login em laço) acumula refresh tokens sem limite no banco (#10).
    /// </summary>
    public const int MaximoDeSessoesPorUsuario = 10;

    public async Task<LoginResultado> Handle(LoginCommand cmd, CancellationToken ct)
    {
        var emailNormalizado = EmailChave.Normalizar(cmd.Email);
        var avaliacao = await throttle.AvaliarAsync(emailNormalizado, cmd.IpHash, ct);
        if (avaliacao.Atraso > TimeSpan.Zero)
            await Task.Delay(avaliacao.Atraso, ct);

        // Bloqueado: nem consulta o banco; o custo de uma verificação de senha é pago do mesmo jeito (abaixo).
        // A busca usa o e-mail tal como o banco normaliza (collation CI_AI); a chave do throttle é a de EmailChave.
        var emailBusca = cmd.Email.Trim().ToUpperInvariant();
        var usuario = avaliacao.Bloqueado
            ? null
            : await db.Usuarios.FirstOrDefaultAsync(u => u.EmailNormalizado == emailBusca, ct);

        // Exatamente UMA verificação PBKDF2 em todos os caminhos (mesmo tempo, S-A12).
        var resultado = ResultadoSenha.Falhou;
        if (usuario is { Ativo: true })
            resultado = hasher.Verificar(usuario.SenhaHash, cmd.Senha);
        else
            hasher.VerificarFicticio(cmd.Senha);

        if (resultado == ResultadoSenha.Falhou || usuario is null)
        {
            if (!avaliacao.Bloqueado)
                await throttle.RegistrarFalhaAsync(emailNormalizado, cmd.IpHash, ct);
            throw new CredenciaisInvalidasException();
        }

        await throttle.LimparAsync(emailNormalizado, cmd.IpHash, ct);

        var agora = clock.UtcNow;
        // Hash com parâmetros antigos: regrava com o custo atual (mesma transação do refresh abaixo).
        if (resultado == ResultadoSenha.OkRehash)
            usuario.AtualizarHashSenha(hasher.Hash(cmd.Senha), agora);

        // A sessão nova entra; se passar do teto, saem as famílias mais antigas (um refresh ativo por família: o rotacionado já está revogado).
        // O teto NÃO é atômico: dois logins simultâneos do mesmo usuário leem 9 famílias e os dois inserem, e ele passa de 10 até o próximo
        // login. É um teto frouxo de propósito (limita o acúmulo, não é um limite de segurança); uma trava por usuário custaria mais que vale.
        var ativos = await db.RefreshTokens.Where(t => t.UsuarioId == usuario.Id && t.RevogadoEmUtc == null && t.ExpiraEmUtc > agora).ToListAsync(ct);
        var excedentes = ativos.Select(t => t.FamiliaId).Distinct().Count() - (MaximoDeSessoesPorUsuario - 1);
        if (excedentes > 0)
        {
            var maisAntigas = ativos.GroupBy(t => t.FamiliaId).OrderBy(g => g.Min(t => t.CriadoEmUtc)).Take(excedentes).SelectMany(g => g);
            foreach (var antigo in maisAntigas) antigo.Revogar(agora);
        }

        var refresh = tokens.NovoRefresh();
        db.RefreshTokens.Add(new RefreshToken(Guid.NewGuid(), usuario.Id, refresh.Hash, Guid.NewGuid(), agora,
            tokens.DuracaoDoRefresh, cmd.IpHash));
        await db.SaveChangesAsync(ct);

        var access = tokens.EmitirAccessToken(usuario);
        return new LoginResultado(access.Token, access.ExpiraEmUtc, usuario.DeveTrocarSenha, UsuarioDto.De(usuario), refresh);
    }
}
