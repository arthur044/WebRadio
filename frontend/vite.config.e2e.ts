import { mkdirSync, copyFileSync } from 'node:fs'
import { dirname } from 'node:path'
import { mergeConfig, defineConfig, type Plugin } from 'vite'
import baseConfig from './vite.config.ts'

// Só pro Playwright local (Guardian H2): adiciona a fixture do probe de refresh como
// entry extra e copia o áudio de teste do player, SEM tocar no vite.config.ts de
// produção nem no dist/ real. outDir separado (dist-e2e) pra nunca colidir com o
// build que vai pra imagem Docker.
function copyStreamFixture(): Plugin {
  return {
    name: 'copy-stream-fixture',
    closeBundle() {
      const dest = 'dist-e2e/stream/radio.mp3'
      mkdirSync(dirname(dest), { recursive: true })
      copyFileSync('e2e/fixtures/radio.mp3', dest)
    },
  }
}

export default mergeConfig(
  baseConfig,
  defineConfig({
    plugins: [copyStreamFixture()],
    build: {
      outDir: 'dist-e2e',
      rollupOptions: {
        input: {
          main: 'index.html',
          probe: 'e2e/fixtures/refresh-probe.html',
        },
      },
    },
  }),
)
