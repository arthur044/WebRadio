import { HubConnectionBuilder, HubConnectionState } from '@microsoft/signalr'
import { useEffect } from 'react'
import { useQueryClient, type QueryClient } from '@tanstack/react-query'
import { useAlertasHarbor } from './alertasHarborStore'
import { useAuthStore } from './authStore'
import { getListenerId } from './listenerId'

const HUB_URL = '/hubs/radio'

const ATRASO_MAX_MS = 30_000

/** Backoff exponencial 0s, 2s, 4s, ... com teto de 30s — nunca desiste (nunca devolve null). */
export function atrasoDeRetry(tentativa: number): number {
  return tentativa <= 0 ? 0 : Math.min(2 ** tentativa * 1000, ATRASO_MAX_MS)
}

/**
 * `withAutomaticReconnect` só cobre queda DEPOIS de conectado, e a política padrão desiste após
 * 4 tentativas. Aqui o `start()` inicial (e a retomada após `onclose`) repete com backoff até
 * conseguir ou até `cancelado()` virar true (unmount).
 */
export async function iniciarComRetry(
  connection: Pick<import('@microsoft/signalr').HubConnection, 'start' | 'state'>,
  cancelado: () => boolean,
  esperar: (ms: number) => Promise<void> = (ms) => new Promise((r) => setTimeout(r, ms)),
): Promise<void> {
  for (let tentativa = 0; !cancelado(); tentativa++) {
    if (connection.state !== HubConnectionState.Disconnected) return
    try {
      await connection.start()
      return
    } catch {
      // Sem hub (deploy/rede) — a UI segue só com REST; tenta de novo com backoff.
    }
    await esperar(atrasoDeRetry(tentativa + 1))
  }
}

/** Teto do dedupe por eventoId (02-api-rest.md §10) — evita crescer sem limite numa aba aberta por dias. */
const MAX_EVENTOS_LEMBRADOS = 500

interface EventoBase {
  eventoId?: string
  ocorridoEmUtc: string
}

export function criarDedupe() {
  const vistos = new Set<string>()
  const ordem: string[] = []

  /** true se já processado (e marca como visto); false na 1ª vez. */
  return function jaProcessado(eventoId: string): boolean {
    if (vistos.has(eventoId)) return true
    vistos.add(eventoId)
    ordem.push(eventoId)
    if (ordem.length > MAX_EVENTOS_LEMBRADOS) {
      const maisAntigo = ordem.shift()
      if (maisAntigo !== undefined) vistos.delete(maisAntigo)
    }
    return false
  }
}

/**
 * Liga os 7 eventos do hub (02-api-rest.md §10) às invalidações de query que cada um afeta.
 * As chaves ('programa-ao-vivo', 'stream-info', ...) são o contrato entre este módulo e as
 * páginas que ainda vão consumi-las — combinar o nome ao criar cada `useQuery`.
 */
export function registrarEventos(
  connection: import('@microsoft/signalr').HubConnection,
  queryClient: QueryClient,
  jaProcessado: (eventoId: string) => boolean,
) {
  function on<T extends EventoBase>(nomeEvento: string, aoReceber: (dados: T) => void) {
    connection.on(nomeEvento, (dados: T) => {
      // Sem eventoId não dá pra deduplicar: processa sempre (invalidar é idempotente).
      if (dados.eventoId && jaProcessado(dados.eventoId)) return
      aoReceber(dados)
    })
  }

  on('ProgramaStatusAlterado', () => {
    queryClient.invalidateQueries({ queryKey: ['programa-ao-vivo'] })
    queryClient.invalidateQueries({ queryKey: ['grade-semana'] })
  })
  on('TocandoAgora', () => {
    queryClient.invalidateQueries({ queryKey: ['stream-info'] })
  })
  // O ao vivo muda a barra do player e o painel do Estúdio. Reler o `/stream/info` em vez de aplicar o
  // payload: a ordem de chegada não é garantida com mais de uma réplica (02 §10), e o REST é a fonte.
  on('TransmissaoAoVivoAlterada', () => {
    queryClient.invalidateQueries({ queryKey: ['stream-info'] })
  })
  on('PedidoCriado', () => {
    queryClient.invalidateQueries({ queryKey: ['pedidos-moderacao'] })
  })
  // Os dois vão para o `listener:{hash}` (Meus pedidos) e para `locutores` (a fila de moderação): cada tela relê a sua.
  on('PedidoModerado', () => {
    queryClient.invalidateQueries({ queryKey: ['pedidos-meus'] })
    queryClient.invalidateQueries({ queryKey: ['pedidos-moderacao'] })
  })
  on('PedidoTocado', () => {
    queryClient.invalidateQueries({ queryKey: ['pedidos-meus'] })
    queryClient.invalidateQueries({ queryKey: ['pedidos-moderacao'] })
  })
  on('MidiaSanitizada', () => {
    queryClient.invalidateQueries({ queryKey: ['midias'] })
  })
  // Não é evento de estado (06 §SignalR): o aviso vive só na memória da sessão e o `eventoId` repetido já foi
  // descartado acima.
  on('AlertaHarborFalhas', (dados) => {
    useAlertasHarbor.getState().registrar(dados)
  })
}

