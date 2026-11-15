; Authenticator Setup — elevated Inno installer with SQL Server Express bootstrap (Host) or LAN Join.
; Compile via packaging\build-setup.ps1 (requires Inno Setup 6.4+ / 7 for DownloadTemporaryFile).

#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif

#define MyAppName "Authenticator"
#define MyAppPublisher "Authenticator"
#define MyAppExeName "Authenticator.Wpf.exe"
#define MyAppId "{{A8E3C5F1-9B42-4D6E-8F1A-2C7D9E0B4A31}"

[Setup]
AppId={#MyAppId}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=dist
OutputBaseFilename=AuthenticatorSetup-{#MyAppVersion}
SetupIconFile=..\Authenticator.Wpf\Assets\app.ico
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\{#MyAppExeName}
CloseApplications=yes
RestartIfNeededByRun=no
MinVersion=10.0.19041

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent

[Code]
const
  SqlExpressDownloadUrl =
    'https://download.microsoft.com/download/3/8/d/38de7036-2433-4207-8eae-06e247e17b25/SQLEXPR_x64_ENU.exe';
  SqlExpressPackage = 'SQLEXPR_x64_ENU.exe';

var
  RolePage: TInputOptionWizardPage;
  InstallRole: Integer; { 0 = Host, 1 = Join, 2 = Local only }
  SharedServerOverride: String;

function RegGetSqlInstanceNames(RootKey: Integer; var Names: TArrayOfString): Boolean;
begin
  Result := RegGetValueNames(
    RootKey,
    'SOFTWARE\Microsoft\Microsoft SQL Server\Instance Names\SQL',
    Names);
end;

function SqlServiceInstalled(const ServiceName: String): Boolean;
begin
  Result :=
    RegKeyExists(HKLM64, 'SYSTEM\CurrentControlSet\Services\' + ServiceName)
    or RegKeyExists(HKLM32, 'SYSTEM\CurrentControlSet\Services\' + ServiceName);
end;

function SqlExpressInstanceExists: Boolean;
var
  InstanceId: String;
begin
  Result := RegQueryStringValue(
    HKLM64,
    'SOFTWARE\Microsoft\Microsoft SQL Server\Instance Names\SQL',
    'SQLEXPRESS',
    InstanceId);
  if not Result then
    Result := RegQueryStringValue(
      HKLM32,
      'SOFTWARE\Microsoft\Microsoft SQL Server\Instance Names\SQL',
      'SQLEXPRESS',
      InstanceId);
  if not Result then
    Result := SqlServiceInstalled('MSSQL$SQLEXPRESS');
end;

function AnySqlServerPresent: Boolean;
var
  Names: TArrayOfString;
begin
  Result := False;
  if RegGetSqlInstanceNames(HKLM64, Names) and (GetArrayLength(Names) > 0) then
  begin
    Result := True;
    Exit;
  end;
  if RegGetSqlInstanceNames(HKLM32, Names) and (GetArrayLength(Names) > 0) then
  begin
    Result := True;
    Exit;
  end;
  if SqlServiceInstalled('MSSQLSERVER') or SqlServiceInstalled('MSSQL$SQLEXPRESS') then
    Result := True;
end;

function ResolveSqlServerForConfig: String;
var
  Names: TArrayOfString;
  I: Integer;
  Name: String;
begin
  if SqlExpressInstanceExists then
  begin
    Result := '.\\SQLEXPRESS';
    Exit;
  end;

  SetArrayLength(Names, 0);
  if not RegGetSqlInstanceNames(HKLM64, Names) or (GetArrayLength(Names) = 0) then
    RegGetSqlInstanceNames(HKLM32, Names);

  for I := 0 to GetArrayLength(Names) - 1 do
  begin
    Name := Names[I];
    if CompareText(Name, 'MSSQLSERVER') = 0 then
    begin
      Result := '.';
      Exit;
    end;
  end;

  for I := 0 to GetArrayLength(Names) - 1 do
  begin
    Name := Names[I];
    if CompareText(Name, 'MSSQLSERVER') <> 0 then
    begin
      Result := '.\\' + Name;
      Exit;
    end;
  end;

  if SqlServiceInstalled('MSSQLSERVER') then
  begin
    Result := '.';
    Exit;
  end;

  Result := '.\\SQLEXPRESS';
end;

function ComputerHostName: String;
var
  N: String;
begin
  N := GetComputerNameString;
  if N = '' then
    N := 'localhost';
  Result := N;
end;

function SharedServerForHost: String;
begin
  Result := ComputerHostName + '\\SQLEXPRESS';
end;

function JsonEscape(const S: String): String;
begin
  Result := S;
  StringChangeEx(Result, '\', '\\', True);
  StringChangeEx(Result, '"', '\"', True);
end;

function WriteConnectionJson: Boolean;
var
  Dir, Path, Json, LocalServer, SharedServer, RoleName, Active, Discovered: String;
begin
  Dir := ExpandConstant('{localappdata}\Authenticator');
  if not ForceDirectories(Dir) then
  begin
    Log('Failed to create ' + Dir);
    Result := False;
    Exit;
  end;

  Path := Dir + '\connection.json';
  if FileExists(Path) then
  begin
    Log('Leaving existing connection.json in place: ' + Path);
    Result := True;
    Exit;
  end;

  LocalServer := ResolveSqlServerForConfig;
  if SharedServerOverride <> '' then
    SharedServer := SharedServerOverride
  else if InstallRole = 0 then
    SharedServer := SharedServerForHost
  else if InstallRole = 1 then
    SharedServer := SharedServerForHost { placeholder; app discovery will replace }
  else
    SharedServer := LocalServer;

  if InstallRole = 0 then
  begin
    RoleName := 'Host';
    Active := 'Shared';
  end
  else if InstallRole = 1 then
  begin
    RoleName := 'Join';
    Active := 'Shared';
  end
  else
  begin
    RoleName := 'Local';
    Active := 'Local';
  end;

  if InstallRole = 1 then
    Discovered := 'true'
  else
    Discovered := 'false';

  Json :=
    '{' + #13#10 +
    '  "activeProfile": "' + Active + '",' + #13#10 +
    '  "role": "' + RoleName + '",' + #13#10 +
    '  "profiles": [' + #13#10 +
    '    { "name": "Local", "server": "' + JsonEscape(LocalServer) + '", "database": "Authenticator" },' + #13#10 +
    '    { "name": "Shared", "server": "' + JsonEscape(SharedServer) + '", "database": "Authenticator", "discovered": ' +
      Discovered + ' }' + #13#10 +
    '  ],' + #13#10 +
    '  "discovery": { "udpPort": 38455 },' + #13#10 +
    '  "Server": "' + JsonEscape(SharedServer) + '",' + #13#10 +
    '  "Database": "Authenticator"' + #13#10 +
    '}';
  Result := SaveStringToFile(Path, Json, False);
  if Result then
    Log('Wrote ' + Path + ' (role=' + RoleName + ', Shared=' + SharedServer + ')')
  else
    Log('Failed to write ' + Path);
end;

function InstallSqlExpress: Boolean;
var
  TmpPath, ExtractDir, SetupExe: String;
  ResultCode: Integer;
  Args: String;
begin
  Result := False;
  TmpPath := ExpandConstant('{tmp}\' + SqlExpressPackage);
  ExtractDir := ExpandConstant('{tmp}\SQLExpressMedia');
  SetupExe := ExtractDir + '\SETUP.exe';

  WizardForm.StatusLabel.Caption := 'Downloading SQL Server Express (~280 MB)…';
  try
    DownloadTemporaryFile(SqlExpressDownloadUrl, SqlExpressPackage, '', nil);
  except
    MsgBox(
      'Could not download SQL Server Express.' + #13#10 + #13#10 +
      'Install SQL Server Express (instance name SQLEXPRESS) manually, then re-run Setup.' + #13#10 + #13#10 +
      GetExceptionMessage,
      mbError,
      MB_OK);
    Exit;
  end;

  if not FileExists(TmpPath) then
  begin
    MsgBox('SQL Server Express download did not produce ' + TmpPath, mbError, MB_OK);
    Exit;
  end;

  ForceDirectories(ExtractDir);
  WizardForm.StatusLabel.Caption := 'Extracting SQL Server Express media…';
  if not Exec(TmpPath, '/Q /x:"' + ExtractDir + '"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    MsgBox('Failed to extract SQL Server Express media.', mbError, MB_OK);
    Exit;
  end;

  if (ResultCode <> 0) or (not FileExists(SetupExe)) then
  begin
    MsgBox(
      'SQL Server Express media extract failed (exit code ' + IntToStr(ResultCode) + ').',
      mbError,
      MB_OK);
    Exit;
  end;

  WizardForm.StatusLabel.Caption := 'Installing SQL Server Express (SQLEXPRESS)…';
  Args :=
    '/Q /ACTION=Install /FEATURES=SQLENGINE /INSTANCENAME=SQLEXPRESS /INSTANCEID=SQLEXPRESS ' +
    '/IACCEPTSQLSERVERLICENSETERMS /UPDATEENABLED=0 /UpdateEnabled=0 ' +
    '/TCPENABLED=1 /NPENABLED=1 ' +
    '/SQLSYSADMINACCOUNTS="Builtin\Administrators" ' +
    '/AGTSVCACCOUNT="NT AUTHORITY\NETWORK SERVICE" /SQLSVCACCOUNT="NT AUTHORITY\NETWORK SERVICE"';

  if not Exec(SetupExe, Args, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    MsgBox('Failed to start SQL Server Express setup.exe.', mbError, MB_OK);
    Exit;
  end;

  if (ResultCode <> 0) and (ResultCode <> 3010) and (not SqlExpressInstanceExists) then
  begin
    MsgBox(
      'SQL Server Express install failed (exit code ' + IntToStr(ResultCode) + ').' + #13#10 +
      'Install Express with instance name SQLEXPRESS, then re-run Setup.',
      mbError,
      MB_OK);
    Exit;
  end;

  if not SqlExpressInstanceExists then
  begin
    MsgBox(
      'SQL Server Express finished but the SQLEXPRESS instance was not found.' + #13#10 +
      'Reboot and re-run Setup, or install Express manually.',
      mbError,
      MB_OK);
    Exit;
  end;

  Result := True;
end;

function EnableSqlFirewallRules: Boolean;
var
  ResultCode: Integer;
begin
  { Best-effort firewall openings for shared host. }
  Exec('netsh', 'advfirewall firewall add rule name="Authenticator SQL TCP 1433" dir=in action=allow protocol=TCP localport=1433', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec('netsh', 'advfirewall firewall add rule name="Authenticator SQL Browser 1434" dir=in action=allow protocol=UDP localport=1434', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec('netsh', 'advfirewall firewall add rule name="Authenticator SQL Discovery 38455" dir=in action=allow protocol=UDP localport=38455', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := True;
end;

procedure InitializeWizard;
begin
  InstallRole := 0;
  SharedServerOverride := '';
  RolePage := CreateInputOptionPage(
    wpSelectTasks,
    'Shared vault role',
    'How should this PC use SQL Express?',
    'Host installs/configures a shared SQL Express instance other machines can discover. Join skips Express and finds a host on the LAN (or enter a server in Settings). Local-only keeps a private Express instance.',
    True,
    False);
  RolePage.Add('Host shared vault on this PC (install/configure SQL Express)');
  RolePage.Add('Join existing shared vault on the LAN (skip SQL Express install)');
  RolePage.Add('Local only (this PC; classic single-machine)');
  RolePage.Values[0] := True;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if CurPageID = RolePage.ID then
  begin
    if RolePage.Values[0] then InstallRole := 0
    else if RolePage.Values[1] then InstallRole := 1
    else InstallRole := 2;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  NeedsRestart := False;

  { Join role: never install Express. }
  if InstallRole = 1 then
  begin
    Log('Join role selected; skipping SQL Express download/install. Discovery happens in-app.');
    Exit;
  end;

  if AnySqlServerPresent then
  begin
    Log('SQL Server already installed/present; skipping Express download/install.');
    if InstallRole = 0 then
      EnableSqlFirewallRules;
    Exit;
  end;

  if InstallRole = 2 then
    Log('Local-only role: installing SQL Express if missing.');
  else
    Log('Host role: downloading and installing SQL Server Express (SQLEXPRESS).');

  if not InstallSqlExpress then
  begin
    Result := 'SQL Server Express (SQLEXPRESS) is required for this role and could not be installed.';
    Exit;
  end;

  if InstallRole = 0 then
    EnableSqlFirewallRules;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    if not WriteConnectionJson then
      MsgBox(
        'App files were installed, but connection.json could not be written under LocalAppData.' + #13#10 +
        'Create %LocalAppData%\Authenticator\connection.json or use Settings → Connection.',
        mbError,
        MB_OK);
  end;
end;
