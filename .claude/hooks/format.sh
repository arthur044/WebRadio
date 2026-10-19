#!/usr/bin/env bash
# PostToolUse (Edit|Write|MultiEdit): formata o arquivo que o agente acabou de mudar.
# Nunca bloqueia o agente: falha de formatação só vira aviso no stderr.
set -uo pipefail

command -v jq >/dev/null || { echo "format-hook: jq ausente, formatação ignorada" >&2; exit 0; }

file=$(jq -r '.tool_input.file_path // empty')
[[ -n "$file" && -f "$file" ]] || exit 0

# A raiz vem do próprio arquivo: CLAUDE_PROJECT_DIR pode ser a pasta da role do Maestri, não o worktree.
root=$(git -C "$(dirname "$file")" rev-parse --show-toplevel 2>/dev/null || true)
[[ -n "$root" ]] || root="${CLAUDE_PROJECT_DIR:-}"
[[ -n "$root" ]] || exit 0
rel="${file#"$root"/}"

case "$rel" in
  frontend/*.ts|frontend/*.tsx|frontend/*.js|frontend/*.jsx|frontend/*.css|frontend/*.json|frontend/*.md)
    (cd "$root/frontend" && npx --no-install prettier --write --log-level warn "${rel#frontend/}") \
      || echo "format-hook: prettier falhou em $rel" >&2
    ;;
  backend/*.cs)
    dotnet format whitespace "$root/backend" --folder --include "${rel#backend/}" \
      || echo "format-hook: dotnet format falhou em $rel" >&2
    ;;
esac
exit 0
