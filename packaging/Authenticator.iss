; Authenticator Setup — elevated Inno installer with SQL Server Express bootstrap.
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
  { SQL Server 2022 Express Core offline media (~280 MB). Prefer this over SSEI web links (they 404 / drift). }
  SqlExpressDownloadUrl =
    'https://download.microsoft.com/download/3/8/d/38de7036-2433-4207-8eae-06e247e17b25/SQLEXPR_x64_ENU.exe';
  SqlExpressPackage = 'SQLEXPR_x64_ENU.exe';

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

{ True when any SQL Server engine instance is installed (default or named) or its service exists. }
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

  { Services still count when Instance Names is missing/incomplete. }
  if SqlServiceInstalled('MSSQLSERVER')
     or SqlServiceInstalled('MSSQL$SQLEXPRESS') then
    Result := True;
end;

{ Prefer SQLEXPRESS, else first named instance, else default instance "." }
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

function WriteConnectionJson: Boolean;
var
  Dir, Path, Json, Server: String;
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

  Server := ResolveSqlServerForConfig;
  Json :=
    '{' + #13#10 +
    '  "Server": "' + Server + '",' + #13#10 +
    '  "Database": "Authenticator"' + #13#10 +
    '}';
  Result := SaveStringToFile(Path, Json, False);
  if Result then
    Log('Wrote ' + Path + ' (Server=' + Server + ')')
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
      'https://www.microsoft.com/en-us/sql-server/sql-server-downloads' + #13#10 + #13#10 +
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
    '/SQLSYSADMINACCOUNTS="Builtin\Administrators" ' +
    '/AGTSVCACCOUNT="NT AUTHORITY\NETWORK SERVICE" /SQLSVCACCOUNT="NT AUTHORITY\NETWORK SERVICE"';

  if not Exec(SetupExe, Args, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    MsgBox('Failed to start SQL Server Express setup.exe.', mbError, MB_OK);
    Exit;
  end;

  { 0 = success; 3010 = success, reboot required }
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

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  NeedsRestart := False;

  if AnySqlServerPresent then
  begin
    Log('SQL Server already installed/present (instance or service); skipping Express download/install.');
    Exit;
  end;

  Log('No local SQL Server found; downloading and installing SQL Server Express (SQLEXPRESS).');
  if not InstallSqlExpress then
    Result := 'SQL Server Express (SQLEXPRESS) is required and could not be installed.';
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    if not WriteConnectionJson then
      MsgBox(
        'App files were installed, but connection.json could not be written under LocalAppData.' + #13#10 +
        'Create %LocalAppData%\Authenticator\connection.json pointing at .\SQLEXPRESS.',
        mbError,
        MB_OK);
  end;
end;
