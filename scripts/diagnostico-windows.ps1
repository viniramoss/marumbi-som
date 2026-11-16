<#
.SYNOPSIS
    Diagnóstico e reparo do Windows nos PCs das lojas (D41). Sem parâmetros, só LÊ e gera um relatório; não muda nada.

.DESCRIPTION
    Junta num relatório o que mudou no PC e pode explicar problemas como "Classe não registrada" ao abrir a Pesquisa
    ou o menu Iniciar, e a tela girando sozinha:
      - versão do Windows e atualizações instaladas (com datas), para comparar com a data da instalação do SOM;
      - driver de vídeo (data e versão) e se há sensor de rotação;
      - estado dos pacotes do Iniciar/Pesquisa e falhas recentes de explorer/SearchHost/StartMenuExperienceHost;
      - quantas vezes o agente do SOM reiniciou o Bluetooth nos últimos dias;
      - os únicos ajustes que o SOM faz no Windows (energia, horário ativo do Windows Update, serviço).

    -Reparar (como administrador) faz só reparos padrão da Microsoft, todos reversíveis ou inofensivos:
      DISM /RestoreHealth + sfc /scannow, recadastro dos pacotes do Iniciar/Pesquisa na conta atual, trava da
      rotação automática, desliga os atalhos Ctrl+Alt+Seta do driver Intel e volta a tela para paisagem.

    -DesfazerAjustesDeEnergia devolve o que o harden-store-pc.ps1 mudou na energia e no Windows Update ao padrão do
      Windows. Não é necessário para os erros acima; o agente continua funcionando, mas o PC pode voltar a suspender.

.EXAMPLE
    irm https://raw.githubusercontent.com/viniramoss/marumbi-som/<branch>/scripts/diagnostico-windows.ps1 | iex
#>
[CmdletBinding()]
param(
    [switch] $Reparar,
    [switch] $DesfazerAjustesDeEnergia
)

# Quando vem por "irm | iex" os parâmetros não chegam; variáveis de ambiente permitem escolher o modo.
if ($env:SOM_REPARAR -eq '1') { $Reparar = $true }
if ($env:SOM_DESFAZER_ENERGIA -eq '1') { $DesfazerAjustesDeEnergia = $true }

$ErrorActionPreference = 'Continue'
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
$lines = [System.Collections.Generic.List[string]]::new()
function Out-Line([string] $Text = '', [string] $Color = 'Gray') { $lines.Add($Text); Write-Host $Text -ForegroundColor $Color }
function Out-Title([string] $Text) { Out-Line ''; Out-Line "== $Text ==" 'Cyan' }
function Try-Run([string] $What, [scriptblock] $Block) {
    try { & $Block } catch { Out-Line "  (não foi possível ler $What`: $($_.Exception.Message))" 'Yellow' }
}

Out-Line ("Diagnóstico do Windows para o SOM - {0:dd/MM/yyyy HH:mm} - PC {1} - usuário {2}{3}" -f (Get-Date), $env:COMPUTERNAME, $env:USERNAME, $(if ($isAdmin) { ' (admin)' } else { '' })) 'White'

# ---------------------------------------------------------------- Windows e SOM
Out-Title 'Windows'
Try-Run 'a versão do Windows' {
    $cv = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
    $os = Get-CimInstance Win32_OperatingSystem
    Out-Line ("  {0} {1} build {2}.{3}" -f $cv.ProductName, $cv.DisplayVersion, $cv.CurrentBuild, $cv.UBR)
    Out-Line ("  Windows instalado/atualizado de versão em {0:dd/MM/yyyy}; ligado desde {1:dd/MM/yyyy HH:mm}" -f $os.InstallDate, $os.LastBootUpTime)
    $c = Get-PSDrive C
    Out-Line ("  Disco C: {0:N1} GB livres de {1:N1} GB" -f ($c.Free / 1GB), (($c.Used + $c.Free) / 1GB))
    $upgrades = Get-ChildItem 'HKLM:\SYSTEM\Setup' -ErrorAction SilentlyContinue | Where-Object PSChildName -like 'Source OS*'
    foreach ($u in $upgrades) { Out-Line "  Atualização de versão anterior: $($u.PSChildName)" }
}

