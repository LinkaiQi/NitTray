; Inno Setup script for NitTray — a per-architecture, per-user installer built
; from the CI publish output. It lays down the app and its WinUSB helper, adds a
; Start Menu shortcut, an optional "run at sign-in" entry, and a proper
; uninstaller.
;
; Built by .github/workflows/release.yml, which passes these on the ISCC command
; line (see that file):
;   /DMyAppVersion=0.1.0
;   /DMyArch=x64            (or arm64)
;   /DSourceDir=<abs path to publish\win-x64>
;   /DOutputBase=NitTray-<tag>-installer-x64

#define MyAppName "NitTray"
#define MyAppPublisher "Linkai Qi"
#define MyAppURL "https://github.com/LinkaiQi/NitTray"
#define MyAppExeName "NitTray.exe"

; Defaults so the script is also openable directly in the Inno IDE for testing.
#ifndef MyAppVersion
  #define MyAppVersion "0.0.0"
#endif
#ifndef MyArch
  #define MyArch "x64"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish\win-x64"
#endif
#ifndef OutputBase
  #define OutputBase "NitTray-installer-" + MyArch
#endif

[Setup]
; Stable identity across versions so upgrades replace in place and the uninstall
; entry is reused. Keep this GUID constant forever.
AppId=F6E903A8-2A56-4159-BB39-397845CF2F68
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
LicenseFile=..\LICENSE
OutputDir=output
OutputBaseFilename={#OutputBase}
SetupIconFile=..\src\client\Assets\AppIcon.ico
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
; Per-user install — no admin needed to install (the WinUSB driver setup still
; elevates on demand at runtime). {autopf} resolves to the per-user Programs dir.
PrivilegesRequired=lowest
VersionInfoVersion={#MyAppVersion}
VersionInfoCompany={#MyAppPublisher}
VersionInfoProductName={#MyAppName}
; Only allow (and 64-bit-install) the matching architecture. The arm64 package is
; native; the x64 package also runs on ARM under x64 emulation as a fallback.
#if MyArch == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif

[Tasks]
Name: "startup"; Description: "Start {#MyAppName} automatically when I sign in"; GroupDescription: "Startup:"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"

[Registry]
; "Run at sign-in" — a per-user entry, removed on uninstall. Only written when the
; startup task is selected.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#MyAppName}"; ValueData: """{app}\{#MyAppExeName}"""; Flags: uninsdeletevalue; Tasks: startup

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent
