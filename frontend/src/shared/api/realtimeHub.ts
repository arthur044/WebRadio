import { HubConnectionBuilder } from '@microsoft/signalr'
import { useEffect } from 'react'
import { useQueryClient, type QueryClient } from '@tanstack/react-query'
import { useAuthStore } from './authStore'
import { getListenerId } from './listenerId'

const HUB_URL = '/hubs/radio'

/** Teto do dedupe por eventoId (02-api-rest.md §10) — evita crescer sem limite numa aba aberta por dias. */
const MAX_EVENTOS_LEMBRADOS = 500

interface EventoBase {
  eventoId: string
  ocorridoEmUtc: string
}

function criarDedupe() {
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
 * Liga os 6 eventos do hub (02-api-rest.md §10) às invalidações de query que cada um afeta.
 * As chaves ('programa-ao-vivo', 'stream-info', ...) são o contrato entre este módulo e as
 * páginas que ainda vão consumi-las — combinar o nome ao criar cada `useQuery`.
 */
function registrarEventos(
  connection: import('@microsoft/signalr').HubConnection,
  queryClient: QueryClient,
  jaProcessado: (eventoId: string) => boolean,
) {
  function on<T extends EventoBase>(nomeEvento: string, aoReceber: (dados: T) => void) {
    connection.on(nomeEvento, (dados: T) => {
      if (jaProcessado(dados.eventoId)) return
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
  on('PedidoCriado', () => {
    queryClient.invalidateQueries({ queryKey: ['pedidos-moderacao'] })
  })
  on('PedidoModerado', () => {
    queryClient.invalidateQueries({ queryKey: ['pedidos-meus'] })
  })
  on('PedidoTocado', () => {
    queryClient.invalidateQueries({ queryKey: ['pedidos-meus'] })
  })
  on('MidiaSanitizada', () => {
    queryClient.invalidateQueries({ queryKey: ['midias'] })
  })
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

  useEffect(() => {
    const jaProcessado = criarDedupe()
    const connection = new HubConnectionBuilder()
      .withUrl(
        `${window.location.origin}${HUB_URL}?listenerId=${encodeURIComponent(getListenerId())}`,
        {
          accessTokenFactory: () => useAuthStore.getState().accessToken ?? '',
        },
      )
      .withAutomaticReconnect()
      .build()

    registrarEventos(connection, queryClient, jaProcessado)

    // 02-api-rest.md §10 / E1-P07: eventos podem ter se perdido durante a queda, então ao
    // reconectar invalida explicitamente em vez de confiar só nos eventos que chegarem depois.
    connection.onreconnected(() => {
      queryClient.invalidateQueries({ queryKey: ['programa-ao-vivo'] })
      queryClient.invalidateQueries({ queryKey: ['stream-info'] })
    })

    connection.start().catch(() => {
      // Sem hub ainda (F10 não mergeado) ou rede fora — a UI segue funcionando só com REST.
    })

    return () => {
      void connection.stop()
    }
  }, [queryClient])
}
