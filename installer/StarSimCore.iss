#define MyAppName "StarSim Core"
#define MyAppPublisher "Asociația Star Sim"
#define MyAppURL "https://starsim.ro/"
#define MyAppExeName "StarSimCore.App.exe"

#ifndef MyAppVersion
  #define MyAppVersion "0.9.0-beta.1"
#endif
#ifndef MyAppNumericVersion
  #define MyAppNumericVersion "0.9.0.0"
#endif
#ifndef MyPayloadDir
  #define MyPayloadDir "..\artifacts\publish\win-x64\StarSimCore"
#endif
#ifndef MyInstallerOutputDir
  #define MyInstallerOutputDir "..\artifacts\installer"
#endif
#ifndef MyVCRedistFile
  #define MyVCRedistFile "..\artifacts\installer\prerequisites\vc_redist.x64.exe"
#endif

[Setup]
AppId={{7E39F1ED-4669-4D5E-9E73-A4E107FB7C42}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
VersionInfoVersion={#MyAppNumericVersion}
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription={#MyAppName} Windows x64 Installer
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppNumericVersion}
VersionInfoTextVersion={#MyAppVersion}
DefaultDirName={autopf}\StarSim Core
DefaultGroupName=StarSim Core
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir={#MyInstallerOutputDir}
OutputBaseFilename=StarSimCore-Setup-{#MyAppVersion}-win-x64
SetupIconFile=..\src\StarSimCore.App\Assets\starsim-core.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
DisableWelcomePage=no
ShowLanguageDialog=auto
UsePreviousLanguage=yes
CloseApplications=yes
RestartApplications=no
SetupLogging=yes
Uninstallable=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "romanian"; MessagesFile: "compiler:Default.isl,Languages\Romanian.isl"

[CustomMessages]
english.CreateDesktopIcon=Create a &desktop shortcut
english.LaunchStarSimCore=Launch StarSim Core
english.InstallingVCRedist=Installing the required Microsoft Visual C++ runtime...
romanian.CreateDesktopIcon=Creează o scurtătură pe &desktop
romanian.LaunchStarSimCore=Pornește StarSim Core
romanian.InstallingVCRedist=Se instalează componenta necesară Microsoft Visual C++...

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#MyPayloadDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#MyVCRedistFile}"; DestDir: "{tmp}"; DestName: "vc_redist.x64.exe"; Flags: deleteafterinstall

[Icons]
Name: "{group}\StarSim Core"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"
Name: "{group}\{cm:UninstallProgram,StarSim Core}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\StarSim Core"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{tmp}\vc_redist.x64.exe"; Parameters: "/install /quiet /norestart"; StatusMsg: "{cm:InstallingVCRedist}"; Flags: runhidden waituntilterminated; Check: not IsVCRuntimeInstalled
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchStarSimCore}"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent

[Code]
function IsVCRuntimeInstalled: Boolean;
var
  Installed: Cardinal;
begin
  Result :=
    RegQueryDWordValue(
      HKLM64,
      'SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64',
      'Installed',
      Installed) and
    (Installed = 1);
end;
