using Microsoft.EntityFrameworkCore;
using WebRadio.Infrastructure.Persistencia;

namespace WebRadio.Infrastructure.Trabalhos;

public sealed record ResultadoDaManutencao(int PedidosAnonimizados, int EventosApagados, int RefreshApagados, int CredenciaisApagadas,
    int IpsApagados = 0, int EventosDePedidoApagados = 0);

public interface IManutencao
{
    Task<ResultadoDaManutencao> ExecutarAsync(CancellationToken ct);
}

/// <summary>
/// Rotinas da seção 11 do <c>03-schema-sqlserver.sql</c>, em lotes de 4.000 (abaixo do limiar de ~5.000 locks que escala o lock para a tabela; o DELETE toca o clustered e os índices).
/// O "agora" é o do SQL (<c>SYSUTCDATETIME()</c>), não o do contêiner. Tudo é idempotente: rodar duas vezes, ou em
/// dois workers ao mesmo tempo, não muda o resultado (a liderança só evita trabalho dobrado).
/// </summary>
public sealed class Manutencao(RadioDbContext db) : IManutencao
{
    public const int LotePadrao = 4000;

    /// <summary>Nome que fica no pedido terminal depois de 30 dias (07, E3-D8): a coluna <c>NomeOuvinte</c> é <c>NOT NULL</c>.</summary>
    public const string NomeAnonimo = "(anônimo)";

    public int Lote { get; init; } = LotePadrao;

    public async Task<ResultadoDaManutencao> ExecutarAsync(CancellationToken ct)
    {
        // LGPD, retenção do pedido de música (07, E3-D8). (1) O hash do IP só serve para investigar abuso: some aos 7 dias (o throttle olha 5 min).
        var ips = await EmLotes(() => db.Database.ExecuteSqlAsync($"""
            UPDATE TOP ({Lote}) interacao.PedidoMusica SET ListenerIpHash = NULL
            WHERE ListenerIpHash IS NOT NULL AND CriadoEmUtc < DATEADD(DAY, -7, SYSUTCDATETIME());
            """, ct));

        // (2) Aos 30 dias, no mesmo lote que anula o hash do dispositivo, os pedidos TERMINAIS perdem o que identifica a pessoa: usuário,
        // dedicatória e nome (a coluna é NOT NULL, então '(anônimo)'). Título, artista, status e datas ficam, para estatística. Terminal =
        // Rejeitado (3), Tocado (4), Expirado (5) e Aprovado (2) moderado há mais de 24 h (E3-D9: sem mídia no Épico 3, ninguém o marcaria
        // tocado). Pendente e Aprovado recente mantêm o texto. O predicado só casa o que ainda tem algo a limpar: idempotente.
        var anonimizados = await EmLotes(() => db.Database.ExecuteSqlAsync($"""
            UPDATE TOP ({Lote}) interacao.PedidoMusica SET
                ListenerDeviceHash = NULL,
                ListenerIpHash = NULL,
                UsuarioId = CASE WHEN Status IN (3, 4, 5) OR (Status = 2 AND ModeradoEmUtc < DATEADD(HOUR, -24, SYSUTCDATETIME())) THEN NULL ELSE UsuarioId END,
                Mensagem = CASE WHEN Status IN (3, 4, 5) OR (Status = 2 AND ModeradoEmUtc < DATEADD(HOUR, -24, SYSUTCDATETIME())) THEN NULL ELSE Mensagem END,
                NomeOuvinte = CASE WHEN Status IN (3, 4, 5) OR (Status = 2 AND ModeradoEmUtc < DATEADD(HOUR, -24, SYSUTCDATETIME())) THEN {NomeAnonimo} ELSE NomeOuvinte END
            WHERE CriadoEmUtc < DATEADD(DAY, -30, SYSUTCDATETIME())
              AND (ListenerDeviceHash IS NOT NULL OR ListenerIpHash IS NOT NULL
                   OR ((Status IN (3, 4, 5) OR (Status = 2 AND ModeradoEmUtc < DATEADD(HOUR, -24, SYSUTCDATETIME())))
                       AND (UsuarioId IS NOT NULL OR Mensagem IS NOT NULL OR NomeOuvinte <> {NomeAnonimo})));
            """, ct));

        // (3) Evento de pedido (nome e dedicatória no payload) sai com 30 dias, ENTREGUE OU PARADO: o outbox não guarda texto de ouvinte sem prazo.
        var eventosDePedido = await EmLotes(() => db.Database.ExecuteSqlAsync($"""
            DELETE TOP ({Lote}) FROM infra.EventoOutbox
            WHERE Tipo IN (N'PedidoCriado', N'PedidoModerado', N'PedidoTocado') AND CriadoEmUtc < DATEADD(DAY, -30, SYSUTCDATETIME());
            """, ct));

        // Só o que já foi entregue: evento parado (ProcessadoEmUtc nulo) fica para análise (01 §2.8).
        var eventos = await EmLotes(() => db.Database.ExecuteSqlAsync($"""
            DELETE TOP ({Lote}) FROM infra.EventoOutbox WHERE ProcessadoEmUtc < DATEADD(DAY, -7, SYSUTCDATETIME());
            """, ct));

        // FK_RefreshToken_SubstituidoPor é RESTRICT e o lote não tem ordem: um sucessor apagado antes do antecessor
        // violaria a FK. Primeiro os vencidos soltam o ponteiro; depois sai quem ninguém mais aponta. Um vencido ainda
        // apontado por token que NÃO venceu (duração do refresh mudou entre emissões) fica para outro dia.
        await EmLotes(() => db.Database.ExecuteSqlAsync($"""
            UPDATE TOP ({Lote}) seg.RefreshToken SET SubstituidoPorId = NULL
            WHERE SubstituidoPorId IS NOT NULL AND ExpiraEmUtc < DATEADD(DAY, -1, SYSUTCDATETIME());
            """, ct));
        var refresh = await EmLotes(() => db.Database.ExecuteSqlAsync($"""
            DELETE TOP ({Lote}) t FROM seg.RefreshToken AS t
            WHERE t.ExpiraEmUtc < DATEADD(DAY, -1, SYSUTCDATETIME())
              AND NOT EXISTS (SELECT 1 FROM seg.RefreshToken AS a WHERE a.SubstituidoPorId = t.Id);
            """, ct));

        // E2-F09: a credencial de transmissão revogada há mais de 90 dias não serve mais nem de histórico. Nenhuma tabela a
        // referencia, então um DELETE simples em lotes basta (sem o passo duplo do RefreshToken).
        var credenciais = await EmLotes(() => db.Database.ExecuteSqlAsync($"""
            DELETE TOP ({Lote}) FROM seg.CredencialTransmissao WHERE RevogadaEmUtc < DATEADD(DAY, -90, SYSUTCDATETIME());
            """, ct));

        return new ResultadoDaManutencao(anonimizados, eventos, refresh, credenciais, ips, eventosDePedido);
    }

    private async Task<int> EmLotes(Func<Task<int>> passo)
    {
        var total = 0;
        int n;
        do
        {
            n = await passo();
            total += n;
        } while (n == Lote);
        return total;
    }
}
