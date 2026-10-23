#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Instala (ou atualiza) o serviço MarumbiSom.Agent no PC da loja (seção 8.1).

.DESCRIPTION
    - Copia os binários publicados para C:\Program Files\MarumbiSom.
    - Cria C:\ProgramData\MarumbiSom com acesso somente para SYSTEM e Administradores.
    - Registra o serviço como LocalSystem, início Automático (Atrasado), recuperação 10 s / 10 s / 60 s,
      zerando a contagem após 1 dia, e com permissões que impedem usuários padrão de pará-lo.
    - Na primeira instalação pede a senha da configuração local (seção 8.4) e cria o atalho
      "Configurar Som" na área de trabalho do administrador que está instalando.
    - Idempotente: pode ser executado de novo para atualizar.

.PARAMETER SourceDir
    Pasta com o resultado do "dotnet publish" (deve conter MarumbiSom.Agent.exe e tools\mpv.exe).

.PARAMETER SpeakerAddress
    Opcional. Endereço Bluetooth (MAC) da caixa, ex.: AA:BB:CC:DD:EE:FF. Também dá para escolher a caixa
    depois, na página "Configurar Som".

.PARAMETER ResetLocalPassword
    Pede uma nova senha da configuração local mesmo que já exista uma (ex.: senha esquecida).

.EXAMPLE
    .\install-agent.ps1 -SourceDir ..\publish\agent -SpeakerAddress AA:BB:CC:DD:EE:FF
#>
[CmdletBinding()]
param(
    [string] $SourceDir = (Join-Path $PSScriptRoot '..\publish\agent'),
    [string] $SpeakerAddress,
    [switch] $ResetLocalPassword
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
foreach ($dir in @($DataDir, (Join-Path $DataDir 'logs'), (Join-Path $DataDir 'manifests'), (Join-Path $DataDir 'cache'))) {
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

$hasPassword = $config.PSObject.Properties['LocalConfig'] -and $config.LocalConfig.PSObject.Properties['PasswordHash'] -and $config.LocalConfig.PasswordHash
if (-not $hasPassword -or $ResetLocalPassword) {
    Write-Step "Senha da página Configurar Som (mínimo 8 caracteres; guarde no gerenciador de senhas)"
    while ($true) {
        $first = Read-Host -AsSecureString 'Nova senha'
        $second = Read-Host -AsSecureString 'Repita a senha'
        $plain1 = [Runtime.InteropServices.Marshal]::PtrToStringBSTR([Runtime.InteropServices.Marshal]::SecureStringToBSTR($first))
        $plain2 = [Runtime.InteropServices.Marshal]::PtrToStringBSTR([Runtime.InteropServices.Marshal]::SecureStringToBSTR($second))
        if ($plain1 -ne $plain2) {
            Write-Warning 'As senhas não são iguais. Tente de novo.'
            continue
        }
        if ($plain1.Length -lt 8) {
            Write-Warning 'A senha precisa ter pelo menos 8 caracteres.'
            continue
        }
        # O agente grava só o hash PBKDF2 no config.json; a senha passa pelo stdin para não aparecer na linha de comando.
        $previousEncoding = $OutputEncoding
        $OutputEncoding = New-Object System.Text.UTF8Encoding $false
        try {
            $plain1 | & $Exe --set-local-password
        } finally {
            $OutputEncoding = $previousEncoding
            $plain1 = $null
            $plain2 = $null
        }
        if ($LASTEXITCODE -ne 0) { throw "Não foi possível gravar a senha (código $LASTEXITCODE)." }
        break
    }
}

$shortcut = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Configurar Som.url'
if (-not (Test-Path $shortcut)) {
    Write-Step "Criando o atalho $shortcut"
    # Só na área de trabalho de quem instala (administrador), nunca na pública: o funcionário não deve ver (seção 8.4).
    Set-Content -Path $shortcut -Encoding ASCII -Value "[InternetShortcut]`r`nURL=http://127.0.0.1:47800/`r`n"
}

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
Write-Host "Configuração local: atalho 'Configurar Som' na área de trabalho (http://127.0.0.1:47800/)."
if (-not $config.Bluetooth.SpeakerAddress) {
    Write-Warning "Caixa ainda não escolhida. Abra 'Configurar Som' e escolha a caixa pareada."
}