Out-Title 'SOM neste PC'
$state = @{ SomInstalled = $null }
Try-Run 'o SOM' {
    $dir = Join-Path $env:ProgramFiles 'MarumbiSom'
    if (Test-Path $dir) {
        $state.SomInstalled = (Get-Item $dir).CreationTime
        $exe = Join-Path $dir 'MarumbiSom.Agent.exe'
        $ver = if (Test-Path $exe) { (Get-Item $exe).VersionInfo.ProductVersion } else { '?' }
        Out-Line ("  Agente instalado em {0:dd/MM/yyyy HH:mm}, versão {1}" -f $state.SomInstalled, $ver)
    } else {
        Out-Line '  Agente do SOM não está instalado neste PC.'
    }
    foreach ($name in 'MarumbiSom.Agent', 'MarumbiSom.Hub') {
        $svc = Get-Service $name -ErrorAction SilentlyContinue
        if ($svc) { Out-Line "  Serviço $name`: $($svc.Status), início $($svc.StartType)" }
    }
    Out-Line '  O SOM só mexe no Windows em: energia (sem suspender/hibernar, USB sem economia), horário ativo do'
    Out-Line '  Windows Update, fuso/hora e o próprio serviço. Não registra DLL/COM, não grava em System32, não mexe'
    Out-Line '  no Iniciar, na Pesquisa, em políticas do Explorer nem em vídeo/rotação. O vulkan-1.dll fica só na pasta'
    Out-Line '  do programa (tools\).'
    $sysVulkan = Join-Path $env:WINDIR 'System32\vulkan-1.dll'
    if (Test-Path $sysVulkan) {
        $vi = (Get-Item $sysVulkan).VersionInfo
        Out-Line ("  System32\vulkan-1.dll existe ({0}, {1}) - vem do driver de vídeo ou do Vulkan Runtime; não é do SOM e não causa 'Classe não registrada'." -f $vi.ProductVersion, $vi.CompanyName)
    }
}

# ---------------------------------------------------------------- Atualizações
Out-Title 'Atualizações do Windows (últimos 120 dias)'
Try-Run 'o histórico do Windows Update' {
    $session = New-Object -ComObject Microsoft.Update.Session
    $searcher = $session.CreateUpdateSearcher()
    $count = $searcher.GetTotalHistoryCount()
    $since = (Get-Date).AddDays(-120)
    $history = if ($count -gt 0) { $searcher.QueryHistory(0, [Math]::Min($count, 300)) } else { @() }
    $rows = $history | Where-Object { $_.Date -ge $since -and $_.Title } | Sort-Object Date
    foreach ($h in $rows) {
        $result = switch ($h.ResultCode) { 2 { 'ok' } 3 { 'ok c/ erros' } 4 { 'FALHOU' } 5 { 'cancelada' } default { "código $($h.ResultCode)" } }
        $mark = if ($state.SomInstalled -and $h.Date -ge $state.SomInstalled) { '*' } else { ' ' }
        Out-Line ("  {0}{1:dd/MM/yyyy HH:mm}  {2,-11} {3}" -f $mark, $h.Date.ToLocalTime(), $result, $h.Title)
    }
    if (-not $rows) { Out-Line '  Nenhuma atualização registrada no período.' }
    elseif ($state.SomInstalled) { Out-Line '  (* = depois da instalação do SOM. O PC agora fica ligado à noite, então o Windows instala mais coisas.)' }
}

# ---------------------------------------------------------------- Vídeo e rotação
Out-Title 'Vídeo e rotação da tela'
Try-Run 'o driver de vídeo' {
    foreach ($d in Get-CimInstance Win32_PnPSignedDriver | Where-Object DeviceClass -eq 'DISPLAY') {
        Out-Line ("  {0} - driver {1} de {2:dd/MM/yyyy} ({3})" -f $d.DeviceName, $d.DriverVersion, $d.DriverDate, $d.DriverProviderName)
    }
    $sensors = Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue | Where-Object { $_.Class -eq 'Sensor' -or $_.FriendlyName -match 'Acceler|Aceler|Orienta|Rotation|Gyro' }
    if ($sensors) { foreach ($s in $sensors) { Out-Line "  Sensor: $($s.FriendlyName) ($($s.Status))" } }
    else { Out-Line '  Nenhum sensor de orientação: a tela não gira sozinha por sensor; giro aqui é atalho Ctrl+Alt+Seta ou acesso remoto.' }
    $rot = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AutoRotation' -ErrorAction SilentlyContinue
    Out-Line ("  Rotação automática: {0}" -f $(if ($rot -and $rot.Enable -eq 0) { 'travada' } else { 'livre (padrão)' }))
    $hk = Get-ItemProperty 'HKCU:\Software\Intel\Display\igfxcui\HotKeys' -ErrorAction SilentlyContinue
    if ($hk) { Out-Line ("  Atalhos Ctrl+Alt+Seta do driver Intel: {0}" -f $(if ($hk.Enable -eq 0) { 'desligados' } else { 'LIGADOS' })) }
    $remote = Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -match '^(parsecd|AnyDesk|TeamViewer|rustdesk)$' } | Select-Object -ExpandProperty ProcessName -Unique
    if ($remote) { Out-Line "  Acesso remoto rodando: $($remote -join ', ') (atalhos de teclado enviados por ele também giram a tela)" }
}
Try-Run 'eventos de driver de vídeo' {
    $evts = Get-WinEvent -FilterHashtable @{ LogName = 'System'; ProviderName = 'Microsoft-Windows-UserPnp'; Id = 20001, 20003; StartTime = (Get-Date).AddDays(-120) } -ErrorAction SilentlyContinue |
        Where-Object { $_.Message -match 'Display|Vídeo|Video|Graphics|Gráfico' } | Select-Object -First 10
    foreach ($e in $evts) { Out-Line ("  {0:dd/MM/yyyy HH:mm} driver de vídeo instalado/atualizado" -f $e.TimeCreated) }
}

