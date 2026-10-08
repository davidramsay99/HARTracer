; Per-user installer for Harborer. Build after scripts/package.ps1 has published the exe:
;   iscc /DArch=x64 /DAppVersion=1.0.0 installer\Harborer.iss
#ifndef AppVersion
  #define AppVersion "1.0.2"
#endif
#ifndef Arch
  #define Arch "x64"
#endif
#define SourceExe "..\artifacts\publish-win-" + Arch + "\HARborer.exe"

[Setup]
AppId={{1BCD4D57-CD71-472C-848F-122F49500D33}
AppName=HARborer
AppVersion={#AppVersion}
AppPublisher=HARborer
DefaultDirName={autopf}\HARborer
DefaultGroupName=HARborer
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
OutputDir=..\artifacts
OutputBaseFilename=HARborer-{#AppVersion}-{#Arch}-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\HARborer.exe
ChangesAssociations=yes
#if Arch == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif

[Tasks]
Name: "harfiles"; Description: "Add HARborer to ""Open with"" for .har files"
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked

[Files]
Source: "{#SourceExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\THIRD-PARTY-NOTICES.txt"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\HARborer"; Filename: "{app}\HARborer.exe"
Name: "{autodesktop}\HARborer"; Filename: "{app}\HARborer.exe"; Tasks: desktopicon

[Registry]
Root: HKA; Subkey: "Software\Classes\.har\OpenWithProgids"; ValueType: string; ValueName: "Harborer.har"; ValueData: ""; Flags: uninsdeletevalue; Tasks: harfiles
Root: HKA; Subkey: "Software\Classes\Harborer.har"; ValueType: string; ValueName: ""; ValueData: "HTTP Archive"; Flags: uninsdeletekey; Tasks: harfiles
Root: HKA; Subkey: "Software\Classes\Harborer.har\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\HARborer.exe,0"; Tasks: harfiles
Root: HKA; Subkey: "Software\Classes\Harborer.har\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\HARborer.exe"" ""%1"""; Tasks: harfiles

[Run]
Filename: "{app}\HARborer.exe"; Description: "Start HARborer"; Flags: nowait postinstall skipifsilent
