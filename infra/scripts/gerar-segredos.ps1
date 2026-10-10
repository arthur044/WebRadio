# =============================================================================
# WebRadio — infra/scripts/gerar-segredos.ps1
# Autor: Forge · tarefa E1-F18 · docs/specs/05-seguranca-auditoria.md §5
#
# Equivalente Windows/PowerShell de gerar-segredos.sh (o DJ usa Windows).
# Mesmo contrato: alfabeto base64url sem padding [A-Za-z0-9_-], as 4 senhas de
# SQL Server regeneradas até ter maiúscula+minúscula+dígito, nunca sobrescreve
# o que já existe. Ver gerar-segredos.sh para a explicação completa do porquê.
#
# Uso:
#   .\infra\scripts\gerar-segredos.ps1 -Modo dev    # cria .env na raiz do repo
#   .\infra\scripts\gerar-segredos.ps1 -Modo prod   # cria secrets\<nome>
# =============================================================================
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('dev', 'prod')]
    [string]$Modo
)

$ErrorActionPreference = 'Stop'
$DirRaiz = Resolve-Path (Join-Path $PSScriptRoot '..\..')

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

function New-SqlPolicyPassword {
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

# Nome × geração (docs/specs/05-seguranca-auditoria.md §5.1). MINIO_API_ACCESS_KEY/SECRET
# e MINIO_WORKER_ACCESS_KEY/SECRET ficam de fora: criados pelo minio-init em runtime.
$Receitas = [ordered]@{
    'Jwt__SigningKey'            = { New-Base64UrlSecret -Bytes 48 }
    'Seguranca__Pepper'          = { New-Base64UrlSecret -Bytes 32 }
    'Playout__Token'             = { New-HexSecret -Bytes 32 }
    'ICECAST_SOURCE_PASSWORD'    = { New-Base64UrlSecret -Bytes 24 }
    'ICECAST_ADMIN_USER'         = { "locutor-$(New-HexSecret -Bytes 3)" }
    'ICECAST_ADMIN_PASSWORD'     = { New-Base64UrlSecret -Bytes 24 }
    'ICECAST_RELAY_PASSWORD'     = { New-Base64UrlSecret -Bytes 24 }
    'LIQUIDSOAP_HARBOR_PASSWORD' = { New-Base64UrlSecret -Bytes 24 }
    'MSSQL_SA_PASSWORD'          = { New-SqlPolicyPassword -Bytes 32 }
    'DB_MIGRATOR_PASSWORD'       = { New-SqlPolicyPassword -Bytes 24 }
    'DB_APP_PASSWORD'            = { New-SqlPolicyPassword -Bytes 24 }
    'DB_RELATORIO_PASSWORD'      = { New-SqlPolicyPassword -Bytes 24 }
    'MINIO_ROOT_USER'            = { New-HexSecret -Bytes 16 }
    'MINIO_ROOT_PASSWORD'        = { New-Base64UrlSecret -Bytes 32 }
    'REDIS_PASSWORD'             = { New-Base64UrlSecret -Bytes 32 }
    'Seed__AdminSenha'           = { New-Base64UrlSecret -Bytes 18 }
}

function ConvertTo-SnakeCase {
    param([string]$Nome)
    $s = $Nome -creplace '([a-z0-9])([A-Z])', '$1_$2'
    $s = $s -replace '__', '_'
    return $s.ToLowerInvariant()
}

if ($Modo -eq 'dev') {
    $Destino = Join-Path $DirRaiz '.env'
    if (Test-Path $Destino) {
        Write-Warning "$Destino já existe — não sobrescrevendo. Apague-o manualmente para gerar de novo."
        exit 0
    }

    # Comentarios do .env gerado ficam em ASCII puro de proposito: o arquivo e escrito sem
    # BOM (compativel com docker-compose) e nao deve depender de deteccao de encoding.
    $linhas = New-Object System.Collections.Generic.List[string]
    $linhas.Add("# Gerado por infra/scripts/gerar-segredos.ps1 dev -- $((Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ'))")
    $linhas.Add('# NUNCA versionar este arquivo (.gitignore ja cobre .env).')
    $linhas.Add('')
    $linhas.Add('ASPNETCORE_ENVIRONMENT=Development')
    $linhas.Add('SEED_DEV=true')
    $linhas.Add('')

    foreach ($nome in $Receitas.Keys) {
        $valor = & $Receitas[$nome]
        $linhas.Add("$nome=$valor")
    }

    $linhas.Add('')
    $linhas.Add('Seed__AdminEmail=admin@webradio.local')

    # LF explícito (não CRLF do Windows) — mesmo formato do .sh, consumido em contêiner Linux.
    [System.IO.File]::WriteAllText($Destino, ($linhas -join "`n") + "`n")
    Write-Host "Gerado $Destino."
}
else {
    $DestinoDir = Join-Path $DirRaiz 'secrets'
    New-Item -ItemType Directory -Force -Path $DestinoDir | Out-Null

    foreach ($nome in $Receitas.Keys) {
        $arquivo = Join-Path $DestinoDir (ConvertTo-SnakeCase $nome)
        if (Test-Path $arquivo) {
            Write-Warning "$arquivo já existe — pulando."
            continue
        }
        $valor = & $Receitas[$nome]
        [System.IO.File]::WriteAllText($arquivo, $valor)
    }

    $emailPath = Join-Path $DestinoDir 'seed_admin_email'
    if (-not (Test-Path $emailPath)) {
        [System.IO.File]::WriteAllText($emailPath, 'admin@webradio.local')
    }

    Write-Host "Segredos gerados em $DestinoDir (os que já existiam foram preservados)."
}
