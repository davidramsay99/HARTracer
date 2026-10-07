; Per-user installer for HarLens. Build after scripts/package.ps1 has published the exe:
;   iscc /DArch=x64 /DAppVersion=1.0.0 installer\HarLens.iss
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef Arch
  #define Arch "x64"
#endif
#define SourceExe "..\artifacts\publish-win-" + Arch + "\HarLens.exe"

[Setup]
AppId={{8F3C2A1E-5B7D-4C9E-A2F1-6D4B8E0C7A35}
AppName=HarLens
AppVersion={#AppVersion}
AppPublisher=HarLens
DefaultDirName={autopf}\HarLens
DefaultGroupName=HarLens
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
OutputDir=..\artifacts
OutputBaseFilename=HarLens-{#AppVersion}-{#Arch}-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\HarLens.exe
ChangesAssociations=yes
#if Arch == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif

[Tasks]
Name: "harfiles"; Description: "Add HarLens to ""Open with"" for .har files"
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked

[Files]
Source: "{#SourceExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\THIRD-PARTY-NOTICES.txt"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\HarLens"; Filename: "{app}\HarLens.exe"
Name: "{autodesktop}\HarLens"; Filename: "{app}\HarLens.exe"; Tasks: desktopicon

[Registry]
Root: HKA; Subkey: "Software\Classes\.har\OpenWithProgids"; ValueType: string; ValueName: "HarLens.har"; ValueData: ""; Flags: uninsdeletevalue; Tasks: harfiles
Root: HKA; Subkey: "Software\Classes\HarLens.har"; ValueType: string; ValueName: ""; ValueData: "HTTP Archive"; Flags: uninsdeletekey; Tasks: harfiles
Root: HKA; Subkey: "Software\Classes\HarLens.har\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\HarLens.exe,0"; Tasks: harfiles
Root: HKA; Subkey: "Software\Classes\HarLens.har\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\HarLens.exe"" ""%1"""; Tasks: harfiles

[Run]
Filename: "{app}\HarLens.exe"; Description: "Start HarLens"; Flags: nowait postinstall skipifsilent
