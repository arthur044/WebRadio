# Política de segurança

## Como reportar uma vulnerabilidade

**Não abra uma issue pública nem um PR com o problema.** Use o relato privado do GitHub:

1. Abra a aba **Security** do repositório [`arthur044/WebRadio`](https://github.com/arthur044/WebRadio/security).
2. Clique em **Report a vulnerability** e descreva o que encontrou: onde está, como reproduzir, o impacto que você enxerga.

O relato fica visível só para os mantenedores. Não inclua segredos reais, dados de ouvintes nem credenciais de produção;
se precisar provar acesso, descreva o caminho sem anexar o dado.

## O que esperar

- Confirmamos o recebimento do relato e dizemos se ele se confirma, com a melhor estimativa de prazo.
- Corrigimos primeiro, divulgamos depois: o aviso público só sai quando a correção estiver disponível.
- Se você quiser crédito pelo achado, diga como prefere ser citado.

## Escopo

Vale para o código deste repositório (API, Worker, front-end, `docker-compose` e scripts de `infra/`). Uma instalação
mal configurada por quem a opera (por exemplo, uma senha de exemplo mantida em produção) não é vulnerabilidade do
projeto, mas os relatos que ajudem a tornar a configuração segura por padrão são bem-vindos.

## Segredos

Nenhum segredo é versionado: os de desenvolvimento e de produção são gerados por `infra/scripts/gerar-segredos.sh`
(ou `.ps1`) e vivem em `secrets/`, fora do Git. O CI varre todo o histórico com o gitleaks. Se você achar um segredo
no repositório, reporte pelo mesmo canal acima, sem copiá-lo.