/**
 * Eventos podem ter se perdido durante a queda (02-api-rest.md §10): ao reconectar o cliente
 * ressincroniza por REST o que os eventos de estado mantêm em dia.
 */
export function invalidarEstadoDaRadio(queryClient: Pick<QueryClient, 'invalidateQueries'>) {
  queryClient.invalidateQueries({ queryKey: ['programa-ao-vivo'] })
  queryClient.invalidateQueries({ queryKey: ['grade-semana'] })
  queryClient.invalidateQueries({ queryKey: ['stream-info'] })
  // Pedidos criados e moderados durante a queda (E3-P02, E3-P03).
  queryClient.invalidateQueries({ queryKey: ['pedidos-meus'] })
  queryClient.invalidateQueries({ queryKey: ['pedidos-moderacao'] })
}

/**
 * E1-P07: conecta no hub `/hubs/radio` (ainda sem backend — depende do E1-F10 do Forge; até
 * lá, `connection.start()` falha e fica em silêncio, sem tempo real, sem quebrar a REST) com
 * reconexão automática. `listenerId` vai na query da conexão (o padrão do SignalR não dá pra
 * mandar em header customizado no handshake WebSocket); o token vai via `accessTokenFactory`,
 * lido da store a cada tentativa — nunca uma cópia presa no closure, porque ele pode ter
 * mudado (refresh) entre a conexão inicial e uma reconexão.
 */
export function useRealtimeHub(): void {
  const queryClient = useQueryClient()
  // A identidade do hub (papel/grupos) vem do token: nova sessão => nova conexão.
  const usuarioId = useAuthStore((estado) => estado.usuario?.id ?? null)

  useEffect(() => {
    const jaProcessado = criarDedupe()
    const connection = new HubConnectionBuilder()
      .withUrl(
        `${window.location.origin}${HUB_URL}?listenerId=${encodeURIComponent(getListenerId())}`,
        {
          accessTokenFactory: () => useAuthStore.getState().accessToken ?? '',
        },
      )
      .withAutomaticReconnect({
        nextRetryDelayInMilliseconds: (ctx) => atrasoDeRetry(ctx.previousRetryCount),
      })
      .build()

    registrarEventos(connection, queryClient, jaProcessado)

    // 02-api-rest.md §10 / E1-P07: eventos podem ter se perdido durante a queda, então ao
    // reconectar invalida explicitamente em vez de confiar só nos eventos que chegarem depois.
    connection.onreconnected(() => invalidarEstadoDaRadio(queryClient))

    let cancelado = false
    // Rede de segurança: se mesmo assim a conexão fechar, volta a tentar com backoff.
    connection.onclose(() => {
      void iniciarComRetry(connection, () => cancelado)
    })
    void iniciarComRetry(connection, () => cancelado)

    return () => {
      cancelado = true
      void connection.stop()
    }
  }, [queryClient, usuarioId])
}
