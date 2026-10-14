; Pylon installer (Inno Setup 6). Built by build.ps1:  ISCC installer\Pylon.iss /DAppVersion=1.0.0
; Per-user install by default (no admin needed); file associations go to HKCU (or HKLM for an all-users install).

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#define SourceDir "..\dist\Pylon"
#define AppExe "Pylon.exe"

[Setup]
AppId={{6C7E2B0A-3F4D-4C1B-9E2A-7A51D0C3B9F4}
AppName=Pylon
AppVersion={#AppVersion}
AppVerName=Pylon {#AppVersion}
AppPublisher=Pylon contributors
AppComments=A media player built on libVLC, with a redesigned VLC interface.
LicenseFile=..\LICENSE
DefaultDirName={autopf}\Pylon
DefaultGroupName=Pylon
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
ChangesAssociations=yes
OutputDir=..\dist
OutputBaseFilename=Pylon-{#AppVersion}-setup-win-x64
SetupIconFile=..\Pylon\Assets\pylon.ico
UninstallDisplayIcon={app}\{#AppExe}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"

[CustomMessages]
en.AssocTask=Associate audio and video files with Pylon
ru.AssocTask=Связать аудио- и видеофайлы с Pylon
en.FolderTask=Add "Play with Pylon" to folder context menus
ru.FolderTask=Добавить «Воспроизвести в Pylon» в меню папок
en.PlayFolder=Play with Pylon
ru.PlayFolder=Воспроизвести в Pylon
en.VideoFile=Video file (Pylon)
ru.VideoFile=Видеофайл (Pylon)
en.AudioFile=Audio file (Pylon)
ru.AudioFile=Аудиофайл (Pylon)
en.OpenDefaults=Make Pylon the default player (opens Windows settings)
ru.OpenDefaults=Сделать Pylon плеером по умолчанию (откроются настройки Windows)

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "assoc"; Description: "{cm:AssocTask}"
Name: "folder"; Description: "{cm:FolderTask}"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Pylon"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\Pylon"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; ProgIDs
Root: HKA; Subkey: "Software\Classes\Pylon.Video"; ValueType: string; ValueData: "{cm:VideoFile}"; Flags: uninsdeletekey; Tasks: assoc
Root: HKA; Subkey: "Software\Classes\Pylon.Video\DefaultIcon"; ValueType: string; ValueData: "{app}\FileIcons\file-video.ico"; Tasks: assoc
Root: HKA; Subkey: "Software\Classes\Pylon.Video\shell\open\command"; ValueType: string; ValueData: """{app}\{#AppExe}"" ""%1"""; Tasks: assoc
Root: HKA; Subkey: "Software\Classes\Pylon.Audio"; ValueType: string; ValueData: "{cm:AudioFile}"; Flags: uninsdeletekey; Tasks: assoc
Root: HKA; Subkey: "Software\Classes\Pylon.Audio\DefaultIcon"; ValueType: string; ValueData: "{app}\FileIcons\file-audio.ico"; Tasks: assoc
Root: HKA; Subkey: "Software\Classes\Pylon.Audio\shell\open\command"; ValueType: string; ValueData: """{app}\{#AppExe}"" ""%1"""; Tasks: assoc

; "Open with" entry + Default Apps registration
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}"; ValueType: string; ValueName: "FriendlyAppName"; ValueData: "Pylon"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\shell\open\command"; ValueType: string; ValueData: """{app}\{#AppExe}"" ""%1"""
Root: HKA; Subkey: "Software\Pylon\Capabilities"; ValueType: string; ValueName: "ApplicationName"; ValueData: "Pylon"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Pylon\Capabilities"; ValueType: string; ValueName: "ApplicationDescription"; ValueData: "A media player built on libVLC."
Root: HKA; Subkey: "Software\Pylon\Capabilities"; ValueType: string; ValueName: "ApplicationIcon"; ValueData: "{app}\{#AppExe},0"
Root: HKA; Subkey: "Software\RegisteredApplications"; ValueType: string; ValueName: "Pylon"; ValueData: "Software\Pylon\Capabilities"; Flags: uninsdeletevalue

; Folder context menu
Root: HKA; Subkey: "Software\Classes\Directory\shell\Pylon.Play"; ValueType: string; ValueData: "{cm:PlayFolder}"; Flags: uninsdeletekey; Tasks: folder
Root: HKA; Subkey: "Software\Classes\Directory\shell\Pylon.Play"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\{#AppExe},0"; Tasks: folder
Root: HKA; Subkey: "Software\Classes\Directory\shell\Pylon.Play\command"; ValueType: string; ValueData: """{app}\{#AppExe}"" ""%1"""; Tasks: folder

; Per-extension entries (OpenWithProgids + Capabilities\FileAssociations + Applications\SupportedTypes)
; keep in sync with MainWindow.AudioExt / VideoExt
Root: HKA; Subkey: "Software\Classes\.mp4\OpenWithProgids"; ValueType: none; ValueName: "Pylon.Video"; Flags: uninsdeletevalue; Tasks: assoc
Root: HKA; Subkey: "Software\Pylon\Capabilities\FileAssociations"; ValueType: string; ValueName: ".mp4"; ValueData: "Pylon.Video"
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".mp4"; ValueData: ""
Root: HKA; Subkey: "Software\Classes\.mkv\OpenWithProgids"; ValueType: none; ValueName: "Pylon.Video"; Flags: uninsdeletevalue; Tasks: assoc
Root: HKA; Subkey: "Software\Pylon\Capabilities\FileAssociations"; ValueType: string; ValueName: ".mkv"; ValueData: "Pylon.Video"
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".mkv"; ValueData: ""
Root: HKA; Subkey: "Software\Classes\.avi\OpenWithProgids"; ValueType: none; ValueName: "Pylon.Video"; Flags: uninsdeletevalue; Tasks: assoc
Root: HKA; Subkey: "Software\Pylon\Capabilities\FileAssociations"; ValueType: string; ValueName: ".avi"; ValueData: "Pylon.Video"
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".avi"; ValueData: ""
Root: HKA; Subkey: "Software\Classes\.mov\OpenWithProgids"; ValueType: none; ValueName: "Pylon.Video"; Flags: uninsdeletevalue; Tasks: assoc
Root: HKA; Subkey: "Software\Pylon\Capabilities\FileAssociations"; ValueType: string; ValueName: ".mov"; ValueData: "Pylon.Video"
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".mov"; ValueData: ""
Root: HKA; Subkey: "Software\Classes\.wmv\OpenWithProgids"; ValueType: none; ValueName: "Pylon.Video"; Flags: uninsdeletevalue; Tasks: assoc
Root: HKA; Subkey: "Software\Pylon\Capabilities\FileAssociations"; ValueType: string; ValueName: ".wmv"; ValueData: "Pylon.Video"
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".wmv"; ValueData: ""
Root: HKA; Subkey: "Software\Classes\.webm\OpenWithProgids"; ValueType: none; ValueName: "Pylon.Video"; Flags: uninsdeletevalue; Tasks: assoc
Root: HKA; Subkey: "Software\Pylon\Capabilities\FileAssociations"; ValueType: string; ValueName: ".webm"; ValueData: "Pylon.Video"
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".webm"; ValueData: ""
Root: HKA; Subkey: "Software\Classes\.flv\OpenWithProgids"; ValueType: none; ValueName: "Pylon.Video"; Flags: uninsdeletevalue; Tasks: assoc
Root: HKA; Subkey: "Software\Pylon\Capabilities\FileAssociations"; ValueType: string; ValueName: ".flv"; ValueData: "Pylon.Video"
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".flv"; ValueData: ""
Root: HKA; Subkey: "Software\Classes\.m4v\OpenWithProgids"; ValueType: none; ValueName: "Pylon.Video"; Flags: uninsdeletevalue; Tasks: assoc
Root: HKA; Subkey: "Software\Pylon\Capabilities\FileAssociations"; ValueType: string; ValueName: ".m4v"; ValueData: "Pylon.Video"
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".m4v"; ValueData: ""
Root: HKA; Subkey: "Software\Classes\.mpg\OpenWithProgids"; ValueType: none; ValueName: "Pylon.Video"; Flags: uninsdeletevalue; Tasks: assoc
Root: HKA; Subkey: "Software\Pylon\Capabilities\FileAssociations"; ValueType: string; ValueName: ".mpg"; ValueData: "Pylon.Video"
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".mpg"; ValueData: ""
Root: HKA; Subkey: "Software\Classes\.mpeg\OpenWithProgids"; ValueType: none; ValueName: "Pylon.Video"; Flags: uninsdeletevalue; Tasks: assoc
Root: HKA; Subkey: "Software\Pylon\Capabilities\FileAssociations"; ValueType: string; ValueName: ".mpeg"; ValueData: "Pylon.Video"
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".mpeg"; ValueData: ""
Root: HKA; Subkey: "Software\Classes\.ts\OpenWithProgids"; ValueType: none; ValueName: "Pylon.Video"; Flags: uninsdeletevalue; Tasks: assoc
Root: HKA; Subkey: "Software\Pylon\Capabilities\FileAssociations"; ValueType: string; ValueName: ".ts"; ValueData: "Pylon.Video"
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".ts"; ValueData: ""
Root: HKA; Subkey: "Software\Classes\.m2ts\OpenWithProgids"; ValueType: none; ValueName: "Pylon.Video"; Flags: uninsdeletevalue; Tasks: assoc
Root: HKA; Subkey: "Software\Pylon\Capabilities\FileAssociations"; ValueType: string; ValueName: ".m2ts"; ValueData: "Pylon.Video"
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".m2ts"; ValueData: ""
Root: HKA; Subkey: "Software\Classes\.mts\OpenWithProgids"; ValueType: none; ValueName: "Pylon.Video"; Flags: uninsdeletevalue; Tasks: assoc
Root: HKA; Subkey: "Software\Pylon\Capabilities\FileAssociations"; ValueType: string; ValueName: ".mts"; ValueData: "Pylon.Video"
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".mts"; ValueData: ""
Root: HKA; Subkey: "Software\Classes\.3gp\OpenWithProgids"; ValueType: none; ValueName: "Pylon.Video"; Flags: uninsdeletevalue; Tasks: assoc
Root: HKA; Subkey: "Software\Pylon\Capabilities\FileAssociations"; ValueType: string; ValueName: ".3gp"; ValueData: "Pylon.Video"
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".3gp"; ValueData: ""
Root: HKA; Subkey: "Software\Classes\.vob\OpenWithProgids"; ValueType: none; ValueName: "Pylon.Video"; Flags: uninsdeletevalue; Tasks: assoc
Root: HKA; Subkey: "Software\Pylon\Capabilities\FileAssociations"; ValueType: string; ValueName: ".vob"; ValueData: "Pylon.Video"
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".vob"; ValueData: ""
Root: HKA; Subkey: "Software\Classes\.ogv\OpenWithProgids"; ValueType: none; ValueName: "Pylon.Video"; Flags: uninsdeletevalue; Tasks: assoc
Root: HKA; Subkey: "Software\Pylon\Capabilities\FileAssociations"; ValueType: string; ValueName: ".ogv"; ValueData: "Pylon.Video"
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".ogv"; ValueData: ""
Root: HKA; Subkey: "Software\Classes\.divx\OpenWithProgids"; ValueType: none; ValueName: "Pylon.Video"; Flags: uninsdeletevalue; Tasks: assoc
Root: HKA; Subkey: "Software\Pylon\Capabilities\FileAssociations"; ValueType: string; ValueName: ".divx"; ValueData: "Pylon.Video"
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".divx"; ValueData: ""
Root: HKA; Subkey: "Software\Classes\.rmvb\OpenWithProgids"; ValueType: none; ValueName: "Pylon.Video"; Flags: uninsdeletevalue; Tasks: assoc
Root: HKA; Subkey: "Software\Pylon\Capabilities\FileAssociations"; ValueType: string; ValueName: ".rmvb"; ValueData: "Pylon.Video"
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".rmvb"; ValueData: ""
Root: HKA; Subkey: "Software\Classes\.mp3\OpenWithProgids"; ValueType: none; ValueName: "Pylon.Audio"; Flags: uninsdeletevalue; Tasks: assoc
Root: HKA; Subkey: "Software\Pylon\Capabilities\FileAssociations"; ValueType: string; ValueName: ".mp3"; ValueData: "Pylon.Audio"
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".mp3"; ValueData: ""
Root: HKA; Subkey: "Software\Classes\.flac\OpenWithProgids"; ValueType: none; ValueName: "Pylon.Audio"; Flags: uninsdeletevalue; Tasks: assoc
Root: HKA; Subkey: "Software\Pylon\Capabilities\FileAssociations"; ValueType: string; ValueName: ".flac"; ValueData: "Pylon.Audio"
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".flac"; ValueData: ""
Root: HKA; Subkey: "Software\Classes\.wav\OpenWithProgids"; ValueType: none; ValueName: "Pylon.Audio"; Flags: uninsdeletevalue; Tasks: assoc
Root: HKA; Subkey: "Software\Pylon\Capabilities\FileAssociations"; ValueType: string; ValueName: ".wav"; ValueData: "Pylon.Audio"
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".wav"; ValueData: ""
Root: HKA; Subkey: "Software\Classes\.ogg\OpenWithProgids"; ValueType: none; ValueName: "Pylon.Audio"; Flags: uninsdeletevalue; Tasks: assoc
Root: HKA; Subkey: "Software\Pylon\Capabilities\FileAssociations"; ValueType: string; ValueName: ".ogg"; ValueData: "Pylon.Audio"
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".ogg"; ValueData: ""
Root: HKA; Subkey: "Software\Classes\.opus\OpenWithProgids"; ValueType: none; ValueName: "Pylon.Audio"; Flags: uninsdeletevalue; Tasks: assoc
Root: HKA; Subkey: "Software\Pylon\Capabilities\FileAssociations"; ValueType: string; ValueName: ".opus"; ValueData: "Pylon.Audio"
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".opus"; ValueData: ""
Root: HKA; Subkey: "Software\Classes\.m4a\OpenWithProgids"; ValueType: none; ValueName: "Pylon.Audio"; Flags: uninsdeletevalue; Tasks: assoc
Root: HKA; Subkey: "Software\Pylon\Capabilities\FileAssociations"; ValueType: string; ValueName: ".m4a"; ValueData: "Pylon.Audio"
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".m4a"; ValueData: ""
Root: HKA; Subkey: "Software\Classes\.aac\OpenWithProgids"; ValueType: none; ValueName: "Pylon.Audio"; Flags: uninsdeletevalue; Tasks: assoc
Root: HKA; Subkey: "Software\Pylon\Capabilities\FileAssociations"; ValueType: string; ValueName: ".aac"; ValueData: "Pylon.Audio"
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".aac"; ValueData: ""
Root: HKA; Subkey: "Software\Classes\.wma\OpenWithProgids"; ValueType: none; ValueName: "Pylon.Audio"; Flags: uninsdeletevalue; Tasks: assoc
Root: HKA; Subkey: "Software\Pylon\Capabilities\FileAssociations"; ValueType: string; ValueName: ".wma"; ValueData: "Pylon.Audio"
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".wma"; ValueData: ""
Root: HKA; Subkey: "Software\Classes\.ape\OpenWithProgids"; ValueType: none; ValueName: "Pylon.Audio"; Flags: uninsdeletevalue; Tasks: assoc
Root: HKA; Subkey: "Software\Pylon\Capabilities\FileAssociations"; ValueType: string; ValueName: ".ape"; ValueData: "Pylon.Audio"
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".ape"; ValueData: ""
Root: HKA; Subkey: "Software\Classes\.aiff\OpenWithProgids"; ValueType: none; ValueName: "Pylon.Audio"; Flags: uninsdeletevalue; Tasks: assoc
Root: HKA; Subkey: "Software\Pylon\Capabilities\FileAssociations"; ValueType: string; ValueName: ".aiff"; ValueData: "Pylon.Audio"
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".aiff"; ValueData: ""
Root: HKA; Subkey: "Software\Classes\.aif\OpenWithProgids"; ValueType: none; ValueName: "Pylon.Audio"; Flags: uninsdeletevalue; Tasks: assoc
Root: HKA; Subkey: "Software\Pylon\Capabilities\FileAssociations"; ValueType: string; ValueName: ".aif"; ValueData: "Pylon.Audio"
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".aif"; ValueData: ""
Root: HKA; Subkey: "Software\Classes\.mka\OpenWithProgids"; ValueType: none; ValueName: "Pylon.Audio"; Flags: uninsdeletevalue; Tasks: assoc
Root: HKA; Subkey: "Software\Pylon\Capabilities\FileAssociations"; ValueType: string; ValueName: ".mka"; ValueData: "Pylon.Audio"
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".mka"; ValueData: ""
Root: HKA; Subkey: "Software\Classes\.mpc\OpenWithProgids"; ValueType: none; ValueName: "Pylon.Audio"; Flags: uninsdeletevalue; Tasks: assoc
Root: HKA; Subkey: "Software\Pylon\Capabilities\FileAssociations"; ValueType: string; ValueName: ".mpc"; ValueData: "Pylon.Audio"
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".mpc"; ValueData: ""
Root: HKA; Subkey: "Software\Classes\.wv\OpenWithProgids"; ValueType: none; ValueName: "Pylon.Audio"; Flags: uninsdeletevalue; Tasks: assoc
Root: HKA; Subkey: "Software\Pylon\Capabilities\FileAssociations"; ValueType: string; ValueName: ".wv"; ValueData: "Pylon.Audio"
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".wv"; ValueData: ""

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,Pylon}"; Flags: nowait postinstall skipifsilent
; in-app updates run the setup with /SILENT: start the new version afterwards
Filename: "{app}\{#AppExe}"; Flags: nowait; Check: WizardSilent
Filename: "ms-settings:defaultapps?registeredAppUser=Pylon"; Description: "{cm:OpenDefaults}"; Flags: shellexec postinstall skipifsilent unchecked nowait; Tasks: assoc
