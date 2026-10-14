# =============================================================================
# WebRadio — infra/scripts/gerar-segredos.ps1
# Autor: Forge · tarefa E1-F18 · docs/specs/05-seguranca-auditoria.md §5
#
# Equivalente Windows/PowerShell de gerar-segredos.sh (o DJ usa Windows).
# Mesmo contrato: um arquivo por segredo em secrets\ (dev e prod), alfabeto
# base64url sem padding [A-Za-z0-9_-], ≥ 24 caracteres, base64url regenerado até
# ter maiúscula+minúscula+dígito, nunca sobrescreve o que já existe. No modo dev
# também cria o .env (configuração NÃO secreta, copiada do .env.example). Ver
# gerar-segredos.sh para a explicação completa do porquê.
#
# Uso:
#   .\infra\scripts\gerar-segredos.ps1 -Modo dev                                   # secrets\ + .env de desenvolvimento
#   .\infra\scripts\gerar-segredos.ps1 -Modo prod -AdminEmail admin@radio.exemplo.br  # só secrets\
#
# Em prod o e-mail do Admin é obrigatório (um e-mail fixo e conhecido é metade da credencial, B3-10); em dev vale
# admin@webradio.local. A pasta secrets\ fica com ACL restrita ao usuário atual e ao SYSTEM (equivale ao chmod 700 do .sh).
# =============================================================================
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('dev', 'prod')]
    [string]$Modo,

    [string]$AdminEmail
)

$ErrorActionPreference = 'Stop'
$DirRaiz = Resolve-Path (Join-Path $PSScriptRoot '..\..')

if ($AdminEmail -and $AdminEmail -cnotmatch '^[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}$') {
    throw 'E-mail do Admin inválido.'
}
if ($Modo -eq 'prod' -and -not $AdminEmail -and -not (Test-Path (Join-Path $DirRaiz 'secrets\seed_admin_email'))) {
    throw 'Em prod informe o e-mail do Admin: -AdminEmail admin@seu-dominio.'
}

# RandomNumberGenerator.Fill (estatico) so existe em .NET Core/5+; Windows PowerShell 5.1
# roda em .NET Framework, onde so RNGCryptoServiceProvider.GetBytes (instancia) existe.
$Script:Rng = [System.Security.Cryptography.RNGCryptoServiceProvider]::new()

function New-Base64UrlSecret {
    param([int]$Bytes)
    $buffer = [byte[]]::new($Bytes)
    $Script:Rng.GetBytes($buffer)
    $base64 = [Convert]::ToBase64String($buffer)
    return $base64.Replace('+', '-').Replace('/', '_').Replace('=', '')
}

function New-StrongSecret {
    # Regenera até ter maiúscula, minúscula e dígito (política de senha do SQL Server).
    param([int]$Bytes)
    while ($true) {
        $valor = New-Base64UrlSecret -Bytes $Bytes
        if ($valor -cmatch '[A-Z]' -and $valor -cmatch '[a-z]' -and $valor -cmatch '[0-9]') {
            return $valor
        }
    }
}

function New-HexSecret {
    param([int]$Bytes)
    $buffer = [byte[]]::new($Bytes)
    $Script:Rng.GetBytes($buffer)
    return ($buffer | ForEach-Object { $_.ToString('x2') }) -join ''
}

# arquivo em secrets\ × geração (docs/specs/05-seguranca-auditoria.md §5.1). Mesma lista do .sh.
$Receitas = [ordered]@{
    'jwt_signing_key'            = { New-StrongSecret -Bytes 48 }
    'seguranca_pepper'           = { New-StrongSecret -Bytes 32 }
    'playout_token'              = { New-HexSecret -Bytes 32 }
    'icecast_source_password'    = { New-StrongSecret -Bytes 24 }
    'icecast_admin_user'         = { "locutor-$(New-HexSecret -Bytes 3)" }
    'icecast_admin_password'     = { New-StrongSecret -Bytes 24 }
    'icecast_relay_password'     = { New-StrongSecret -Bytes 24 }
    'liquidsoap_harbor_password' = { New-StrongSecret -Bytes 24 }
    'mssql_sa_password'          = { New-StrongSecret -Bytes 32 }
    'db_migrator_password'       = { New-StrongSecret -Bytes 24 }
    'db_app_password'            = { New-StrongSecret -Bytes 24 }
    'db_relatorio_password'      = { New-StrongSecret -Bytes 24 }
    'minio_root_user'            = { New-HexSecret -Bytes 16 }
    'minio_root_password'        = { New-StrongSecret -Bytes 32 }
    'minio_api_access_key'       = { New-HexSecret -Bytes 20 }
    'minio_api_secret_key'       = { New-StrongSecret -Bytes 30 }
    'minio_worker_access_key'    = { New-HexSecret -Bytes 20 }
    'minio_worker_secret_key'    = { New-StrongSecret -Bytes 30 }
    'redis_password'             = { New-StrongSecret -Bytes 32 }
    'seed_admin_senha'           = { New-StrongSecret -Bytes 18 }
}

$DestinoDir = Join-Path $DirRaiz 'secrets'
New-Item -ItemType Directory -Force -Path $DestinoDir | Out-Null

# ACL: só o usuário atual e o SYSTEM entram em secrets\ (o .sh faz chmod 700). Remove a herança da pasta do repositório.
# Só no Windows: em outros sistemas (o script também roda no PowerShell 7 do Linux/macOS) o icacls não existe.
if ([Environment]::OSVersion.Platform -eq 'Win32NT') {
    $usuario = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    & icacls.exe $DestinoDir /inheritance:r /grant:r "*${usuario}:(OI)(CI)F" '*S-1-5-18:(OI)(CI)F' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "icacls falhou ao restringir a ACL de $DestinoDir." }
}

function Write-Secret {
    # Sem BOM e sem newline no fim: o .NET e o sqlcmd leriam qualquer byte a mais como parte da senha.
    param([string]$Arquivo, [string]$Valor)
    $tmp = "$Arquivo.tmp"
    [System.IO.File]::WriteAllText($tmp, $Valor, [System.Text.UTF8Encoding]::new($false))
    Move-Item -Force $tmp $Arquivo
}

$criados = 0
foreach ($nome in $Receitas.Keys) {
    $arquivo = Join-Path $DestinoDir $nome
    if (Test-Path $arquivo) {
        Write-Warning "secrets\$nome já existe — preservado."
        continue
    }
    Write-Secret -Arquivo $arquivo -Valor (& $Receitas[$nome])
    $criados++
}

$emailPath = Join-Path $DestinoDir 'seed_admin_email'
if (-not (Test-Path $emailPath)) {
    $email = if ($AdminEmail) { $AdminEmail } else { 'admin@webradio.local' }
    Write-Secret -Arquivo $emailPath -Valor $email
}

Write-Host "secrets\: $criados segredo(s) novo(s); os que já existiam foram preservados."

if ($Modo -eq 'dev') {
    $Destino = Join-Path $DirRaiz '.env'
    if (Test-Path $Destino) {
        Write-Warning ".env já existe — não sobrescrevendo."
    }
    else {
        # Copia byte a byte (LF do repositório preservado; o .env é lido pelo Compose no Linux também).
        Copy-Item (Join-Path $DirRaiz '.env.example') $Destino
        Write-Host "Gerado .env (configuração de desenvolvimento, copiada do .env.example)."
    }
}
