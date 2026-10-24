; Instalador do agente da loja (Etapa 6, D27 g). Compilado pelo CI com o Inno Setup 6:
;   ISCC /DAppVersion=0.6.41 /DSourceDir=..\publish\agent installer\agent.iss
; Copia a pasta publicada para uma pasta temporária e roda o scripts\install-agent.ps1 numa janela do PowerShell,
; que instala em C:\Program Files\MarumbiSom, registra o serviço e pede a senha da página Configurar Som.
; Para desinstalar: scripts\uninstall-agent.ps1 (o instalador não registra desinstalação própria).

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish\agent"
#endif

[Setup]
AppId={{0E8D7A66-3F7C-4A4C-9B53-AB5131AD2AAB}
AppName=Marumbi Som - Agente da loja
AppVersion={#AppVersion}
AppVerName=Marumbi Som - Agente da loja {#AppVersion}
AppPublisher=Casa de Carnes Marumbi
VersionInfoVersion={#AppVersion}
CreateAppDir=no
Uninstallable=no
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
OutputDir=..\publish\installer
OutputBaseFilename=MarumbiSom.Agent-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "ptbr"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Messages]
ptbr.ReadyLabel1=O instalador vai abrir uma janela do PowerShell para instalar o serviço do agente.
ptbr.ReadyLabel2a=Na primeira instalação, a janela pede a senha da página Configurar Som (mínimo 8 caracteres). Guarde-a no gerenciador de senhas. Clique em Instalar para continuar.

[Files]
Source: "{#SourceDir}\*"; DestDir: "{tmp}\agent"; Flags: recursesubdirs ignoreversion

[Code]
procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
  Agent: String;
  Command: String;
begin
  if CurStep <> ssPostInstall then
    Exit;

  Agent := ExpandConstant('{tmp}\agent');
  { Janela visível: o script pede a senha. No fim (ou em caso de erro) a janela espera o Enter para dar tempo de ler. }
  Command := '-NoProfile -ExecutionPolicy Bypass -Command "try { & ''' + Agent + '\scripts\install-agent.ps1'' -SourceDir ''' + Agent +
    '''; Write-Host; Read-Host ''Instalado. Tecle Enter para fechar''; exit 0 } catch { Write-Host $_ -ForegroundColor Red; Read-Host ''A instalação falhou. Tecle Enter para fechar''; exit 1 }"';
  WizardForm.StatusLabel.Caption := 'Instalando o serviço do agente (veja a janela do PowerShell)...';
  if not Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'), Command, Agent, SW_SHOW, ewWaitUntilTerminated, ResultCode) then
    MsgBox('Não foi possível abrir o PowerShell: ' + SysErrorMessage(ResultCode), mbError, MB_OK)
  else if ResultCode <> 0 then
    MsgBox('A instalação do serviço falhou (código ' + IntToStr(ResultCode) + '). Veja a mensagem na janela do PowerShell e o checklist de instalação.', mbError, MB_OK);
end;
