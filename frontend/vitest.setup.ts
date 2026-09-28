import '@testing-library/jest-dom/vitest'

// jsdom não implementa a Web Locks API (todos os browsers reais suportam desde
// 2022). Polyfill mínimo só pra rodar o single-flight do httpClient sob teste —
// serializa por nome de lock, igual ao navigator.locks.request real.
if (!('locks' in navigator)) {
  const queues = new Map<string, Promise<unknown>>()
  Object.defineProperty(navigator, 'locks', {
    configurable: true,
    value: {
      request: (name: string, callback: () => Promise<unknown>) => {
        const previous = queues.get(name) ?? Promise.resolve()
        const current = previous.then(callback, callback)
        queues.set(
          name,
          current.catch(() => undefined),
        )
        return current
      },
    },
  })
}
