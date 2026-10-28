<#
.SYNOPSIS
    Gera o pacote assinado de uma versão do agente (ou do Hub) para enviar no painel (seção 12.2, passo 1; D27 b, D32).

.DESCRIPTION
    Compacta a pasta do agente publicada pelo CI (artefato MarumbiSom.Agent-win-x64) em MarumbiSom.Agent-<versão>.zip
    e assina com a chave privada em MarumbiSom.Agent-<versão>.sig.json. A versão é a do MarumbiSom.Agent.dll. Os dois
    arquivos vão em Configurações > Versão do agente para distribuição, no painel.

    Confere que o release-public-key.txt da pasta tem a chave pública desta assinatura: um pacote com outra chave
    deixaria as lojas atualizadas sem aceitar as próximas atualizações (use -AllowKeyChange só se a troca for de
    propósito).

    Com -HubDir, gera MarumbiSom.Hub-<versão>.zip e .sig.json da pasta do artefato MarumbiSom.Hub-win-x64 (versão do
    MarumbiSom.Hub.dll), para Configurações > Atualização do Hub > Enviar pacote. A assinatura do Hub é própria: um pacote do agente
    não passa por pacote do Hub.

.PARAMETER AgentDir
    Pasta do artefato MarumbiSom.Agent-win-x64 extraída.

.PARAMETER HubDir
    No lugar de -AgentDir: pasta do artefato MarumbiSom.Hub-win-x64 extraída.

.PARAMETER KeyFile
    Chave privada gerada pelo new-signing-key.ps1.

.PARAMETER OutDir
    Onde gravar o .zip e o .sig.json (padrão: a pasta atual). Não pode ficar dentro de -AgentDir.

.PARAMETER Version
    Opcional. Se informada, precisa ser a do MarumbiSom.Agent.dll (nos pacotes de teste, qualquer versão).

.PARAMETER TestInvalidSignature
    Gera um pacote de teste assinado com uma chave descartável (critério de aceite: a loja recusa). Com -Version, usa
    uma versão própria (ex.: 0.6.998), para não ocupar no Hub a versão do pacote bom.

.PARAMETER TestBroken
    Gera um pacote de teste que não sobe (critério de aceite: volta sozinho para a versão anterior). No agente, estraga
    o programa e usa a -Version informada. No Hub, usa a versão do build (mais novo que o instalado, sem -Version) e
    põe no appsettings.json um endereço que derruba o Hub depois de abrir o banco, para testar também a volta do banco.

.EXAMPLE
    .\sign-release.ps1 -AgentDir C:\Users\vinicius\Downloads\MarumbiSom.Agent-win-x64

.EXAMPLE
    .\sign-release.ps1 -AgentDir ..\MarumbiSom.Agent-win-x64 -TestInvalidSignature -Version 0.6.998

.EXAMPLE
    .\sign-release.ps1 -AgentDir ..\MarumbiSom.Agent-win-x64 -TestBroken -Version 0.6.999

.EXAMPLE
    .\sign-release.ps1 -HubDir ..\MarumbiSom.Hub-win-x64 -TestBroken
#>
[CmdletBinding()]
param(
    [string] $AgentDir,
    [string] $HubDir,
    [string] $KeyFile = (Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'MarumbiSom\chave-assinatura-atualizacoes.txt'),
    [string] $OutDir = (Get-Location).Path,
    [string] $Version,
    [switch] $AllowKeyChange,
    [switch] $TestInvalidSignature,
    [switch] $TestBroken,
    [string] $Tool
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'release-tool.ps1')

if ([bool]$AgentDir -eq [bool]$HubDir) { throw 'Informe a pasta do agente (-AgentDir) ou a do Hub (-HubDir), uma das duas.' }
if (-not (Test-Path $KeyFile)) { throw "Chave privada não encontrada em $KeyFile. Gere com .\new-signing-key.ps1 (uma vez só)." }

if ($AgentDir) {
    $toolArgs = @('pack', '--key', $KeyFile, '--agent', (Resolve-Path $AgentDir).Path, '--out', $OutDir)
} else {
    $toolArgs = @('pack', '--key', $KeyFile, '--hub', (Resolve-Path $HubDir).Path, '--out', $OutDir)
}
if ($Version) { $toolArgs += @('--version', $Version) }
if ($AllowKeyChange) { $toolArgs += '--allow-key-change' }
if ($TestInvalidSignature) { $toolArgs += '--test-invalid-signature' }
if ($TestBroken) { $toolArgs += '--test-broken' }
Invoke-ReleaseTool -Tool $Tool -Arguments $toolArgs
