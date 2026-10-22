#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Prepara o PC da loja para tocar sem intervenção (seção 14.3). Idempotente; gera um relatório.

.PARAMETER EmployeeAccount
    Nome da conta Windows usada pelos funcionários. O script alerta se ela for administradora.

.PARAMETER ActiveHoursStart / ActiveHoursEnd
    Horário ativo do Windows Update (o Windows não reinicia para atualizar dentro dele). Padrão 07–21.
#>
[CmdletBinding()]
param(
    [string] $EmployeeAccount,
    [ValidateRange(0, 23)] [int] $ActiveHoursStart = 7,
    [ValidateRange(0, 23)] [int] $ActiveHoursEnd = 21
)

$ErrorActionPreference = 'Stop'
$report = [System.Collections.Generic.List[string]]::new()
$warnings = 0

function Add-Report([string] $Status, [string] $Message) {
    $line = "[{0}] {1}" -f $Status, $Message
    $report.Add($line)
    $color = switch ($Status) { 'OK' { 'Green' } 'ALTERADO' { 'Cyan' } default { 'Yellow' } }
    Write-Host $line -ForegroundColor $color
    if ($Status -eq 'ATENÇÃO') { $script:warnings++ }
}

function Invoke-Step([string] $Description, [scriptblock] $Action) {
    try {
        & $Action
    } catch {
        Add-Report 'ATENÇÃO' "$Description falhou: $($_.Exception.Message)"
    }
}

# 1. Hibernação desligada (também desliga a Inicialização Rápida, que atrapalha drivers USB/Bluetooth).
Invoke-Step 'Desativar hibernação' {
    powercfg /hibernate off | Out-Null
    Add-Report 'ALTERADO' 'Hibernação e Inicialização Rápida desativadas'
}

# 2. Suspensão e hibernação "nunca" na energia AC.
Invoke-Step 'Suspensão nunca' {
    powercfg /change standby-timeout-ac 0 | Out-Null
    powercfg /change hibernate-timeout-ac 0 | Out-Null
    Add-Report 'ALTERADO' 'Suspensão e hibernação em "nunca" na energia AC'
}

# 3. Suspensão seletiva de USB desativada no plano ativo.
Invoke-Step 'Suspensão seletiva de USB' {
    $usbSubgroup = '2a737441-1930-4402-8d77-b2bebba308a3'
    $selectiveSuspend = '48e6b7a6-50f5-4782-a5d4-53bb8f07e226'
    powercfg /setacvalueindex SCHEME_CURRENT $usbSubgroup $selectiveSuspend 0 | Out-Null
    powercfg /setdcvalueindex SCHEME_CURRENT $usbSubgroup $selectiveSuspend 0 | Out-Null
    powercfg /setactive SCHEME_CURRENT | Out-Null
    Add-Report 'ALTERADO' 'Suspensão seletiva de USB desativada'
}

# 4. "Permitir que o computador desligue este dispositivo" desmarcado no adaptador Bluetooth e hubs USB.
Invoke-Step 'Economia de energia dos dispositivos' {
    $targets = Get-CimInstance Win32_PnPEntity | Where-Object {
        ($_.PNPClass -eq 'Bluetooth' -and $_.DeviceID -like 'USB\*') -or
        ($_.PNPClass -eq 'USB' -and ($_.DeviceID -like 'USB\ROOT_HUB*' -or $_.Name -match 'Hub'))
    }
    $power = Get-CimInstance -Namespace root\wmi -ClassName MSPower_DeviceEnable -ErrorAction SilentlyContinue
    foreach ($device in $targets) {
        $entry = $power | Where-Object { $_.InstanceName -like "$($device.DeviceID)*" }
        if (-not $entry) {
            Add-Report 'ATENÇÃO' "Sem opção de energia via WMI para '$($device.Name)'; confira no Gerenciador de Dispositivos"
            continue
        }
        if ($entry.Enable) {
            $entry | Set-CimInstance -Property @{ Enable = $false }
            Add-Report 'ALTERADO' "Economia de energia desativada: $($device.Name)"
        } else {
            Add-Report 'OK' "Economia de energia já desativada: $($device.Name)"
        }
    }
    if (-not ($targets | Where-Object { $_.PNPClass -eq 'Bluetooth' })) {
        Add-Report 'ATENÇÃO' 'Nenhum adaptador Bluetooth USB encontrado'
    }
}

