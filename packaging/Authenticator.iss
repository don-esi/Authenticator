; Authenticator Setup — elevated Inno installer with SQL Server Express bootstrap.
; Compile via packaging\build-setup.ps1 (requires Inno Setup 6.4+ for DownloadTemporaryFile).

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
    'https://download.microsoft.com/download/3/8/d/38de7036-2433-4207-8e93-f960bfd23c49/SQL2022-SSEI-Expr.exe';
  SqlExpressBootstrapper = 'SQL2022-SSEI-Expr.exe';

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
end;

function WriteConnectionJson: Boolean;
var
  Dir, Path, Json: String;
begin
  Dir := ExpandConstant('{localappdata}\Authenticator');
  if not ForceDirectories(Dir) then
  begin
    Log('Failed to create ' + Dir);
    Result := False;
    Exit;
  end;

  Path := Dir + '\connection.json';
  Json :=
    '{' + #13#10 +
    '  "Server": ".\\SQLEXPRESS",' + #13#10 +
    '  "Database": "Authenticator"' + #13#10 +
    '}';
  Result := SaveStringToFile(Path, Json, False);
  if Result then
    Log('Wrote ' + Path)
  else
    Log('Failed to write ' + Path);
end;

function InstallSqlExpress: Boolean;
var
  TmpPath: String;
  ResultCode: Integer;
  Args: String;
begin
  Result := False;
  TmpPath := ExpandConstant('{tmp}\' + SqlExpressBootstrapper);

  WizardForm.StatusLabel.Caption := 'Downloading SQL Server Express…';
  try
    DownloadTemporaryFile(SqlExpressDownloadUrl, SqlExpressBootstrapper, '', nil);
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

  { SQL2022-SSEI-Expr.exe is the Express bootstrapper; it downloads engine media then installs. }
  WizardForm.StatusLabel.Caption := 'Installing SQL Server Express (SQLEXPRESS)…';
  Args :=
    '/ACTION=Install /FEATURES=SQL /INSTANCENAME=SQLEXPRESS ' +
    '/IACCEPTSQLSERVERLICENSETERMS /QUIET /HIDEPROGRESSBAR=1 ' +
    '/SQLSYSADMINACCOUNTS="Builtin\Administrators"';

  if not Exec(TmpPath, Args, '', SW_SHOW, ewWaitUntilTerminated, ResultCode) then
  begin
    MsgBox('Failed to start SQL Server Express installer.', mbError, MB_OK);
    Exit;
  end;

  if (ResultCode <> 0) and (not SqlExpressInstanceExists) then
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

  if SqlExpressInstanceExists then
  begin
    Log('SQLEXPRESS instance already present; skipping Express install.');
    Exit;
  end;

  Log('SQLEXPRESS not found; installing SQL Server Express.');
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
