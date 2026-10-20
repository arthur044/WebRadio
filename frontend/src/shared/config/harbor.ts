/**
 * Onde o source client (BUTT, Mixxx) conecta. A porta do ao vivo não passa pelo nginx do site: fica no
 * `harbor-borda`, publicado só no IP da VPN (06 §E2-F11), então o endereço vem da configuração do build.
 * Sem `VITE_HARBOR_HOST` a tela manda o locutor pedir o endereço à administração em vez de inventar um.
 */
export const HARBOR = {
  host: (import.meta.env.VITE_HARBOR_HOST as string | undefined) || null,
  porta: (import.meta.env.VITE_HARBOR_PORT as string | undefined) || '8005',
  /** O `input.harbor("live", ...)` do radio.liq. */
  mount: '/live',
} as const
