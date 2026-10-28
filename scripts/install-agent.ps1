#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Instala (ou atualiza) o serviço MarumbiSom.Agent no PC da loja (seção 8.1).

.DESCRIPTION
    - Copia os binários publicados para C:\Program Files\MarumbiSom.
    - Cria C:\ProgramData\MarumbiSom com acesso somente para SYSTEM e Administradores.
    - Registra o serviço como LocalSystem, início Automático (Atrasado), recuperação 10 s / 10 s / 60 s,
      zerando a contagem após 1 dia, e com permissões que impedem usuários padrão de pará-lo.
    - Idempotente: pode ser executado de novo para atualizar.

.PARAMETER SourceDir
    Pasta com o resultado do "dotnet publish" (deve conter MarumbiSom.Agent.exe e tools\mpv.exe).

.PARAMETER SpeakerAddress
    Endereço Bluetooth (MAC) da caixa, ex.: AA:BB:CC:DD:EE:FF. Veja com "MarumbiSom.Agent.exe --list-devices".

.EXAMPLE
    .\install-agent.ps1 -SourceDir ..\publish\agent -SpeakerAddress AA:BB:CC:DD:EE:FF
#>
[CmdletBinding()]
param(
    [string] $SourceDir = (Join-Path $PSScriptRoot '..\publish\agent'),
    [string] $SpeakerAddress
)

$ErrorActionPreference = 'Stop'
$ServiceName = 'MarumbiSom.Agent'
$InstallDir = Join-Path $env:ProgramFiles 'MarumbiSom'
$DataDir = Join-Path $env:ProgramData 'MarumbiSom'
$Exe = Join-Path $InstallDir 'MarumbiSom.Agent.exe'

function Write-Step([string] $Message) { Write-Host "==> $Message" -ForegroundColor Cyan }

$SourceDir = (Resolve-Path $SourceDir).Path
if (-not (Test-Path (Join-Path $SourceDir 'MarumbiSom.Agent.exe'))) {
    throw "MarumbiSom.Agent.exe não encontrado em $SourceDir. Rode 'dotnet publish' antes."
}
if (-not (Test-Path (Join-Path $SourceDir 'tools\mpv.exe')) -and -not (Test-Path (Join-Path $InstallDir 'tools\mpv.exe'))) {
    Write-Warning "tools\mpv.exe não encontrado. Rode scripts\download-tools.ps1 ou copie o mpv.exe para $InstallDir\tools."
}
if ($SpeakerAddress -and $SpeakerAddress -notmatch '^([0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2}$') {
    throw "SpeakerAddress inválido: '$SpeakerAddress'. Formato: AA:BB:CC:DD:EE:FF."
}

$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing -and $existing.Status -ne 'Stopped') {
    Write-Step "Parando o serviço $ServiceName"
    Stop-Service -Name $ServiceName -Force
    (Get-Service $ServiceName).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
}

Write-Step "Copiando binários para $InstallDir"
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
# /E copia subpastas; não usa /MIR para não apagar tools\ instalado separadamente.
robocopy $SourceDir $InstallDir /E /NFL /NDL /NJH /NJS /NP /R:2 /W:2 | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy falhou com código $LASTEXITCODE" }

Write-Step "Preparando $DataDir (somente SYSTEM e Administradores)"
foreach ($dir in @($DataDir, (Join-Path $DataDir 'logs'), (Join-Path $DataDir 'poc\musicas'))) {
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
}
# SIDs em vez de nomes, para funcionar no Windows em português: S-1-5-18 = SYSTEM, S-1-5-32-544 = Administradores.
icacls $DataDir /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' /T /C /Q | Out-Null
if ($LASTEXITCODE -ne 0) { throw "icacls falhou com código $LASTEXITCODE" }

$configPath = Join-Path $DataDir 'config.json'
if (Test-Path $configPath) {
    $config = Get-Content $configPath -Raw | ConvertFrom-Json
} else {
    $config = [pscustomobject]@{}
}
if (-not $config.PSObject.Properties['Bluetooth']) {
    $config | Add-Member -NotePropertyName Bluetooth -NotePropertyValue ([pscustomobject]@{ SpeakerAddress = '' })
}
if ($SpeakerAddress) {
    $config.Bluetooth | Add-Member -NotePropertyName SpeakerAddress -NotePropertyValue $SpeakerAddress.ToUpperInvariant() -Force
}
$tmp = "$configPath.tmp"
$config | ConvertTo-Json -Depth 10 | Set-Content -Path $tmp -Encoding UTF8
Move-Item -Path $tmp -Destination $configPath -Force
Write-Step "Configuração em $configPath"

if (-not $existing) {
    Write-Step "Registrando o serviço $ServiceName"
    sc.exe create $ServiceName binPath= "`"$Exe`"" start= delayed-auto obj= LocalSystem DisplayName= "Marumbi Som - Agente da loja" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "sc.exe create falhou com código $LASTEXITCODE" }
} else {
    sc.exe config $ServiceName binPath= "`"$Exe`"" start= delayed-auto obj= LocalSystem | Out-Null
}
sc.exe description $ServiceName "Toca a música ambiente da Casa de Carnes Marumbi e mantém a caixa Bluetooth conectada." | Out-Null
# Recuperação (seção 8.1): reiniciar após 10 s, 10 s e 60 s; zerar a contagem após 1 dia.
sc.exe failure $ServiceName reset= 86400 actions= restart/10000/restart/10000/restart/60000 | Out-Null
sc.exe failureflag $ServiceName 1 | Out-Null
# Permissões do serviço: SYSTEM e Administradores com controle total; usuários interativos e de serviço só consultam.
# Regra 11: o funcionário (conta padrão) não consegue parar, pausar ou reconfigurar o serviço.
sc.exe sdset $ServiceName 'D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCSWLOCRRC;;;IU)(A;;CCLCSWLOCRRC;;;SU)' | Out-Null
if ($LASTEXITCODE -ne 0) { throw "sc.exe sdset falhou com código $LASTEXITCODE" }

Write-Step "Iniciando o serviço"
Start-Service -Name $ServiceName
(Get-Service $ServiceName).WaitForStatus('Running', [TimeSpan]::FromSeconds(30))
Write-Host ""
Write-Host "Instalado. Logs em $DataDir\logs" -ForegroundColor Green
if (-not $config.Bluetooth.SpeakerAddress) {
    Write-Warning "Caixa ainda não configurada. Rode '$Exe --list-devices' e depois este script com -SpeakerAddress."
}