# ---------------------------------------------------------------- Iniciar / Pesquisa
Out-Title 'Menu Iniciar e Pesquisa'
Try-Run 'os pacotes do Iniciar/Pesquisa' {
    $names = 'Microsoft.Windows.ShellExperienceHost', 'Microsoft.Windows.StartMenuExperienceHost', 'MicrosoftWindows.Client.CBS',
        'Microsoft.Windows.Search', 'Microsoft.Windows.Cortana', 'Microsoft.UI.Xaml.CBS', 'MicrosoftWindows.Client.Core'
    foreach ($n in $names) {
        $p = Get-AppxPackage -Name $n -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($p) { Out-Line ("  {0,-45} {1} {2}" -f $n, $p.Version, $p.Status) }
    }
}
Try-Run 'falhas de aplicativos' {
    $crashes = Get-WinEvent -FilterHashtable @{ LogName = 'Application'; Id = 1000; StartTime = (Get-Date).AddDays(-30) } -ErrorAction SilentlyContinue |
        ForEach-Object { if ($_.Properties.Count -gt 0) { [string]$_.Properties[0].Value } }
    $groups = $crashes | Group-Object | Sort-Object Count -Descending | Select-Object -First 8
    if ($groups) { foreach ($g in $groups) { Out-Line ("  {0,4}x {1} travou (30 dias)" -f $g.Count, $g.Name) } }
    else { Out-Line '  Nenhum aplicativo travou nos últimos 30 dias.' }
    $classErr = Get-WinEvent -FilterHashtable @{ LogName = 'Microsoft-Windows-TWinUI/Operational'; StartTime = (Get-Date).AddDays(-30) } -MaxEvents 300 -ErrorAction SilentlyContinue |
        Where-Object { $_.Message -match '0x80040154|Classe não registrada|Class not registered' }
    if ($classErr) { Out-Line ("  'Classe não registrada' (0x80040154) registrado {0}x nos últimos 30 dias; último em {1:dd/MM/yyyy HH:mm}" -f $classErr.Count, $classErr[0].TimeCreated) 'Yellow' }
}

# ---------------------------------------------------------------- Bluetooth do agente
Out-Title 'Quanto o agente mexeu no Bluetooth (últimos 7 dias)'
Try-Run 'os logs do agente' {
    $logDir = Join-Path $env:ProgramData 'MarumbiSom\logs'
    $files = Get-ChildItem $logDir -Filter 'agent-*' -ErrorAction Stop | Sort-Object LastWriteTime -Descending | Select-Object -First 7
    foreach ($f in $files | Sort-Object Name) {
        $text = Get-Content $f.FullName -Raw -Encoding UTF8 -ErrorAction SilentlyContinue
        if (-not $text) { continue }
        $adapter = ([regex]::Matches($text, 'Reiniciando o adaptador Bluetooth')).Count
        $a2dp = ([regex]::Matches($text, 'método B')).Count
        $svc = ([regex]::Matches($text, 'Reiniciando o serviço')).Count
        Out-Line ("  {0}: adaptador reiniciado {1}x, A2DP religado {2}x, serviço reiniciado {3}x" -f $f.Name, $adapter, $a2dp, $svc)
    }
}

