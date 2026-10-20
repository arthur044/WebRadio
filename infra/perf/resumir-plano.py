#!/usr/bin/env python3
"""
E2-A02 (Atlas): resume a saída do sqlcmd de medir-a02.sql.

Para cada cenário ("### ..."), imprime o STATISTICS IO (uma linha por tabela) e, de cada plano real (SET STATISTICS XML),
a árvore de operadores com índice, predicados de busca, linhas estimadas × reais, linhas lidas e leituras lógicas reais.

Uso: python3 resumir-plano.py saida.txt [prefixo-do-cenario ...]
"""
import re
import sys
import xml.etree.ElementTree as ET

NS = {"s": "http://schemas.microsoft.com/sqlserver/2004/07/showplan"}
Q = "{http://schemas.microsoft.com/sqlserver/2004/07/showplan}"


def filhos_relop(elem):
    for c in elem:
        if c.tag == Q + "RelOp":
            yield c
        else:
            yield from filhos_relop(c)


def soma(relop, attr):
    total, achou = 0, False
    info = relop.find(Q + "RunTimeInformation")
    if info is None:
        return None
    for c in info.iter(Q + "RunTimeCountersPerThread"):
        if attr in c.attrib:
            total += int(c.attrib[attr])
            achou = True
    return total if achou else None


def col(c):
    return c.attrib.get("Column", "?")


def busca(relop):
    """Predicados de busca do IndexScan/IndexSeek, no formato 'Chave op, ...'. Vazio = varredura (sem busca)."""
    partes = []
    for sp in relop.iter(Q + "SeekPredicateNew"):
        for tipo in ("Prefix", "StartRange", "EndRange"):
            for n in sp.iter(Q + tipo):
                cols = ",".join(col(c) for c in n.find(Q + "RangeColumns"))
                partes.append(f"{tipo}[{n.attrib.get('ScanType', '')}] {cols}")
    return "; ".join(partes)


def imprimir_relop(r, nivel, saida):
    op = r.attrib["PhysicalOp"]
    obj = next(iter(r.iter(Q + "Object")), None)
    idx = ""
    if obj is not None and "Index" in op and "Index" in obj.attrib:
        idx = f" {obj.attrib['Table']}.{obj.attrib['Index']}"
    est = r.attrib.get("EstimateRows", "?")
    estlidas = r.attrib.get("EstimatedRowsRead")
    atual = soma(r, "ActualRows")
    lidas = soma(r, "ActualRowsRead")
    lr = soma(r, "ActualLogicalReads")
    execs = soma(r, "ActualExecutions")
    m = f"est={float(est):g}" + (f"/lidas~{float(estlidas):g}" if estlidas else "") + f" real={atual}"
    if lidas is not None:
        m += f" lidas={lidas}"
    if lr:
        m += f" leit.lógicas={lr}"
    if execs is not None and execs != 1:
        m += f" exec={execs}"
    saida.append("  " * nivel + f"- {op}{idx}  [{m}]")
    if op.startswith(("Index Seek", "Index Scan", "Clustered Index Seek", "Clustered Index Scan")):
        b = busca(r)
        saida.append("  " * (nivel + 1) + (f"busca: {b}" if b else "busca: nenhuma (varredura)"))
        pred = None
        for p in r.findall(Q + "IndexScan/" + Q + "Predicate/" + Q + "ScalarOperator"):
            pred = p.attrib.get("ScalarString")
        if pred:
            pred = re.sub(r"\[WebRadioPerf\]\.\[(grade|seg|infra)\]\.\[\w+\] as \[(\w+)\]\.", r"\2.", pred)
            pred = re.sub(r"\[(\w+)\]", r"\1", pred)
            saida.append("  " * (nivel + 1) + f"residual: {pred[:260]}")
    for f in filhos_relop(r):
        imprimir_relop(f, nivel + 1, saida)


def resumir_plano(xml, saida):
    root = ET.fromstring(xml)
    for st in root.iter(Q + "StmtSimple"):
        qp = st.find(Q + "QueryPlan")
        if qp is None:
            continue
        raiz = next(iter(filhos_relop(qp)), None)
        if raiz is None:
            continue
        txt = re.sub(r"\s+", " ", st.attrib.get("StatementText", "")).strip()
        saida.append(f"plano: {txt[:150]}")
        extra = []
        if "CardinalityEstimationModelVersion" in st.attrib:
            extra.append(f"CE={st.attrib['CardinalityEstimationModelVersion']}")
        extra.append(f"custo={float(st.attrib.get('StatementSubTreeCost', 0)):.4g}")
        for pc in qp.iter(Q + "ColumnReference"):
            if "ParameterCompiledValue" in pc.attrib:
                extra.append(f"{pc.attrib['Column']}: compilado={pc.attrib['ParameterCompiledValue']}"
                             f" executado={pc.attrib.get('ParameterRuntimeValue', '?')}")
        saida.append("  " + " | ".join(extra))
        for w in qp.iter(Q + "PlanAffectingConvert"):
            saida.append(f"  AVISO conversão: {w.attrib.get('ConvertIssue')} {w.attrib.get('Expression')}")
        imprimir_relop(raiz, 1, saida)


def resumir_io(trecho):
    linhas = []
    for m in re.finditer(r"Table '([^']+)'\. Scan count (\d+), logical reads (\d+), physical reads (\d+), page server reads \d+, "
                         r"read-ahead reads (\d+)", trecho):
        tabela = "#temp" if m.group(1).startswith("#") else m.group(1)
        linhas.append(f"  {tabela}: scan={m.group(2)} lógicas={m.group(3)} físicas={m.group(4)} read-ahead={m.group(5)}")
    return linhas


def main():
    texto = open(sys.argv[1], encoding="utf-8", errors="replace").read()
    filtros = sys.argv[2:]
    partes = re.split(r"^### ", texto, flags=re.M)[1:]
    for p in partes:
        titulo, _, corpo = p.partition("\n")
        if filtros and not any(titulo.startswith(f) for f in filtros):
            continue
        print(f"## {titulo}")
        # remove os planos do trecho de IO para não confundir
        sem_xml = re.sub(r"<ShowPlanXML.*?</ShowPlanXML>", "", corpo, flags=re.S)
        io = resumir_io(sem_xml)
        if io:
            print("STATISTICS IO:")
            print("\n".join(io))
        for i, m in enumerate(re.finditer(r"<ShowPlanXML.*?</ShowPlanXML>", corpo, flags=re.S), 1):
            saida = []
            resumir_plano(m.group(0), saida)
            if saida:
                print(f"[plano {i}]")
                print("\n".join(saida))
        print()


if __name__ == "__main__":
    main()
