import { Route, Routes } from 'react-router-dom'
import { RootLayout } from './shared/ui/RootLayout'
import { MuralPage } from './features/mural/MuralPage'
import { GradePage } from './features/grade/GradePage'
import { PedidosPage } from './features/pedidos/PedidosPage'
import { EstudioPage } from './features/estudio/EstudioPage'

function App() {
  return (
    <Routes>
      <Route element={<RootLayout />}>
        <Route path="/" element={<MuralPage />} />
        <Route path="/grade" element={<GradePage />} />
        <Route path="/pedidos" element={<PedidosPage />} />
        <Route path="/estudio" element={<EstudioPage />} />
      </Route>
    </Routes>
  )
}

export default App