# ---------------------------------------------------------------- Reparos
if ($Reparar) {
    Out-Title 'Reparo'
    if ($isAdmin) {
        Out-Line '  DISM /RestoreHealth (pode levar 10 a 30 min)...' 'White'
        DISM.exe /Online /Cleanup-Image /RestoreHealth | Out-Null
        Out-Line "  DISM terminou com código $LASTEXITCODE"
        Out-Line '  sfc /scannow...' 'White'
        sfc.exe /scannow | Out-Null
        Out-Line "  sfc terminou com código $LASTEXITCODE (0 = nada a corrigir ou corrigido)"
        New-Item 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AutoRotation' -Force | Out-Null
        Set-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AutoRotation' -Name Enable -Value 0 -Type DWord
        Out-Line '  Rotação automática travada.'
    } else {
        Out-Line '  Sem administrador: DISM, sfc e a trava de rotação foram pulados. Rode de novo como administrador.' 'Yellow'
    }

    # Atalhos Ctrl+Alt+Seta do painel Intel (drivers antigos) desligados para a conta atual.
    if (Test-Path 'HKCU:\Software\Intel\Display\igfxcui') {
        New-Item 'HKCU:\Software\Intel\Display\igfxcui\HotKeys' -Force | Out-Null
        Set-ItemProperty 'HKCU:\Software\Intel\Display\igfxcui\HotKeys' -Name Enable -Value 0 -Type DWord
        Out-Line '  Atalhos Ctrl+Alt+Seta do driver Intel desligados nesta conta.'
    }

    # Tela de volta para paisagem, se estiver girada.
    try {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class SomDisplay {
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DEVMODE {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra; public int dmFields;
        public int dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels; public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplaySettings(string d, int m, ref DEVMODE dm);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int ChangeDisplaySettings(ref DEVMODE dm, int flags);
    public static int ResetOrientation() {
        var dm = new DEVMODE(); dm.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
        if (!EnumDisplaySettings(null, -1, ref dm)) return -100;
        if (dm.dmDisplayOrientation == 0) return 1;
        if (dm.dmDisplayOrientation == 1 || dm.dmDisplayOrientation == 3) { int w = dm.dmPelsWidth; dm.dmPelsWidth = dm.dmPelsHeight; dm.dmPelsHeight = w; }
        dm.dmDisplayOrientation = 0; dm.dmFields = 0x80 | 0x80000 | 0x100000;
        return ChangeDisplaySettings(ref dm, 1);
    }
}
'@ -ErrorAction Stop
        $r = [SomDisplay]::ResetOrientation()
        Out-Line $(switch ($r) { 1 { '  Tela já estava em paisagem.' } 0 { '  Tela voltou para paisagem.' } default { "  Não consegui mudar a orientação (código $r); use Configurações > Tela > Orientação." } })
    } catch { Out-Line "  Orientação da tela não verificada: $($_.Exception.Message)" 'Yellow' }

    # Pacotes do Iniciar/Pesquisa recadastrados para a conta atual (correção padrão para 'Classe não registrada').
    Out-Line '  Recadastrando Iniciar, Pesquisa e apps do sistema nesta conta...' 'White'
    $fail = 0
    Get-AppxPackage -ErrorAction SilentlyContinue | Where-Object { $_.SignatureKind -eq 'System' -or $_.Name -match 'ShellExperienceHost|StartMenuExperienceHost|Client\.CBS|Windows\.Search|Cortana|UI\.Xaml' } | ForEach-Object {
        $manifest = Join-Path $_.InstallLocation 'AppxManifest.xml'
        if (Test-Path $manifest) {
            try { Add-AppxPackage -DisableDevelopmentMode -Register $manifest -ErrorAction Stop } catch { $fail++ }
        }
    }
    Out-Line "  Recadastro concluído ($fail pacote(s) em uso ou sem recadastro, normal)."
    Out-Line '  Reinicie o PC. Se o erro aparecer na conta do funcionário, rode este comando também logado nela (sem admin).' 'Green'
}

if ($DesfazerAjustesDeEnergia) {
    Out-Title 'Desfazendo os ajustes de energia do SOM'
    if (-not $isAdmin) { Out-Line '  Precisa de administrador.' 'Yellow' }
    else {
        powercfg /hibernate on | Out-Null
        $usb = '2a737441-1930-4402-8d77-b2bebba308a3'; $sel = '48e6b7a6-50f5-4782-a5d4-53bb8f07e226'
        powercfg /setacvalueindex SCHEME_CURRENT $usb $sel 1 | Out-Null
        powercfg /setdcvalueindex SCHEME_CURRENT $usb $sel 1 | Out-Null
        powercfg /setactive SCHEME_CURRENT | Out-Null
        Set-ItemProperty 'HKLM:\SOFTWARE\Microsoft\WindowsUpdate\UX\Settings' -Name SmartActiveHoursState -Value 1 -Type DWord -ErrorAction SilentlyContinue
        Out-Line '  Hibernação/Inicialização Rápida religadas, suspensão seletiva de USB no padrão e horário ativo automático.'
        Out-Line '  A suspensão continua em "nunca" para a música não parar; mude em Configurações > Energia se quiser.'
    }
}

$outDir = 'C:\MarumbiSom-diagnostico'
New-Item -ItemType Directory -Force $outDir | Out-Null
$outFile = Join-Path $outDir ("diagnostico-{0}-{1:yyyyMMdd-HHmm}.txt" -f $env:COMPUTERNAME, (Get-Date))
$lines | Set-Content -Path $outFile -Encoding UTF8
Write-Host ''
Write-Host "Relatório salvo em $outFile - mande esse arquivo." -ForegroundColor Green
