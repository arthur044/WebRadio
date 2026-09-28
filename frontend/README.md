# WebRadio — Frontend

Frontend da plataforma WebRadio: SPA em **React 19** + **Vite** + **TypeScript** + **Tailwind CSS** + **React Router**.

## Stack

| Tecnologia | Versão | Propósito |
|---|---|---|
| React | 19.2.8 | UI e state management |
| Vite | 8.3.0 | Bundler e dev server |
| TypeScript | ~6.0.2 | Type safety |
| Tailwind CSS | 4.3.3 | Design tokens e utility-first CSS |
| React Router | 7.18.4 | Roteamento SPA |
| TanStack Query | 5.104.0 | Fetch e cache de dados do servidor |
| Zustand | 5.0.15 | State local (player, UI) |
| ESLint | 10.11.0 | Linting JS/TS |
| Prettier | 3.9.9 | Formatação de código |
| Vitest | 5.0.2 | Unit tests |
| Playwright | 1.63.0 | E2E tests |

## Scripts npm

```bash
# Desenvolvimento
npm run dev              # Inicia Vite dev server em http://localhost:5173

# Compilação
npm run build           # TypeScript + Vite build para `dist/`
npm run preview         # Visualiza o build localmente (sem Hot Module Reload)

# Qualidade
npm run lint            # ESLint em todo `src/`
npm run format          # Prettier em todo `src/` (overwrite)
npm run format:check    # Prettier sem overwrite (apenas valida)

# Testes
npm test                # Vitest run (unitários)
npm run test:watch      # Vitest watch (reexecuta ao salvar)
npm run test:coverage   # Vitest com cobertura

# E2E
npm run e2e             # Playwright test (alias para test:e2e)
npm run test:e2e        # Playwright test
npm run build:e2e       # Build específico para E2E
```

## Estrutura de pastas

```
src/
├── features/               # Por feature do negócio
│   ├── auth/              # Login, logout, troca de senha
│   ├── player/            # Player de áudio global
│   ├── grade/             # Grade de programas
│   ├── pedidos/           # Sistema de pedidos musicais
│   ├── mural/             # Feed de divulgações
│   └── estudio/           # Painel do locutor (protegido)
├── shared/                # Reutilizável entre features
│   ├── api/               # Cliente HTTP, tipos
│   ├── lib/               # Utilitários (formatação, hash, timezone)
│   └── ui/                # Componentes base
└── assets/
    ├── fonts/             # Web fonts customizadas
    └── images/            # Logos, ícones

tests/
├── unit/                  # Vitest (unitários)
└── e2e/                   # Playwright (e2e)
```

## Desenvolvimento local

```bash
npm install              # Instalar dependências
npm run dev              # Dev server em http://localhost:5173
npm run lint             # Verificar qualidade
npm test                 # Testes unitários
npm run test:e2e         # Testes E2E
```

## Design e direção visual

Consulte [`./DESIGN.md`](./DESIGN.md) para paleta de cores, tipografia, tokens CSS e componentes-base.

## Build e deploy

```bash
npm run build            # Build otimizado para `dist/`
npm run preview          # Testa build localmente
```

## Troubleshooting

| Problema | Solução |
|----------|---------|
| Alias TypeScript `@/` não funciona | Verifique `tsconfig.json` paths |
| Player reinicia ao navegar | `<PlayerBar />` deve estar fora de `<Outlet />` |
| CORS error em dev | Configure `vite.config.ts` proxy para `/api` |
| E2E com Playwright falhando | Rode `npm run build:e2e` antes de `npm run test:e2e` |

## Contribuição

Siga [`../docs/processo-de-desenvolvimento.md`](../docs/processo-de-desenvolvimento.md):
- Branch: `prism/<tarefa-id>`
- Commits só de seus arquivos
- PR contra `main` com testes verdes

---

**Versão**: v1.1 (Épico 1)  
**Última atualização**: 2026-09-28
