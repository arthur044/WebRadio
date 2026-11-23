import { Link } from 'react-router-dom'

export function NaoEncontradaPage() {
  return (
    <section className="mx-auto flex max-w-md flex-col items-center gap-4 px-4 py-16 text-center">
      <h1 className="font-display text-text text-2xl font-bold">Página não encontrada</h1>
      <p className="text-text-muted">O endereço não existe ou mudou de lugar.</p>
      <Link to="/" className="text-accent font-medium">
        Voltar ao Mural
      </Link>
    </section>
  )
}
