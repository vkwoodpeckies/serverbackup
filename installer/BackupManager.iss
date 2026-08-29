[Setup]
AppName=Backup Manager
AppVersion=1.0.20
DefaultDirName={autopf}\Backup Manager
OutputBaseFilename=BackupManagerSetup-1.0.20
PrivilegesRequired=admin
[Files]
Source: "..\publish\desktop\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion
Source: "..\publish\service\*"; DestDir: "{app}\service"; Flags: recursesubdirs ignoreversion
[Run]
Filename: "sc.exe"; Parameters: "create ""Backup Manager Service"" binPath= ""{app}\service\BackupManager.Service.exe"" start= auto"; Flags: runhidden
Filename: "sc.exe"; Parameters: "start ""Backup Manager Service"""; Flags: runhidden
Filename: "{app}\BackupManager.Desktop.exe"; Description: "Launch Backup Manager"; Flags: nowait postinstall skipifsilent
[Icons]
Name: "{autoprograms}\Backup Manager"; Filename: "{app}\BackupManager.Desktop.exe"
Name: "{autodesktop}\Backup Manager"; Filename: "{app}\BackupManager.Desktop.exe"
Name: "{commonstartup}\Backup Manager"; Filename: "{app}\BackupManager.Desktop.exe"
[Code]
function PrepareToInstall(var NeedsRestart: Boolean): String;
var ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\sc.exe'), 'stop "Backup Manager Service"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}\sc.exe'), 'delete "Backup Manager Service"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := '';
end;
