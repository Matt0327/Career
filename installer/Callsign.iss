; Inno Setup script for Callsign — the public installer.
;
; Build:
;   1. Produce the self-contained app:
;        dotnet publish app/Callsign.Desktop -c Release -r win-x64 --self-contained true -o <publishDir>
;   2. Compile this installer (Inno Setup 6, free — https://jrsoftware.org/isdl.php):
;        ISCC.exe installer\Callsign.iss /DSourceDir="<publishDir>"
;   -> produces installer\out\Callsign-Setup-<ver>.exe
;
; It installs per-user (no admin/UAC), makes shortcuts, and prompts to install the
; Microsoft Edge WebView2 runtime if the PC doesn't already have it.

#ifndef SourceDir
  #define SourceDir "..\dist\callsign-standalone"
#endif
#ifndef AppVersion
  #define AppVersion "0.4.0"
#endif
#define AppName "BentoFly"
#define AppPublisher "BentoFly"
#define AppExe "BentoFly.exe"

[Setup]
AppId={{B9E1B2B0-1C3D-4E5F-A6B7-C8D9E0F10203}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=out
OutputBaseFilename=BentoFly-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\{#AppExe}
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{userdesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"

[Run]
Filename: "{app}\{#AppExe}"; Description: "Launch BentoFly now"; Flags: nowait postinstall skipifsilent

[Code]
{ True if the Microsoft Edge WebView2 Evergreen runtime is installed (machine- or per-user). }
function IsWebView2Installed(): Boolean;
var pv: String;
begin
  Result := False;
  if RegQueryStringValue(HKLM, 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', pv) then
    Result := (pv <> '') and (pv <> '0.0.0.0');
  if not Result then
    if RegQueryStringValue(HKCU, 'Software\Microsoft\EdgeUpdate\Clients\{{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', pv) then
      Result := (pv <> '') and (pv <> '0.0.0.0');
end;

procedure CurStepChanged(CurStep: TSetupStep);
var ErrorCode: Integer;
begin
  if (CurStep = ssPostInstall) and (not IsWebView2Installed()) then
  begin
    if MsgBox('BentoFly needs the Microsoft Edge WebView2 runtime, which is not installed on this PC.' + #13#10#13#10 +
              'Open the free download page now? (Install it, then launch BentoFly.)',
              mbConfirmation, MB_YESNO) = IDYES then
      ShellExec('', 'https://developer.microsoft.com/microsoft-edge/webview2/', '', '', SW_SHOW, ewNoWait, ErrorCode);
  end;
end;
