#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Remove o serviço MarumbiSom.Agent. Os dados em C:\ProgramData\MarumbiSom só são apagados com -RemoveData.
#>
[CmdletBinding()]
param([switch] $RemoveData)

$ErrorActionPreference = 'Stop'
$ServiceName = 'MarumbiSom.Agent'

if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
    sc.exe delete $ServiceName | Out-Null
    Write-Host "Serviço $ServiceName removido."
}
Get-Process -Name mpv -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -like (Join-Path $env:ProgramFiles 'MarumbiSom\*') } |
    Stop-Process -Force
Remove-Item -Recurse -Force (Join-Path $env:ProgramFiles 'MarumbiSom') -ErrorAction SilentlyContinue
if ($RemoveData) {
    Remove-Item -Recurse -Force (Join-Path $env:ProgramData 'MarumbiSom') -ErrorAction SilentlyContinue
    Write-Host 'Dados removidos.'
}
