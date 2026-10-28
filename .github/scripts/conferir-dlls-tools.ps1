<#
.SYNOPSIS
    Confere que os executáveis e DLLs de tools\ só dependem de DLLs que todo Windows 10 tem ou de arquivos que vão
    junto em tools\.

.DESCRIPTION
    O mpv.exe do shinchiro importa o vulkan-1.dll, que só existe com driver de vídeo instalado. No PC piloto ele
    existia; na Loja 2 (Windows 10 22H2) não, e o mpv morria com 0xC0000135 (STATUS_DLL_NOT_FOUND). O runner do
    GitHub tem quase tudo em System32, então rodar o mpv aqui não pega o problema: a conferência é pela lista de
    importações (dumpbin /dependents). Uma DLL nova fora da lista quebra o CI em vez de quebrar na loja.
#>
param([Parameter(Mandatory)] [string] $Tools)

$ErrorActionPreference = 'Stop'
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$dumpbin = & $vswhere -latest -products * -find 'VC\Tools\MSVC\**\bin\Hostx64\x64\dumpbin.exe' | Select-Object -First 1
if (-not $dumpbin) { throw 'dumpbin.exe não encontrado (ferramentas C++ do Visual Studio)' }

# DLLs de System32 em qualquer Windows 10 1809 ou mais novo, sem driver nem programa extra (as api-ms-win-* também).
$windows = @(
    'advapi32.dll', 'avicap32.dll', 'avrt.dll', 'bcrypt.dll', 'bcryptprimitives.dll', 'cfgmgr32.dll', 'comctl32.dll',
    'comdlg32.dll', 'crypt32.dll', 'd2d1.dll', 'd3d11.dll', 'd3d9.dll', 'dwmapi.dll', 'dwrite.dll', 'dxgi.dll',
    'gdi32.dll', 'imm32.dll', 'iphlpapi.dll', 'kernel32.dll', 'mfplat.dll', 'mmdevapi.dll', 'msvcrt.dll',
    'ncrypt.dll', 'normaliz.dll', 'ntdll.dll', 'ole32.dll', 'oleaut32.dll', 'opengl32.dll', 'powrprof.dll',
    'psapi.dll', 'secur32.dll', 'setupapi.dll', 'shcore.dll', 'shell32.dll', 'shlwapi.dll', 'user32.dll',
    'userenv.dll', 'uxtheme.dll', 'version.dll', 'winhttp.dll', 'winmm.dll', 'wldap32.dll', 'ws2_32.dll'
)

$missing = @()
foreach ($binary in Get-ChildItem -Path $Tools -File | Where-Object { $_.Extension -in '.exe', '.dll' }) {
    # Só a seção de dependências normais; as de carga atrasada não impedem o programa de abrir.
    $inImports = $false
    foreach ($line in & $dumpbin /nologo /dependents $binary.FullName) {
        if ($line -match 'has the following dependencies') { $inImports = $true; continue }
        if ($line -match 'delay load dependencies|^\s*Summary') { $inImports = $false; continue }
        if (-not $inImports -or $line -notmatch '^\s+(\S+\.dll)\s*$') { continue }
        $dll = $Matches[1].ToLowerInvariant()
        if ($dll -like 'api-ms-win-*' -or $windows -contains $dll) { continue }
        if (Test-Path (Join-Path $Tools $dll)) {
            Write-Host "$($binary.Name): $dll vai junto em tools"
            continue
        }
        $missing += "$($binary.Name) depende de $dll, que não vem com o Windows 10 nem está em tools"
    }
}

if ($missing) {
    foreach ($problem in $missing) { Write-Host "::error::$problem" }
    exit 1
}
Write-Host 'Todas as DLLs de tools estão no Windows 10 ou junto em tools.'
