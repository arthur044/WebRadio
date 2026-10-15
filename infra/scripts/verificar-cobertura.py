#!/usr/bin/env python3
"""
WebRadio — infra/scripts/verificar-cobertura.py (E1-Z, item 6 da Definição de Pronto do 04)

A cobertura dos projetos Domain e Application precisa ser >= 80%. O `dotnet test --collect:"XPlat Code Coverage"` gera UM
coverage.cobertura.xml por projeto de teste, e cada assembly é coberto por mais de um projeto (o Application, por exemplo, também
roda nos testes da Api): a cobertura de um assembly é a UNIÃO das linhas cobertas em todos os relatórios, não a de um relatório só.

Uso:
    infra/scripts/verificar-cobertura.py <pasta-dos-relatórios> [--minimo 80] [Assembly ...]
    (padrão: WebRadio.Domain e WebRadio.Application)

Sai com 1 se algum assembly pedido ficar abaixo do mínimo, se faltar no relatório ou se não houver relatório nenhum (um passo que
não mede nada não pode passar). Escreve a tabela em $GITHUB_STEP_SUMMARY quando roda no Actions. Cobertura de LINHAS.
"""
import argparse
import collections
import glob
import os
import sys
import xml.etree.ElementTree as ET


def ler(pasta):
    """Une os relatórios: assembly -> {(arquivo, linha): acertos somados}."""
    relatorios = glob.glob(os.path.join(pasta, "**", "coverage.cobertura.xml"), recursive=True)
    linhas = collections.defaultdict(lambda: collections.defaultdict(int))
    for caminho in relatorios:
        for pacote in ET.parse(caminho).getroot().iter("package"):
            nome = pacote.get("name")
            for classe in pacote.iter("class"):
                # Cada projeto de teste grava o caminho de um jeito (relativo ou com a pasta do assembly): normaliza para o que vem
                # depois do nome do assembly, senão o mesmo arquivo contaria duas vezes.
                arquivo = classe.get("filename").replace("\\", "/").split(nome + "/")[-1]
                for linha in classe.iter("line"):
                    linhas[nome][(arquivo, int(linha.get("number")))] += int(linha.get("hits"))
    return relatorios, linhas


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("pasta")
    ap.add_argument("assemblies", nargs="*", default=["WebRadio.Domain", "WebRadio.Application"])
    ap.add_argument("--minimo", type=float, default=80.0)
    # intermixed: o argparse do Python 3.12 (runner) não retoma os posicionais depois de uma opção (`pasta --minimo 80 A B`); o 3.14 aceita.
    args = ap.parse_intermixed_args()

    relatorios, linhas = ler(args.pasta)
    if not relatorios:
        print(f"::error::Nenhum coverage.cobertura.xml em {args.pasta}: a cobertura não foi medida.")
        return 1

    resumo = [f"### Cobertura de linhas (mínimo {args.minimo:g}%, {len(relatorios)} relatórios unidos)", "",
              "| Assembly | Linhas cobertas | Cobertura | Resultado |", "|---|---|---|---|"]
    falhou = False
    for nome in args.assemblies:
        if nome not in linhas or not linhas[nome]:
            print(f"::error::{nome} não aparece em nenhum relatório de cobertura.")
            resumo.append(f"| {nome} | — | — | FALHA (ausente) |")
            falhou = True
            continue
        total = len(linhas[nome])
        cobertas = sum(1 for acertos in linhas[nome].values() if acertos > 0)
        pct = 100.0 * cobertas / total
        ok = pct >= args.minimo
        print(f"{'ok   ' if ok else 'FALHA'} {nome}: {cobertas}/{total} linhas = {pct:.1f}% (mínimo {args.minimo:g}%)")
        if not ok:
            print(f"::error::{nome}: cobertura {pct:.1f}% abaixo do mínimo de {args.minimo:g}% (item 6 da Definição de Pronto).")
            falhou = True
        resumo.append(f"| {nome} | {cobertas}/{total} | {pct:.1f}% | {'ok' if ok else 'FALHA'} |")

    destino = os.environ.get("GITHUB_STEP_SUMMARY")
    if destino:
        with open(destino, "a", encoding="utf-8") as f:
            f.write("\n".join(resumo) + "\n")
    return 1 if falhou else 0


if __name__ == "__main__":
    sys.exit(main())
