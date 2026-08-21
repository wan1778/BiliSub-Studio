#ifndef AppVersion
  #define AppVersion "4.0.0-beta.12-csharp-p5"
#endif
#ifndef PublishDir
  #error PublishDir must point to the verified WinUI publish directory
#endif
#ifndef OutputDir
  #error OutputDir must point to the candidate artifact directory
#endif
#ifndef OutputBaseFilename
  #define OutputBaseFilename "BiliSubStudio_Setup_v4.0.0-beta.12-csharp-p5_x64"
#endif

[Setup]
AppId={{D2393B27-9CB7-4D76-8E65-6A6BD0EC729D}
AppName=BiliSub Studio
AppVersion={#AppVersion}
AppVerName=BiliSub Studio {#AppVersion}
AppPublisher=BiliSub Studio
VersionInfoVersion=4.0.0.12
VersionInfoTextVersion={#AppVersion}
DefaultDirName={localappdata}\Programs\BiliSub Studio
DefaultGroupName=BiliSub Studio
UninstallDisplayName=BiliSub Studio
UninstallDisplayIcon={app}\BiliSubStudio.exe
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
SetupArchitecture=x64
MinVersion=10.0.17763
OutputDir={#OutputDir}
OutputBaseFilename={#OutputBaseFilename}
SetupIconFile=..\src\BiliSubStudio.App\Assets\BiliSubStudio.ico
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
DisableProgramGroupPage=auto
AllowNoIcons=yes
UsePreviousAppDir=yes
CloseApplications=yes
RestartApplications=no
SetupLogging=yes
Uninstallable=yes

[Tasks]
Name: "desktopicon"; Description: "Tạo lối tắt trên màn hình"; GroupDescription: "Lối tắt:"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs notimestamp

[Dirs]
Name: "{app}\Data"; Flags: uninsneveruninstall
Name: "{app}\Tools"; Flags: uninsneveruninstall
Name: "{app}\Temp"; Flags: uninsneveruninstall
Name: "{app}\Cache"; Flags: uninsneveruninstall
Name: "{app}\Downloads"; Flags: uninsneveruninstall

[Icons]
Name: "{group}\BiliSub Studio"; Filename: "{app}\BiliSubStudio.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\BiliSub Studio"; Filename: "{app}\BiliSubStudio.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\BiliSubStudio.exe"; Description: "Mở BiliSub Studio"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent
