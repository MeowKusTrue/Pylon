; Bridge from the Inno Setup era (Pylon 1.0-1.2) to Velopack (1.3+).
; The in-app updater of those versions downloads "Pylon-x.y.z-setup-win-x64.exe" from the latest GitHub release and runs
; it with /SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS. Velopack's own setup rejects those switches, so this
; tiny installer has that name instead: it runs the Velopack setup silently and starts Pylon. The new copy removes the
; old Inno install on its first run (ShellRegistration.RemoveInnoInstall).
; Built by build.ps1:  ISCC installer\Bridge.iss /DAppVersion=1.3.0

#ifndef AppVersion
  #define AppVersion "1.3.0"
#endif
#ifndef InstallArgs
  #define InstallArgs "--silent"
#endif
#ifndef LaunchExe
  #define LaunchExe "{localappdata}\Pylon\Pylon.exe"
#endif
#ifndef VelopackSetup
  #define VelopackSetup "..\dist\releases\Pylon-win-Setup.exe"
#endif

[Setup]
AppId={{3E5A1C2D-7B48-4F0E-9A61-2C8D4B7E91F3}
AppName=Pylon
AppVersion={#AppVersion}
AppPublisher=Pylon contributors
Uninstallable=no
CreateAppDir=no
PrivilegesRequired=lowest
DisableWelcomePage=yes
DisableReadyPage=yes
DisableFinishedPage=yes
OutputDir=..\dist\releases
OutputBaseFilename=Pylon-{#AppVersion}-setup-win-x64
SetupIconFile=..\Pylon\Assets\pylon.ico
; the Velopack setup inside is already compressed
Compression=none
WizardStyle=modern

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"

[CustomMessages]
en.Installing=Installing Pylon…
ru.Installing=Устанавливаю Pylon…

[Files]
Source: "{#VelopackSetup}"; DestDir: "{tmp}"; DestName: "PylonSetup.exe"; Flags: deleteafterinstall

[Run]
Filename: "{tmp}\PylonSetup.exe"; Parameters: "{#InstallArgs}"; StatusMsg: "{cm:Installing}"; Flags: waituntilterminated
; if Velopack already started it, the second launch just hands over to the running copy
Filename: "{#LaunchExe}"; Flags: nowait
