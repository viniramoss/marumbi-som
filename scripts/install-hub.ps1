#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Instala (ou atualiza) o serviço MarumbiSom.Hub no PC do escritório (seção 6).

.DESCRIPTION
    - Copia os binários publicados para C:\MarumbiSom\Hub\app (com tools\ffmpeg.exe e tools\ffprobe.exe).
    - Registra o serviço MarumbiSom.Hub com a conta virtual "NT SERVICE\MarumbiSom.Hub" (sem senha e sem
      privilégios de administrador), início Automático e recuperação 10 s / 10 s / 60 s.
    - Protege C:\MarumbiSom\Hub: só SYSTEM e Administradores têm controle; o serviço lê app\ e grava em data\
      e logs\. Usuários comuns do PC não leem o banco nem trocam os binários.
    - Na primeira instalação pede a senha do usuário "admin" do painel (mínimo 10 caracteres).
    - Ajusta o plano de energia para o PC não suspender nem hibernar na tomada (seção 6.1).
    - O Hub escuta só em 127.0.0.1:5080; o acesso das lojas e do celular é pelo Tailscale Serve.
    - Idempotente: pode ser executado de novo para atualizar.

.PARAMETER SourceDir
    Pasta com o resultado do "dotnet publish" do Hub (deve conter MarumbiSom.Hub.exe e tools\ffmpeg.exe).

.PARAMETER ResetAdminPassword
    Pede uma nova senha para o usuário do painel mesmo que ele já exista (ex.: senha esquecida).

.PARAMETER AdminUser
    Nome do usuário do painel (padrão: admin).

.PARAMETER TailscaleServe
    Também roda "tailscale serve --bg 5080" no final (o Tailscale precisa estar instalado e logado).

.PARAMETER SkipPowerSettings
    Não mexe no plano de energia.

.EXAMPLE
    .\install-hub.ps1 -SourceDir ..\publish\hub -TailscaleServe
#>
[CmdletBinding()]
param(
    [string] $SourceDir = (Join-Path $PSScriptRoot '..\publish\hub'),
    [switch] $ResetAdminPassword,
    [string] $AdminUser = 'admin',
    [switch] $TailscaleServe,
    [switch] $SkipPowerSettings
)

$ErrorActionPreference = 'Stop'
$ServiceName = 'MarumbiSom.Hub'
$ServiceAccount = "NT SERVICE\$ServiceName"
$Root = 'C:\MarumbiSom\Hub'
$AppDir = Join-Path $Root 'app'
$DataDir = Join-Path $Root 'data'
$LogsDir = Join-Path $Root 'logs'
$Exe = Join-Path $AppDir 'MarumbiSom.Hub.exe'
$Port = 5080

function Write-Step([string] $Message) { Write-Host "==> $Message" -ForegroundColor Cyan }

$SourceDir = (Resolve-Path $SourceDir).Path
if (-not (Test-Path (Join-Path $SourceDir 'MarumbiSom.Hub.exe'))) {
    throw "MarumbiSom.Hub.exe não encontrado em $SourceDir. Rode 'dotnet publish' antes ou use o pacote do CI."
}
foreach ($tool in 'ffmpeg.exe', 'ffprobe.exe') {
    if (-not (Test-Path (Join-Path $SourceDir "tools\$tool")) -and -not (Test-Path (Join-Path $AppDir "tools\$tool"))) {
        throw "tools\$tool não encontrado. Rode scripts\download-tools.ps1 -For hub -Destination $SourceDir\tools."
    }
}

$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing -and $existing.Status -ne 'Stopped') {
    Write-Step "Parando o serviço $ServiceName"
    $servicePid = (Get-CimInstance Win32_Service -Filter "Name='$ServiceName'").ProcessId
    Stop-Service -Name $ServiceName -Force
    (Get-Service $ServiceName).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60))
    # O Windows marca "Parado" antes de o processo terminar de sair; até lá os arquivos seguem em uso.
    if ($servicePid) { Wait-Process -Id $servicePid -Timeout 30 -ErrorAction SilentlyContinue }
}
# ffmpeg/ffprobe que tenham sobrado de uma ingestão também seguram os arquivos.
Get-Process -Name ffmpeg, ffprobe -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -like (Join-Path $AppDir '*') } |
    Stop-Process -Force -ErrorAction SilentlyContinue

