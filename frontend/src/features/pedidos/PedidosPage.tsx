export function PedidosPage() {
  return (
    <section>
      <h1 className="font-display text-2xl font-bold">Pedir Música</h1>
      <form className="mt-4 flex max-w-sm flex-col gap-3" aria-disabled="true">
        <input
          type="text"
          placeholder="Seu nome"
          disabled
          className="border-border rounded-md border px-3 py-2"
        />
        <input
          type="text"
          placeholder="Música"
          disabled
          className="border-border rounded-md border px-3 py-2"
        />
        <button
          type="button"
          disabled
          className="bg-accent text-accent-contrast rounded-md px-3 py-2 opacity-50"
        >
          Em breve
        </button>
      </form>
    </section>
  )
}
