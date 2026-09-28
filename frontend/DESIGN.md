# WebRadio — Direção visual (E1-P02)

> Autor: Prism (Frontend) · 2026-09-27 · Pendente de revisão do DJ

## Direção: "Console de estúdio"

Nada de dashboard SaaS genérico. A referência é a mesa de transmissão de uma rádio:
superfícies escuras e foscas, um único acento de sinal (o vermelho de "NO AR"), tipografia
condensada para números e estados (horário, "AO VIVO", contadores) e uma sans humanista para
leitura longa (Mural, descrições de programa).

Por que não "clean minimal": o produto inteiro gira em torno de **um estado binário e
crítico** — está ou não está ao ar — e isso pede um vocabulário visual de estúdio/broadcast,
não de produtividade corporativa.

## Paleta

Definida em OKLCH (uniformidade perceptual entre claro/escuro) e exposta como tokens
`--color-*` no `@theme` do Tailwind v4 (`src/index.css`), com os mesmos nomes redefinidos em
`@media (prefers-color-scheme: dark)`.

| Token | Claro | Escuro | Uso |
|---|---|---|---|
| `--color-surface` | `oklch(97% 0.006 80)` | `oklch(16% 0.012 280)` | fundo da página |
| `--color-surface-raised` | `oklch(100% 0 0)` | `oklch(21% 0.014 280)` | cards, PlayerBar, mesa |
| `--color-border` | `oklch(88% 0.008 80)` | `oklch(30% 0.016 280)` | divisórias |
| `--color-text` | `oklch(20% 0.01 80)` | `oklch(94% 0.01 80)` | texto principal |
| `--color-text-muted` | `oklch(48% 0.01 80)` | `oklch(68% 0.012 280)` | texto secundário |
| `--color-on-air` | `oklch(55% 0.21 25)` | `oklch(62% 0.22 25)` | selo "AO VIVO", sinal, foco de erro |
| `--color-accent` | `oklch(58% 0.14 250)` | `oklch(72% 0.13 250)` | ações primárias, links |
| `--color-accent-contrast` | `oklch(99% 0 0)` | `oklch(12% 0.01 280)` | texto sobre `--color-accent`/`--color-on-air` |

`--color-on-air` nunca é reaproveitado para nada que não seja o sinal de transmissão ativa ou
um erro bloqueante — é o único vermelho da paleta, para preservar o significado.

## Tipografia

Duas famílias (limite do `web/performance.md`):

- **Display / técnica** — `Space Grotesk` (condensada o suficiente para números e selos:
  horário da grade, "AO VIVO", contador de ouvintes). `font-display: swap`, só os pesos 500 e
  700 carregados.
- **Leitura** — `Inter` (Mural, formulário de pedido, textos de programa). Só o peso 400 e 600.

Ambas **auto-hospedadas** em `src/assets/fonts/` (`@font-face` em `src/index.css`), não via
CDN do Google: a CSP normativa do Épico 1 (`05-seguranca-auditoria.md` S-M14) restringe
`style-src`/`font-src` a `'self'`, então `fonts.googleapis.com`/`fonts.gstatic.com` não
passariam no console do Playwright do P06.

Escala tipográfica com `clamp()` (`--text-*` tokens), igual ao padrão de
`web/coding-style.md`.

## Modo claro/escuro

Ambos intencionais, não é o escuro "invertendo" o claro:

- Claro = estúdio de manhã: superfícies levemente quentes, sombras baixas, muita luz.
- Escuro = transmissão noturna: quase preto com viés frio-azulado, o `--color-on-air`
  ganha um leve glow (`box-shadow` com a própria cor) porque é o que se vê primeiro numa
  cabine escura.

Segue `prefers-color-scheme`; não há toggle manual no Épico 1 (fora de escopo).

## Motion

Só propriedades de compositor (`transform`, `opacity`) — nunca `width`/`top`/`margin`. O selo
"AO VIVO" pulsa (`opacity` 1↔0.55, 1.6s, `ease-in-out`, respeita
`prefers-reduced-motion: reduce` → sem animação, só o texto).

## Componentes-base que já usam os tokens

- `PlayerBar` (`src/features/player`): `--color-surface-raised`, `--color-on-air` no indicador.
- Selo "AO VIVO" (`src/shared/ui`): `--color-on-air` + `--color-accent-contrast`.
- Botões primários: `--color-accent`.