Write-Step "Copiando binários para $AppDir"
foreach ($dir in @($Root, $AppDir, $DataDir, $LogsDir)) {
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
}
# /E copia subpastas; não usa /MIR para não apagar tools\ instalado separadamente.
robocopy $SourceDir $AppDir /E /NFL /NDL /NJH /NJS /NP /R:10 /W:3 | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy falhou com código $LASTEXITCODE" }
# O painel (wwwroot, D23 f) é espelhado: arquivos de versões anteriores do painel saem.
if (Test-Path (Join-Path $SourceDir 'wwwroot\index.html')) {
    robocopy (Join-Path $SourceDir 'wwwroot') (Join-Path $AppDir 'wwwroot') /MIR /NFL /NDL /NJH /NJS /NP /R:10 /W:3 | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "robocopy do painel falhou com código $LASTEXITCODE" }
} else {
    Write-Warning "Pacote sem o painel (wwwroot\index.html). Use o pacote do CI ou rode 'npm ci; npm run build' em web\panel e copie dist para wwwroot."
}

if (-not $existing) {
    Write-Step "Registrando o serviço $ServiceName (conta $ServiceAccount)"
    sc.exe create $ServiceName binPath= "`"$Exe`"" start= auto obj= $ServiceAccount DisplayName= "Marumbi Som - Hub" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "sc.exe create falhou com código $LASTEXITCODE" }
} else {
    sc.exe config $ServiceName binPath= "`"$Exe`"" start= auto obj= $ServiceAccount | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "sc.exe config falhou com código $LASTEXITCODE" }
}
sc.exe description $ServiceName "Central de músicas e ofertas da Casa de Carnes Marumbi. Acesso somente pelo Tailscale." | Out-Null
# Recuperação (seção 6.1): reiniciar após 10 s, 10 s e 60 s; zerar a contagem após 1 dia.
sc.exe failure $ServiceName reset= 86400 actions= restart/10000/restart/10000/restart/60000 | Out-Null
sc.exe failureflag $ServiceName 1 | Out-Null
# Só SYSTEM e Administradores param ou reconfiguram o serviço; os demais só consultam.
sc.exe sdset $ServiceName 'D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCSWLOCRRC;;;IU)(A;;CCLCSWLOCRRC;;;SU)' | Out-Null
if ($LASTEXITCODE -ne 0) { throw "sc.exe sdset falhou com código $LASTEXITCODE" }

Write-Step "Protegendo $Root"
# Pastas na raiz do C:\ herdam "Usuários autenticados: Modificar"; a herança é cortada para nenhum usuário comum
# trocar os binários (que rodam como serviço) nem ler o banco. SIDs em vez de nomes para o Windows em português:
# S-1-5-18 = SYSTEM, S-1-5-32-544 = Administradores. A conta virtual do serviço só existe depois do sc.exe create.
# Arquivos gravados pelo serviço têm a conta dele como dona; os Administradores assumem para acertar as permissões.
takeown /F $Root /A /R | Out-Null
if ($LASTEXITCODE -ne 0) { throw "takeown falhou com código $LASTEXITCODE" }
# Só as pastas recebem permissões; os arquivos herdam. Repetir /inheritance:r em cada arquivo (com /T) deixava os
# arquivos que já existiam numa atualização (ex.: o banco) sem permissão nenhuma, nem para o serviço.
icacls $Root /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' "${ServiceAccount}:(OI)(CI)RX" /C /Q | Out-Null
if ($LASTEXITCODE -ne 0) { throw "icacls falhou com código $LASTEXITCODE" }
icacls (Join-Path $Root '*') /reset /T /C /Q | Out-Null
if ($LASTEXITCODE -ne 0) { throw "icacls /reset falhou com código $LASTEXITCODE" }
foreach ($dir in @($DataDir, $LogsDir)) {
    # Sem /T: o Windows repassa a permissão herdável para o que já está dentro da pasta.
    icacls $dir /grant "${ServiceAccount}:(OI)(CI)M" /C /Q | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "icacls falhou em $dir com código $LASTEXITCODE" }
}

