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
UninstallDisplayIcon={app}\Runtime\BiliSubStudio.exe
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
DisableProgramGroupPage=yes
DisableDirPage=no
AppendDefaultDirName=yes
AllowNoIcons=no
UsePreviousAppDir=yes
CloseApplications=yes
RestartApplications=no
SetupLogging=yes
Uninstallable=yes

[Tasks]
Name: "desktopicon"; Description: "Tạo lối tắt trên màn hình"; GroupDescription: "Lối tắt:"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}\Runtime"; Flags: ignoreversion recursesubdirs createallsubdirs notimestamp

[Dirs]
Name: "{app}\Data"; Flags: uninsneveruninstall
Name: "{app}\Tools"; Flags: uninsneveruninstall
Name: "{app}\Temp"; Flags: uninsneveruninstall
Name: "{app}\Cache"; Flags: uninsneveruninstall
Name: "{app}\Downloads"; Flags: uninsneveruninstall

[Icons]
Name: "{group}\BiliSub Studio"; Filename: "{app}\Runtime\BiliSubStudio.exe"; WorkingDir: "{app}\Runtime"
Name: "{autodesktop}\BiliSub Studio"; Filename: "{app}\Runtime\BiliSubStudio.exe"; WorkingDir: "{app}\Runtime"; Tasks: desktopicon

[Run]
Filename: "{app}\Runtime\BiliSubStudio.exe"; Description: "Mở BiliSub Studio"; WorkingDir: "{app}\Runtime"; Flags: nowait postinstall skipifsilent

[Code]
function IsProtectedRootName(const Name: String): Boolean;
begin
  Result :=
    (CompareText(Name, 'Data') = 0) or
    (CompareText(Name, 'Tools') = 0) or
    (CompareText(Name, 'Temp') = 0) or
    (CompareText(Name, 'Cache') = 0) or
    (CompareText(Name, 'Downloads') = 0) or
    (CompareText(Name, 'Runtime') = 0);
end;

function FirstPathComponent(const RelativePath: String): String;
var
  P: Integer;
begin
  P := Pos('\', RelativePath);
  if P > 0 then
    Result := Copy(RelativePath, 1, P - 1)
  else
    Result := RelativePath;
end;

function IsSafeLegacyRuntimePath(const RelativePath: String): Boolean;
begin
  Result :=
    (RelativePath <> '') and
    (RelativePath[1] <> '\') and
    (Pos(':', RelativePath) = 0) and
    (Pos('..\', RelativePath) = 0) and
    (Pos('\..', RelativePath) = 0) and
    (not IsProtectedRootName(FirstPathComponent(RelativePath)));
end;

procedure CleanupLegacyFlatRuntime;
var
  Lines: TArrayOfString;
  I, Pass: Integer;
  Line, RelativePath, DirectoryPath, Target: String;
begin
  { Migration is intentionally checksum-owned: remove only files that the old
    verified publish declared as runtime. Unknown/user files in {app} survive. }
  if not FileExists(ExpandConstant('{app}\BiliSubStudio.exe')) then
    Exit;
  if not FileExists(ExpandConstant('{app}\SHA256SUMS.txt')) then
    Exit;
  if not LoadStringsFromFile(ExpandConstant('{app}\SHA256SUMS.txt'), Lines) then
    Exit;

  Log('Migrating legacy flat BiliSub Studio runtime into Runtime\');
  for I := 0 to GetArrayLength(Lines) - 1 do
  begin
    Line := Lines[I];
    if Length(Line) < 67 then
      Continue;
    RelativePath := Copy(Line, 67, MaxInt);
    StringChangeEx(RelativePath, '/', '\', True);
    if not IsSafeLegacyRuntimePath(RelativePath) then
      Continue;
    Target := ExpandConstant('{app}\') + RelativePath;
    if FileExists(Target) and (not DeleteFile(Target)) then
      Log('Could not delete legacy runtime file: ' + Target);
  end;

  { All runtime files are gone first. Repeated empty-directory passes then
    collapse locale/Assets/Pages trees without touching protected/user roots. }
  for Pass := 1 to 12 do
  begin
    for I := 0 to GetArrayLength(Lines) - 1 do
    begin
      Line := Lines[I];
      if Length(Line) < 67 then
        Continue;
      RelativePath := Copy(Line, 67, MaxInt);
      StringChangeEx(RelativePath, '/', '\', True);
      if not IsSafeLegacyRuntimePath(RelativePath) then
        Continue;
      DirectoryPath := ExtractFileDir(RelativePath);
      while DirectoryPath <> '' do
      begin
        RemoveDir(ExpandConstant('{app}\') + DirectoryPath);
        DirectoryPath := ExtractFileDir(DirectoryPath);
      end;
    end;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
    CleanupLegacyFlatRuntime;
end;
