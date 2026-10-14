#!/usr/bin/env bash
# =============================================================================
# WebRadio — infra/scripts/verificar-paridade-segredos.sh (B3-10a)
#
# A lista de segredos e a receita de cada um existem em DOIS lugares mantidos à mão (gerar-segredos.sh e .ps1), e o compose
# declara os arquivos que monta. Um segredo novo que entra só em um deles passaria despercebido até alguém no Windows (ou no
# compose) esbarrar nele. Este script falha se:
#   - o .sh e o .ps1 não têm os MESMOS nomes de segredo com a MESMA receita (tipo e número de bytes);
#   - o `secrets:` do docker-compose.yml não declara exatamente os arquivos que o gerador cria.
# Roda no job `seguranca` do CI (só precisa de bash e python3) e localmente na raiz do repositório.
# =============================================================================
set -euo pipefail
export LC_ALL=C

RAIZ="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$RAIZ"

python3 -I - infra/scripts/gerar-segredos.sh infra/scripts/gerar-segredos.ps1 docker-compose.yml <<'PY'
import re, sys

sh_arquivo, ps_arquivo, compose_arquivo = sys.argv[1:4]
erros = []

# --- .sh: bloco <<'LISTA' ... LISTA, linhas nome=TIPO:N
sh = {}
dentro = False
for linha in open(sh_arquivo, encoding="utf-8"):
    linha = linha.rstrip("\n")
    if "<<'LISTA'" in linha:
        dentro = True
    elif linha == "LISTA":
        dentro = False
    elif dentro and "=" in linha:
        nome, receita = linha.split("=", 1)
        sh[nome] = receita

# --- .ps1: 'nome' = { New-StrongSecret -Bytes N } | { New-HexSecret -Bytes N } | { "locutor-$(New-HexSecret -Bytes N)" }
ps = {}
for linha in open(ps_arquivo, encoding="utf-8-sig"):
    m = re.match(r"""\s*'([a-z_]+)'\s*=\s*\{\s*(.+?)\s*\}\s*$""", linha)
    if not m:
        continue
    nome, corpo = m.groups()
    if (r := re.fullmatch(r"New-StrongSecret -Bytes (\d+)", corpo)):
        ps[nome] = f"B64:{r.group(1)}"
    elif (r := re.fullmatch(r"New-HexSecret -Bytes (\d+)", corpo)):
        ps[nome] = f"HEX:{r.group(1)}"
    elif (r := re.fullmatch(r'"locutor-\$\(New-HexSecret -Bytes (\d+)\)"', corpo)):
        ps[nome] = f"HEXUSER:{r.group(1)}"
    else:
        erros.append(f".ps1: receita não reconhecida para {nome}: {corpo}")

if not sh:
    erros.append(".sh: nenhum segredo lido (o bloco LISTA mudou de formato?)")
if not ps:
    erros.append(".ps1: nenhum segredo lido (o formato de $Receitas mudou?)")

for nome in sorted(set(sh) | set(ps)):
    if nome not in ps:
        erros.append(f"{nome}: está no .sh e falta no .ps1")
    elif nome not in sh:
        erros.append(f"{nome}: está no .ps1 e falta no .sh")
    elif sh[nome] != ps[nome]:
        erros.append(f"{nome}: receita diferente (.sh={sh[nome]}, .ps1={ps[nome]})")

# --- compose: secrets: do topo (nome: { file: ./secrets/nome }). seed_admin_email é gravado à parte pelos dois geradores.
compose = set()
dentro = False
for linha in open(compose_arquivo, encoding="utf-8"):
    if linha.startswith("secrets:"):
        dentro = True
        continue
    if dentro:
        if linha and not linha.startswith((" ", "#", "\n")):
            break
        m = re.match(r"\s{2}([a-z_]+):\s*\{\s*file:\s*\./secrets/([a-z_]+)\s*\}", linha)
        if m:
            if m.group(1) != m.group(2):
                erros.append(f"compose: o segredo {m.group(1)} aponta para o arquivo {m.group(2)}")
            compose.add(m.group(2))

gerados = set(sh) | {"seed_admin_email"}
for nome in sorted(gerados - compose):
    erros.append(f"{nome}: o gerador cria e o compose não declara")
for nome in sorted(compose - gerados):
    erros.append(f"{nome}: o compose declara e o gerador não cria")

if erros:
    print("Paridade dos segredos QUEBRADA:", file=sys.stderr)
    for e in erros:
        print(f"  - {e}", file=sys.stderr)
    sys.exit(1)
print(f"paridade ok: {len(sh)} segredos com a mesma receita no .sh e no .ps1, mais seed_admin_email; o compose declara os mesmos {len(compose)} arquivos.")
PY
