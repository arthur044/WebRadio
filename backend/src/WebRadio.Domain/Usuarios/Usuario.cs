using WebRadio.Domain.Common;
using WebRadio.Domain.Enums;
using WebRadio.Domain.Erros;

namespace WebRadio.Domain.Usuarios;

/// <summary>
/// v1.1: FalhasLogin/BloqueadoAteUtc saíram da entidade (o bloqueio de login vive em Redis por
/// (conta, IpHash), D21). Domain não conhece esse estado.
/// </summary>
public sealed class Usuario : Entidade
{
    public string Nome { get; private set; } = null!;

    public string Email { get; private set; } = null!;

    public string EmailNormalizado { get; private set; } = null!;

    public string SenhaHash { get; private set; } = null!;

    public Role Role { get; private set; }

    public bool Ativo { get; private set; }

    /// <summary>true no Admin semeado; enquanto true, o token só permite POST /auth/trocar-senha e GET /auth/me.</summary>
    public bool DeveTrocarSenha { get; private set; }

    public DateTime CriadoEmUtc { get; private set; }

    public DateTime AtualizadoEmUtc { get; private set; }

    private Usuario()
    {
    }

    public Usuario(Guid id, string nome, string email, string senhaHash, Role role, bool deveTrocarSenha, DateTime agora)
        : base(id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nome);
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentException.ThrowIfNullOrWhiteSpace(senhaHash);
        UtcGuard.Exigir(agora, nameof(agora));

        Nome = nome;
        Email = email;
        EmailNormalizado = email.ToUpperInvariant();
        SenhaHash = senhaHash;
        Role = role;
        Ativo = true;
        DeveTrocarSenha = deveTrocarSenha;
        CriadoEmUtc = agora;
        AtualizadoEmUtc = agora;
    }

    /// <summary>
    /// Rebaixar o último Admin ativo é proibido, venha de quem vier. <paramref name="ehUltimoAdminAtivo"/> é
    /// calculado pelo handler (Application) — o Domain não consulta outros agregados. D22: o handler precisa
    /// calcular isso dentro da transação, sob sp_getapplock 'seg:admins' exclusivo.
    /// </summary>
    public void AlterarRole(Role novoRole, bool ehUltimoAdminAtivo, DateTime agora)
    {
        UtcGuard.Exigir(agora, nameof(agora));

        if (novoRole == Role)
        {
            return; // no-op: não revoga sessões nem emite evento à toa.
        }

        GarantirNaoRemoveUltimoAdmin(ehUltimoAdminAtivo, removeAdmin: novoRole != Role.Admin);

        Role = novoRole;
        AtualizadoEmUtc = agora;
        AddDomainEvent(new UsuarioCredenciaisAlteradas(Id, MotivoAlteracaoCredenciais.RoleAlterada));
    }

    /// <summary>Desativar o último Admin ativo é proibido, venha de quem vier (mesma regra de AlterarRole).</summary>
    public void Desativar(bool ehUltimoAdminAtivo, DateTime agora)
    {
        UtcGuard.Exigir(agora, nameof(agora));

        if (!Ativo)
        {
            return; // no-op: já está inativo.
        }

        GarantirNaoRemoveUltimoAdmin(ehUltimoAdminAtivo, removeAdmin: true);

        Ativo = false;
        AtualizadoEmUtc = agora;
        AddDomainEvent(new UsuarioCredenciaisAlteradas(Id, MotivoAlteracaoCredenciais.Desativado));
    }

    public void TrocarSenha(string novoSenhaHash, DateTime agora)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(novoSenhaHash);
        UtcGuard.Exigir(agora, nameof(agora));

        SenhaHash = novoSenhaHash;
        DeveTrocarSenha = false;
        AtualizadoEmUtc = agora;
        AddDomainEvent(new UsuarioCredenciaisAlteradas(Id, MotivoAlteracaoCredenciais.SenhaTrocada));
    }

    private void GarantirNaoRemoveUltimoAdmin(bool ehUltimoAdminAtivo, bool removeAdmin)
    {
        if (Role == Role.Admin && removeAdmin && ehUltimoAdminAtivo)
        {
            throw new TransicaoInvalidaException("O último Admin ativo não pode ser rebaixado nem desativado.");
        }
    }
}
