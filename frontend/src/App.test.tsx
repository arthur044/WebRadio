import { render, screen } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router-dom'
import { describe, expect, it } from 'vitest'
import App from './App'

function renderApp() {
  const queryClient = new QueryClient()
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter>
        <App />
      </MemoryRouter>
    </QueryClientProvider>,
  )
}

describe('App', () => {
  it('renders the Mural route by default', () => {
    renderApp()
    expect(screen.getByRole('heading', { name: 'Mural' })).toBeInTheDocument()
  })

  it('renders the persistent player bar', () => {
    renderApp()
    expect(screen.getByTestId('player-bar')).toBeInTheDocument()
  })
})