$database = Join-Path $DataDir 'marumbi.db'
if (-not (Test-Path $database) -or $ResetAdminPassword) {
    Write-Step "Senha do usuário '$AdminUser' do painel (mínimo 10 caracteres; guarde no gerenciador de senhas)"
    while ($true) {
        $first = Read-Host -AsSecureString 'Nova senha'
        $second = Read-Host -AsSecureString 'Repita a senha'
        $plain1 = [Runtime.InteropServices.Marshal]::PtrToStringBSTR([Runtime.InteropServices.Marshal]::SecureStringToBSTR($first))
        $plain2 = [Runtime.InteropServices.Marshal]::PtrToStringBSTR([Runtime.InteropServices.Marshal]::SecureStringToBSTR($second))
        if ($plain1 -ne $plain2) {
            Write-Warning 'As senhas não são iguais. Tente de novo.'
            continue
        }
        if ($plain1.Length -lt 10) {
            Write-Warning 'A senha precisa ter pelo menos 10 caracteres.'
            continue
        }
        # O Hub cria o banco (se ainda não existe) e grava só o hash; a senha vai pelo stdin, fora da linha de comando.
        $previousEncoding = $OutputEncoding
        $OutputEncoding = New-Object System.Text.UTF8Encoding $false
        try {
            $plain1 | & $Exe --create-admin $AdminUser
        } finally {
            $OutputEncoding = $previousEncoding
            $plain1 = $null
            $plain2 = $null
        }
        if ($LASTEXITCODE -ne 0) { throw "Não foi possível gravar a senha (código $LASTEXITCODE)." }
        break
    }
}

if (-not $SkipPowerSettings) {
    Write-Step 'Plano de energia: nunca suspender nem hibernar na tomada'
    powercfg /change standby-timeout-ac 0 | Out-Null
    powercfg /change hibernate-timeout-ac 0 | Out-Null
}

Write-Step "Iniciando o serviço"
Start-Service -Name $ServiceName
(Get-Service $ServiceName).WaitForStatus('Running', [TimeSpan]::FromSeconds(30))
$healthy = $false
for ($i = 0; $i -lt 30 -and -not $healthy; $i++) {
    try {
        $health = Invoke-RestMethod -Uri "http://127.0.0.1:$Port/healthz" -TimeoutSec 2 -UseBasicParsing
        $healthy = [bool]$health.ok
    } catch {
        Start-Sleep -Seconds 1
    }
}
if (-not $healthy) {
    throw "O serviço iniciou, mas http://127.0.0.1:$Port/healthz não respondeu. Veja os logs em $LogsDir."
}

if ($TailscaleServe) {
    $tailscale = Get-Command tailscale -ErrorAction SilentlyContinue
    if (-not $tailscale) {
        Write-Warning 'tailscale não encontrado no PATH. Instale o Tailscale, faça login e rode: tailscale serve --bg 5080'
    } else {
        Write-Step 'Publicando o Hub na rede Tailscale (tailscale serve --bg 5080)'
        & $tailscale.Source serve --bg $Port
        if ($LASTEXITCODE -ne 0) { Write-Warning "tailscale serve falhou (código $LASTEXITCODE). Rode o comando de novo depois de conferir o login." }
    }
}

Write-Host ""
Write-Host "Hub instalado e respondendo em http://127.0.0.1:$Port (só neste PC)." -ForegroundColor Green
Write-Host "Dados em $DataDir, logs em $LogsDir. Copie a pasta data\ para fora do PC (ex.: Google Drive para desktop)."
if (-not $TailscaleServe) {
    Write-Host "Para as lojas e o celular alcançarem o Hub: tailscale serve --bg $Port"
}
