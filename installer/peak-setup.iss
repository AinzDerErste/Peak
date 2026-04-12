; Peak - Dynamic Island for Windows
; Inno Setup Script

#define MyAppName "Peak"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "Peak"
#define MyAppExeName "Peak.App.exe"
#define MyAppURL "https://github.com/AinzDerErste/Peak"

#define PublishDir "..\src\Peak.App\bin\Release\net8.0-windows10.0.22621.0\publish"
#define DiscordPluginDir "..\src\Peak.Plugins.Discord\bin\Release\net8.0-windows10.0.22621.0"
#define TeamSpeakPluginDir "..\src\Peak.Plugins.TeamSpeak\bin\Release\net8.0-windows10.0.22621.0"
#define IconFile "..\src\Peak.App\Assets\app-icon.ico"

[Setup]
AppId={{B8F3E2A1-7C4D-4E5F-9A6B-1D2E3F4A5B6C}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
DefaultDirName={localappdata}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=output
OutputBaseFilename=PeakSetup
SetupIconFile={#IconFile}
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/max
SolidCompression=yes
PrivilegesRequired=lowest
WizardStyle=modern
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "german"; MessagesFile: "compiler:Languages\German.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "autostart"; Description: "Start Peak with Windows"; GroupDescription: "Additional options:"
Name: "discord"; Description: "Install Discord plugin (voice call integration)"; GroupDescription: "Plugins:"
Name: "teamspeak"; Description: "Install TeamSpeak 6 plugin (voice channel display)"; GroupDescription: "Plugins:"

[Files]
; Main application
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; Discord plugin → %APPDATA%\Peak\plugins\discord\
Source: "{#DiscordPluginDir}\Peak.Plugins.Discord.dll"; DestDir: "{userappdata}\Peak\plugins\discord"; Flags: ignoreversion; Tasks: discord
; TeamSpeak plugin → %APPDATA%\Peak\plugins\teamspeak\
Source: "{#TeamSpeakPluginDir}\Peak.Plugins.TeamSpeak.dll"; DestDir: "{userappdata}\Peak\plugins\teamspeak"; Flags: ignoreversion; Tasks: teamspeak

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "SOFTWARE\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#MyAppName}"; ValueData: """{app}\{#MyAppExeName}"" --minimized"; Flags: uninsdeletevalue; Tasks: autostart

[Run]
; Interactive install: offer launch after setup wizard
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent
; Silent install (e.g. triggered by in-app updater): always relaunch the app
Filename: "{app}\{#MyAppExeName}"; Flags: nowait runasoriginaluser; Check: WizardSilent

[UninstallRun]
Filename: "taskkill"; Parameters: "/F /IM {#MyAppExeName}"; Flags: runhidden; RunOnceId: "KillPeak"

[UninstallDelete]
Type: filesandordirs; Name: "{app}"
Type: filesandordirs; Name: "{userappdata}\Peak\plugins\discord"
Type: filesandordirs; Name: "{userappdata}\Peak\plugins\teamspeak"

[Code]
function IsDotNet8Installed(): Boolean;
var
  ResultCode: Integer;
begin
  Result := Exec('dotnet', '--list-runtimes', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
  if Result then
  begin
    // Check if .NET 8 desktop runtime is available
    Result := Exec('cmd', '/c dotnet --list-runtimes | findstr "Microsoft.WindowsDesktop.App 8."', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
  end;
end;

function InitializeSetup(): Boolean;
begin
  Result := True;
  if not IsDotNet8Installed() then
  begin
    if MsgBox('Peak requires .NET 8 Desktop Runtime which was not found on your system.' + #13#10 + #13#10 +
              'Please download and install it from:' + #13#10 +
              'https://dotnet.microsoft.com/download/dotnet/8.0' + #13#10 + #13#10 +
              'Do you want to continue the installation anyway?',
              mbConfirmation, MB_YESNO) = IDNO then
    begin
      Result := False;
    end;
  end;
end;
