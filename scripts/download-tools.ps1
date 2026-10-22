<#
.SYNOPSIS
    Baixa o mpv (e, futuramente, o ffmpeg) com verificação de SHA-256 (seção 13.1).

.DESCRIPTION
    As URLs e os hashes ficam em scripts\tools.lock.json, que é versionado. Os binários nunca são commitados.
    Se um item ainda não tem hash, o script baixa, mostra o SHA-256 calculado e para. Confira o arquivo
    (origem, versão) e rode de novo com -AcceptNewHash para gravar o hash no lock; depois faça commit do lock.
    Arquivo cujo hash não confere é apagado e o script falha.

.PARAMETER Destination
    Pasta onde os executáveis serão colocados (padrão: publish\agent\tools).

.EXAMPLE
    .\download-tools.ps1 -Destination ..\publish\agent\tools
#>
[CmdletBinding()]
param(
    [string] $Destination = (Join-Path $PSScriptRoot '..\publish\agent\tools'),
    [switch] $AcceptNewHash
)

$ErrorActionPreference = 'Stop'
$lockPath = Join-Path $PSScriptRoot 'tools.lock.json'
$lock = Get-Content $lockPath -Raw | ConvertFrom-Json
$cache = Join-Path $env:TEMP 'marumbisom-tools'
New-Item -ItemType Directory -Force -Path $cache, $Destination | Out-Null
$lockChanged = $false

foreach ($tool in $lock.tools) {
    $archive = Join-Path $cache ([IO.Path]::GetFileName(([Uri]$tool.url).AbsolutePath))
    if (-not (Test-Path $archive)) {
        Write-Host "Baixando $($tool.name) de $($tool.url)"
        Invoke-WebRequest -Uri $tool.url -OutFile $archive -UseBasicParsing
    }

    $actual = (Get-FileHash -Algorithm SHA256 -Path $archive).Hash.ToLowerInvariant()
    if (-not $tool.sha256) {
        if (-not $AcceptNewHash) {
            Write-Warning "$($tool.name) ainda não tem hash no lock. SHA-256 calculado: $actual"
            Write-Warning "Confira a origem do arquivo e rode novamente com -AcceptNewHash para gravar."
            exit 2
        }
        $tool.sha256 = $actual
        $lockChanged = $true
    } elseif ($tool.sha256.ToLowerInvariant() -ne $actual) {
        Remove-Item $archive -Force
        throw "SHA-256 de $($tool.name) não confere. Esperado $($tool.sha256), obtido $actual. Arquivo apagado."
    }

    $extractDir = Join-Path $cache "$($tool.name)-extract"
    Remove-Item -Recurse -Force $extractDir -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force -Path $extractDir | Out-Null
    # tar.exe (libarchive) do Windows 10/11 abre .zip e, nas versões recentes, .7z; senão usa o 7-Zip instalado.
    # No Windows PowerShell 5.1, stderr de comando nativo com ErrorActionPreference=Stop vira erro fatal.
    $ErrorActionPreference = 'Continue'
    tar -xf $archive -C $extractDir 2>$null
    $tarExit = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    if ($tarExit -ne 0 -or -not (Get-ChildItem -Path $extractDir -Recurse -File | Select-Object -First 1)) {
        $sevenZip = @("$env:ProgramFiles\7-Zip\7z.exe", "${env:ProgramFiles(x86)}\7-Zip\7z.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
        if (-not $sevenZip) { throw "Não foi possível extrair $archive. Instale o 7-Zip (gratuito) e rode de novo." }
        & $sevenZip x $archive "-o$extractDir" -y | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "7-Zip falhou ao extrair $archive" }
    }

    foreach ($file in $tool.files) {
        $found = Get-ChildItem -Path $extractDir -Recurse -Filter $file | Select-Object -First 1
        if (-not $found) { throw "$file não encontrado dentro de $archive" }
        Copy-Item $found.FullName -Destination (Join-Path $Destination $file) -Force
        Write-Host "  $file -> $Destination"
    }
}

if ($lockChanged) {
    $lock | ConvertTo-Json -Depth 10 | Set-Content -Path $lockPath -Encoding UTF8
    Write-Host "Hash gravado em $lockPath. Faça commit deste arquivo." -ForegroundColor Yellow
}
