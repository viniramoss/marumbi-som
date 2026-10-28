<#
.SYNOPSIS
    Gera a chave de assinatura das atualizações do agente (seção 12.2, D27 b). Rode UMA vez, no PC de
    desenvolvimento do administrador.

.DESCRIPTION
    - Grava a chave PRIVADA em -KeyFile (padrão: Documentos\MarumbiSom\chave-assinatura-atualizacoes.txt), com acesso
      só para o seu usuário. Ela nunca vai para o Git, para o Hub ou para a nuvem (seção 12.1). Guarde uma cópia
      offline (pendrive no cofre): sem ela, as lojas não aceitam mais atualizações remotas.
    - Mostra a chave PÚBLICA, que vai no repositório em src\MarumbiSom.Agent\release-public-key.txt (ou é mandada a
      quem cuida do repositório). Os builds seguintes do CI já saem com ela.
    - Com -AgentDir, grava também a chave pública no release-public-key.txt de uma pasta do agente já baixada do CI,
      para instalar uma loja de teste sem esperar um build novo.

.PARAMETER KeyFile
    Onde gravar a chave privada. Não sobrescreve um arquivo existente.

.PARAMETER AgentDir
    Opcional. Pasta do artefato MarumbiSom.Agent-win-x64 extraída.

.PARAMETER Tool
    Caminho do MarumbiSom.Release.exe (padrão: ao lado deste script).

.EXAMPLE
    .\new-signing-key.ps1

.EXAMPLE
    .\new-signing-key.ps1 -AgentDir C:\Users\vinicius\Downloads\MarumbiSom.Agent-win-x64
#>
[CmdletBinding()]
param(
    [string] $KeyFile = (Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'MarumbiSom\chave-assinatura-atualizacoes.txt'),
    [string] $AgentDir,
    [string] $Tool
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'release-tool.ps1')

if (Test-Path $KeyFile) {
    throw "Já existe uma chave em $KeyFile. Para não perder a atual (as lojas confiam nela), escolha outro -KeyFile ou apague de propósito."
}

$toolArgs = @('new-key', '--out', $KeyFile)
if ($AgentDir) {
    $AgentDir = (Resolve-Path $AgentDir).Path
    if (-not (Test-Path (Join-Path $AgentDir 'MarumbiSom.Agent.exe'))) { throw "MarumbiSom.Agent.exe não encontrado em $AgentDir." }
    $toolArgs += @('--public-out', (Join-Path $AgentDir 'release-public-key.txt'))
}
Invoke-ReleaseTool -Tool $Tool -Arguments $toolArgs

# Só o usuário atual lê a chave privada (SID em vez de nome, para funcionar no Windows em português).
$sid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value
icacls $KeyFile /inheritance:r /grant:r "*${sid}:F" /C /Q | Out-Null
if ($LASTEXITCODE -ne 0) { throw "icacls falhou com código $LASTEXITCODE" }

Write-Host ""
Write-Host "Pronto. Próximos passos:" -ForegroundColor Green
Write-Host "  1. Copie a chave privada para um pendrive guardado em local seguro (cópia offline)."
Write-Host "  2. Cole a chave pública acima em src\MarumbiSom.Agent\release-public-key.txt no repositório (ou mande para"
Write-Host "     quem cuida do repositório). Os próximos builds do CI já saem com ela."
Write-Host "  3. Para assinar uma versão: .\sign-release.ps1 -AgentDir <pasta do MarumbiSom.Agent-win-x64>"
