# `AUTH_401_TAXA`: o que o alerta de 401 do login vê e não vê

Alerta do `/auth/login` (R1, #9). Um log estruturado por minuto, só quando houve 401 no minuto.

## Como aparece

- Log da Api: `AUTH_401_TAXA total=<n> janelaSegundos=60 porSegundo=<x>` (a média da janela de 60 s).
- `Information` abaixo de 1 por segundo; `Warning` a partir de 1 por segundo.
- Conta só o status final 401 do login (por nome de endpoint, então `/login/` e `/LOGIN` contam). 429 e 503 não contam.

```bash
docker compose logs --since 1h api | grep AUTH_401_TAXA
```

## O que ele NÃO pega (limite conhecido, Sentinel no #162)

O login é limitado a **30/min por IP** (0,5/s), por causa de ouvintes atrás de NAT. Logo, **um IP sozinho nunca chega a 1 por segundo**: um *password spraying* de um IP só (uma senha comum contra muitas contas, a 0,5/s) fica sempre em `Information`. O `Warning` só aparece com **2 ou mais IPs** em ataque sustentado.

Para o spray de um IP só:

1. Olhe os `Information` seguidos: `total` perto de 30 por minuto, minuto após minuto, é o padrão.
2. A defesa contra a conta é o bloqueio por conta e IP (5 falhas), que o spray de uma senha só não aciona. O custo do atacante é o PBKDF2 de cada tentativa.
3. O semáforo de PBKDF2 devolve 503 no login quando está cheio; 503 repetido no login também é sinal.

Mudar o limiar do `Warning` (hoje 1/s, constante `LimiarDeAlertaPorSegundo`) é decisão de segurança; não suba o limite do login para o alerta sair.
