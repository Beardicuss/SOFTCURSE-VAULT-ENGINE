#ifndef PublishSource
  #define PublishSource "..\Publish"
#endif
#ifndef ReleaseOutputDir
  #define ReleaseOutputDir "..\output"
#endif

#define MyAppName "Softcurse Vault Cleaner"
#define MyAppVersion GetFileVersion(PublishSource + "\Win11 Auto-Clean.exe")
#define MyAppPublisher "Softcurse"
#define MyAppURL "https://github.com/Beardicuss/SOFTCURSE-VAULT-ENGINE"
#define MyAppExeName "Win11 Auto-Clean.exe"

[Setup]
; Stable product ID: do not change between releases.
AppId={{A3F7B2C1-8D4E-4F6A-9E2B-1C3D5F7A8B9E}}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
VersionInfoVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir={#ReleaseOutputDir}
OutputBaseFilename=SoftcurseVaultCleaner_Setup_v{#MyAppVersion}
SetupIconFile=..\Resources\vault.ico
UninstallDisplayIcon={app}\vault.ico
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
CloseApplications=yes
RestartApplications=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishSource}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\Resources\vault.ico"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\vault.ico"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\vault.ico"; Tasks: desktopicon
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\vault.ico"

[Run]
; Main app runs as standard user; its fixed maintenance helper elevates separately.
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent shellexec

[UninstallDelete]
Type: filesandordirs; Name: "{app}\logs"
Type: filesandordirs; Name: "{app}\cache"
Type: filesandordirs; Name: "{app}\temp"

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if (CurUninstallStep = usPostUninstall) and (not UninstallSilent) then
  begin
    if MsgBox('Also remove Softcurse Vault Cleaner settings, logs, staged updates, and WebView2 browsing data for this Windows account?',
      mbConfirmation, MB_YESNO) = IDYES then
    begin
      DelTree(ExpandConstant('{userappdata}\SoftcurseVaultCleaner'), True, True, True);
      DelTree(ExpandConstant('{localappdata}\SoftcurseVaultCleaner'), True, True, True);
    end;
  end;
end;
