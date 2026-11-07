# Usado por new-signing-key.ps1 e sign-release.ps1: acha e roda o MarumbiSom.Release (D27 b).
# Procura ao lado dos scripts (artefato MarumbiSom.Release-win-x64) e, no repositório, roda o projeto com o SDK.

function Invoke-ReleaseTool {
    param([string] $Tool, [string[]] $Arguments)

    if (-not $Tool) {
        $Tool = Join-Path $PSScriptRoot 'MarumbiSom.Release.exe'
    }
    if (Test-Path $Tool) {
        & $Tool @Arguments
    } else {
        $project = Join-Path $PSScriptRoot '..\src\MarumbiSom.Release'
        if (-not (Test-Path $project)) {
            throw "MarumbiSom.Release.exe não encontrado em $Tool. Baixe o artefato MarumbiSom.Release-win-x64 do CI e rode os scripts de dentro dele."
        }
        & dotnet run --project $project -c Release -- @Arguments
    }
    if ($LASTEXITCODE -ne 0) { throw "MarumbiSom.Release falhou (código $LASTEXITCODE)." }
}
