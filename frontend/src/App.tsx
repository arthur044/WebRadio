import { Route, Routes } from 'react-router-dom'
import { RootLayout } from './shared/ui/RootLayout'
import { MuralPage } from './features/mural/MuralPage'
import { GradePage } from './features/grade/GradePage'
import { PedidosPage } from './features/pedidos/PedidosPage'
import { EstudioPage } from './features/estudio/EstudioPage'
import { LoginPage } from './features/auth/LoginPage'
import { TrocarSenhaPage } from './features/auth/TrocarSenhaPage'
import { RequireRole } from './features/auth/RequireRole'

function App() {
  return (
    <Routes>
      <Route element={<RootLayout />}>
        <Route path="/" element={<MuralPage />} />
        <Route path="/grade" element={<GradePage />} />
        <Route path="/pedidos" element={<PedidosPage />} />
        <Route
          path="/estudio"
          element={
            <RequireRole roles={['Locutor', 'Admin']}>
              <EstudioPage />
            </RequireRole>
          }
        />
        <Route path="/login" element={<LoginPage />} />
        <Route path="/trocar-senha" element={<TrocarSenhaPage />} />
      </Route>
    </Routes>
  )
}

export default App
