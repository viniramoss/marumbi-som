<#
.SYNOPSIS
    Gera o pacote assinado de uma versão do agente para enviar no painel (seção 12.2, passo 1; D27 b).

.DESCRIPTION
    Compacta a pasta do agente publicada pelo CI (artefato MarumbiSom.Agent-win-x64) em MarumbiSom.Agent-<versão>.zip
    e assina com a chave privada em MarumbiSom.Agent-<versão>.sig.json. A versão é a do MarumbiSom.Agent.dll. Os dois
    arquivos vão em Configurações > Versão do agente para distribuição, no painel.

    Confere que o release-public-key.txt da pasta tem a chave pública desta assinatura: um pacote com outra chave
    deixaria as lojas atualizadas sem aceitar as próximas atualizações (use -AllowKeyChange só se a troca for de
    propósito).

.PARAMETER AgentDir
    Pasta do artefato MarumbiSom.Agent-win-x64 extraída.

.PARAMETER KeyFile
    Chave privada gerada pelo new-signing-key.ps1.

.PARAMETER OutDir
    Onde gravar o .zip e o .sig.json (padrão: a pasta atual). Não pode ficar dentro de -AgentDir.

.PARAMETER Version
    Opcional. Se informada, precisa ser a do MarumbiSom.Agent.dll (ou a versão do pacote de teste quebrado).

.PARAMETER TestInvalidSignature
    Gera um pacote de teste assinado com uma chave descartável (critério de aceite: a loja recusa).

.PARAMETER TestBroken
    Gera um pacote de teste com o agente estragado, na -Version informada (critério de aceite: a loja volta sozinha
    para a versão anterior).

.EXAMPLE
    .\sign-release.ps1 -AgentDir C:\Users\vinicius\Downloads\MarumbiSom.Agent-win-x64

.EXAMPLE
    .\sign-release.ps1 -AgentDir ..\MarumbiSom.Agent-win-x64 -TestBroken -Version 0.6.999
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $AgentDir,
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

$AgentDir = (Resolve-Path $AgentDir).Path
if (-not (Test-Path $KeyFile)) { throw "Chave privada não encontrada em $KeyFile. Gere com .\new-signing-key.ps1 (uma vez só)." }

$toolArgs = @('pack', '--key', $KeyFile, '--agent', $AgentDir, '--out', $OutDir)
if ($Version) { $toolArgs += @('--version', $Version) }
if ($AllowKeyChange) { $toolArgs += '--allow-key-change' }
if ($TestInvalidSignature) { $toolArgs += '--test-invalid-signature' }
if ($TestBroken) { $toolArgs += '--test-broken' }
Invoke-ReleaseTool -Tool $Tool -Arguments $toolArgs
