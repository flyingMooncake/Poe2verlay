; Inno Setup 6 script for the poe2verlay installer. Run compile.bat, which builds dist\poe2verlay.exe first.
; Installs per user (no admin rights) to %LOCALAPPDATA%\Programs\poe2verlay. Uninstalling keeps the
; settings and saved runs in %APPDATA%\poe2verlay.

#define AppName "poe2verlay"
#define AppExe "poe2verlay.exe"
#define ExeFile AddBackslash(SourcePath) + "..\dist\" + AppExe
#ifndef AppVersion
  #define AppVersion GetStringFileInfo(ExeFile, "ProductVersion")
#endif
#if AppVersion == ""
  #define AppVersion "1.0.0"
#endif

[Setup]
AppId={{8C4F2A61-3B7E-4D19-9E52-7A0F1C6D2B94}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=FlyingMoonCake
AppPublisherURL=https://github.com/flyingMooncake/Poe2verlay
AppSupportURL=https://github.com/flyingMooncake/Poe2verlay/issues
AppUpdatesURL=https://github.com/flyingMooncake/Poe2verlay/releases
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
OutputDir=..\dist
OutputBaseFilename={#AppName}-setup-{#AppVersion}
SetupIconFile=..\assets\poe2verlay.ico
UninstallDisplayIcon={app}\{#AppExe}
WizardStyle=modern
Compression=lzma2
SolidCompression=yes
CloseApplications=force
RestartApplications=no

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "startup"; Description: "Start {#AppName} with Windows"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#ExeFile}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon
Name: "{userstartup}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: startup

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; The tray app keeps running in the background; stop it so its exe can be removed.
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM {#AppExe}"; Flags: runhidden; RunOnceId: "StopApp"
