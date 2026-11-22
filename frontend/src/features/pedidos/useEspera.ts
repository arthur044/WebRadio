import { useCallback, useEffect, useState } from 'react'

/**
 * Contagem regressiva em segundos a partir de um prazo absoluto (`Date.now() + espera`), e não de um contador
 * decrementado a cada tick: uma aba em segundo plano atrasa os timers, e o prazo continua certo quando ela volta.
 */
export function useEspera() {
  const [prazo, setPrazo] = useState<number | null>(null)
  const [agora, setAgora] = useState(() => Date.now())

  useEffect(() => {
    if (prazo === null) return
    const id = setInterval(() => {
      const t = Date.now()
      setAgora(t)
      // Venceu: encerra a contagem (e este intervalo) em vez de deixar um prazo morto girando.
      if (t >= prazo) setPrazo(null)
    }, 1000)
    return () => clearInterval(id)
  }, [prazo])

  const restante = prazo === null ? 0 : Math.max(0, Math.ceil((prazo - agora) / 1000))

  const iniciar = useCallback((segundos: number) => {
    const t = Date.now()
    setAgora(t)
    setPrazo(t + segundos * 1000)
  }, [])

  const parar = useCallback(() => setPrazo(null), [])

  return { restante, iniciar, parar }
}

/** `272` -> `4:32`; de 1 h em diante, `5400` -> `1:30:00` (a espera do limite de envios chega a 2 h). */
export function formatarEspera(segundos: number): string {
  const h = Math.floor(segundos / 3600)
  const min = Math.floor((segundos % 3600) / 60)
  const seg = segundos % 60
  const ss = String(seg).padStart(2, '0')
  if (h === 0) return `${min}:${ss}`
  return `${h}:${String(min).padStart(2, '0')}:${ss}`
}