# 5. Horário ativo do Windows Update cobrindo o horário de funcionamento.
Invoke-Step 'Horário ativo do Windows Update' {
    $key = 'HKLM:\SOFTWARE\Microsoft\WindowsUpdate\UX\Settings'
    New-Item -Path $key -Force | Out-Null
    Set-ItemProperty -Path $key -Name ActiveHoursStart -Value $ActiveHoursStart -Type DWord
    Set-ItemProperty -Path $key -Name ActiveHoursEnd -Value $ActiveHoursEnd -Type DWord
    Set-ItemProperty -Path $key -Name SmartActiveHoursState -Value 0 -Type DWord
    Add-Report 'ALTERADO' ("Horário ativo do Windows Update: {0:00}:00–{1:00}:00" -f $ActiveHoursStart, $ActiveHoursEnd)
}

# 6. Fuso horário de Brasília e sincronização de horário.
Invoke-Step 'Fuso e horário' {
    if ((Get-TimeZone).Id -ne 'E. South America Standard Time') {
        Set-TimeZone -Id 'E. South America Standard Time'
        Add-Report 'ALTERADO' 'Fuso horário definido para E. South America Standard Time'
    } else {
        Add-Report 'OK' 'Fuso horário já é E. South America Standard Time'
    }
    Set-Service -Name w32time -StartupType Automatic
    if ((Get-Service w32time).Status -ne 'Running') { Start-Service w32time }
    w32tm /resync /force | Out-Null
    if ($LASTEXITCODE -eq 0) { Add-Report 'OK' 'Horário sincronizado (w32tm /resync)' }
    else { Add-Report 'ATENÇÃO' "w32tm /resync retornou $LASTEXITCODE" }
}

# 7. Serviços de Bluetooth e áudio com início automático.
foreach ($name in 'bthserv', 'Audiosrv') {
    Invoke-Step "Serviço $name" {
        $service = Get-Service -Name $name
        if ($service.StartType -ne 'Automatic') {
            Set-Service -Name $name -StartupType Automatic
            Add-Report 'ALTERADO' "Serviço $name com início automático"
        } else {
            Add-Report 'OK' "Serviço $name já tem início automático"
        }
        if ($service.Status -ne 'Running') { Start-Service -Name $name }
    }
}

# 8. Contas locais: o funcionário NÃO pode ser administrador (regra 11).
Invoke-Step 'Contas locais' {
    $admins = Get-LocalGroupMember -SID 'S-1-5-32-544' | ForEach-Object { ($_.Name -split '\\')[-1] }
    foreach ($user in Get-LocalUser | Where-Object Enabled) {
        $isAdmin = $admins -contains $user.Name
        Add-Report 'OK' ("Conta '{0}' {1}" -f $user.Name, $(if ($isAdmin) { 'é ADMINISTRADORA' } else { 'é padrão' }))
    }
    if ($EmployeeAccount) {
        if ($admins -contains $EmployeeAccount) {
            Add-Report 'ATENÇÃO' "A conta do funcionário '$EmployeeAccount' é administradora. Torne-a padrão (regra 11)."
        } else {
            Add-Report 'OK' "A conta do funcionário '$EmployeeAccount' não é administradora"
        }
    } else {
        Add-Report 'ATENÇÃO' 'Informe -EmployeeAccount para conferir a conta do funcionário'
    }
}

$reportDir = Join-Path $env:ProgramData 'MarumbiSom'
New-Item -ItemType Directory -Force -Path $reportDir | Out-Null
$reportPath = Join-Path $reportDir ("harden-report-{0:yyyy-MM-dd-HHmm}.txt" -f (Get-Date))
$report | Set-Content -Path $reportPath -Encoding UTF8
Write-Host ""
Write-Host "Relatório salvo em $reportPath ($warnings item(ns) de atenção)." -ForegroundColor $(if ($warnings) { 'Yellow' } else { 'Green' })
