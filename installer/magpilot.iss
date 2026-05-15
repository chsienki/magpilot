; Magpilot Inno Setup installer
;
; Per-machine install (PrivilegesRequired=admin) under %ProgramFiles%\Magpilot.
; Ships two components:
;   - Magpilot Agent  (self-contained .NET 9 publish under {app}\agent\)
;   - Magpilot Launcher (self-contained .NET 9 publish under {app}\bin\)
;
; Optional install Tasks:
;   - addtopath:  append {app}\bin to system PATH (Inno's setoldata replace)
;   - schedtask:  register MagpilotAgent scheduled task at user logon
;   - firewall:   inbound rules for TCP 5099 + UDP 47823
;
; A custom Settings page collects MAGPILOT_HUB_URL, MAGPILOT_AGENT_TOKEN,
; and MAGPILOT_AGENT_PUBLIC_URL, then writes them to {app}\config\magpilot.env.
; On upgrade, existing values are pre-populated from that file so silent
; re-installs (e.g. magpilot --magpilot-update) preserve them.
;
; Build via CI (release.yml) or locally with:
;   iscc /DAppVersion=0.1.0 /DPublishDir=publish installer\magpilot.iss

#ifndef AppVersion
  #define AppVersion "0.0.0-dev"
#endif
#ifndef PublishDir
  #define PublishDir "publish"
#endif

#define MyAppName "Magpilot"
#define MyAppPublisher "chsienki"
#define MyAppURL "https://github.com/chsienki/magpilot"

[Setup]
AppId={{A2D8E5C0-9F4B-4F3A-9E2D-1C8B7F4A6E3D}
AppName={#MyAppName}
AppVersion={#AppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
DefaultDirName={commonpf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputBaseFilename=magpilot-setup-{#AppVersion}
OutputDir=installer-output
Compression=lzma2
SolidCompression=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=force
RestartApplications=no
ChangesEnvironment=yes
UninstallDisplayName={#MyAppName} {#AppVersion}

[Components]
Name: "agent"; Description: "Magpilot Agent (per-host daemon)"; Types: full custom; Flags: checkablealone
Name: "launcher"; Description: "Magpilot Launcher (the `magpilot` command)"; Types: full custom; Flags: checkablealone

[Tasks]
Name: "addtopath"; Description: "Add Magpilot to system PATH"; GroupDescription: "Setup options:"; Components: launcher
Name: "schedtask"; Description: "Run the agent at user logon"; GroupDescription: "Setup options:"; Components: agent
Name: "firewall"; Description: "Open Windows Firewall for LAN (TCP 5099, UDP 47823)"; GroupDescription: "Setup options:"; Components: agent

[Files]
; Self-contained net9.0 publish of Magpilot.Agent (includes runtime + Magpilot.Agent.exe).
Source: "{#PublishDir}\agent\*"; DestDir: "{app}\agent"; Components: agent; Flags: ignoreversion recursesubdirs createallsubdirs
; Self-contained net9.0 publish of Magpilot.Host (the magpilot.exe launcher + Pty.Net natives).
Source: "{#PublishDir}\bin\*"; DestDir: "{app}\bin"; Components: launcher; Flags: ignoreversion recursesubdirs createallsubdirs
; Helper PowerShell scripts (always copied so uninstall can run them).
Source: "install-task.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "uninstall-task.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "firewall.ps1"; DestDir: "{app}"; Flags: ignoreversion

[Dirs]
Name: "{app}\config"; Permissions: users-modify

[Registry]
; Append {app}\bin to the system PATH if the addtopath task is selected
; AND the path isn't already there. {olddata} is the existing PATH value.
Root: HKLM; Subkey: "SYSTEM\CurrentControlSet\Control\Session Manager\Environment"; \
    ValueType: expandsz; ValueName: "Path"; ValueData: "{olddata};{app}\bin"; \
    Tasks: addtopath; Check: NeedsAddPath(ExpandConstant('{app}\bin'))

[Code]
const
  EnvFileName = 'magpilot.env';

function NeedsAddPath(NewPath: String): Boolean;
var
  CurrentPath: String;
begin
  if not RegQueryStringValue(HKEY_LOCAL_MACHINE,
       'SYSTEM\CurrentControlSet\Control\Session Manager\Environment', 'Path', CurrentPath) then
  begin
    Result := True;
    exit;
  end;
  // Look for ';NewPath;' in ';CurrentPath;'. Case-insensitive.
  Result := Pos(';' + Uppercase(NewPath) + ';', ';' + Uppercase(CurrentPath) + ';') = 0;
end;

function ReadEnvKey(FilePath, Key: String): String;
var
  Lines: TArrayOfString;
  i: Integer;
  Line, Prefix: String;
begin
  Result := '';
  if not LoadStringsFromFile(FilePath, Lines) then exit;
  Prefix := Key + '=';
  for i := 0 to GetArrayLength(Lines) - 1 do
  begin
    Line := Trim(Lines[i]);
    if (Length(Line) = 0) or (Line[1] = '#') then continue;
    if Pos(Prefix, Line) = 1 then
    begin
      Result := Copy(Line, Length(Prefix) + 1, Length(Line));
      exit;
    end;
  end;
end;

// Generate a 32-char (128-bit) random hex token. Used as the default
// agent bearer secret on fresh installs so the user has something
// secure pre-filled and can either keep it (and copy it to the hub
// config) or paste over it with an existing token to pair with a
// hub that already knows the secret.
function GenerateRandomToken(): String;
var
  i: Integer;
  Hex: String;
begin
  Hex := '0123456789abcdef';
  Result := '';
  for i := 1 to 32 do
    Result := Result + Hex[Random(16) + 1];
end;

var
  SettingsPage: TInputQueryWizardPage;

procedure InitializeWizard();
begin
  SettingsPage := CreateInputQueryPage(
    wpSelectTasks,
    'Magpilot Settings',
    'Configure the agent. The agent token is auto-generated; the hub ' +
    'bearer comes from your hub config (leave blank to skip hub-mediated ' +
    'log forwarding + autoupdate checks).',
    'These values are written to <install>\config\magpilot.env and ' +
    're-read on every install (so re-running the installer or ' +
    '`magpilot --magpilot-update` preserves them).');
  SettingsPage.Add('Hub URL (e.g. http://192.168.1.239:7088):', False);
  SettingsPage.Add('Agent token (auto-generated; bearer the hub uses to call IN):', False);
  SettingsPage.Add('Hub bearer (the hub''s MAGPILOT_HUB_BEARER, agent uses to call OUT):', True);
  SettingsPage.Add('Public URL the hub uses to reach this agent:', False);

  // Defaults shown on first install. ShouldSkipPage overrides these from
  // an existing magpilot.env if one is present (upgrade path).
  SettingsPage.Values[0] := 'http://192.168.1.239:7088';
  SettingsPage.Values[1] := GenerateRandomToken();
  SettingsPage.Values[2] := '';
  SettingsPage.Values[3] := 'http://' + GetComputerNameString + ':5099';
end;

function ShouldSkipPage(PageID: Integer): Boolean;
var
  EnvPath, ExistingHubUrl, ExistingToken, ExistingBearer, ExistingPublic: String;
begin
  Result := False;
  // First place {app} is safe to expand: ShouldSkipPage(SettingsPage.ID)
  // runs after wpSelectDir, in both interactive and silent installs.
  // Pre-populate from any pre-existing magpilot.env so silent re-installs
  // (e.g. magpilot --magpilot-update) preserve hub URL + tokens.
  if PageID = SettingsPage.ID then
  begin
    EnvPath := ExpandConstant('{app}\config\' + EnvFileName);
    if FileExists(EnvPath) then
    begin
      ExistingHubUrl := ReadEnvKey(EnvPath, 'MAGPILOT_HUB_URL');
      ExistingToken  := ReadEnvKey(EnvPath, 'MAGPILOT_AGENT_TOKEN');
      ExistingBearer := ReadEnvKey(EnvPath, 'MAGPILOT_HUB_BEARER');
      ExistingPublic := ReadEnvKey(EnvPath, 'MAGPILOT_AGENT_PUBLIC_URL');
      if ExistingHubUrl <> '' then SettingsPage.Values[0] := ExistingHubUrl;
      if ExistingToken  <> '' then SettingsPage.Values[1] := ExistingToken;
      if ExistingBearer <> '' then SettingsPage.Values[2] := ExistingBearer;
      if ExistingPublic <> '' then SettingsPage.Values[3] := ExistingPublic;
    end;
  end;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if CurPageID = SettingsPage.ID then
  begin
    if (Trim(SettingsPage.Values[1]) = '') and WizardIsComponentSelected('agent') then
    begin
      MsgBox('Agent token must not be empty when the Agent component is installed.', mbError, MB_OK);
      Result := False;
    end;
  end;
end;

procedure WriteEnvFile();
var
  EnvPath: String;
  Lines: TArrayOfString;
begin
  EnvPath := ExpandConstant('{app}\config\' + EnvFileName);
  ForceDirectories(ExtractFilePath(EnvPath));
  SetArrayLength(Lines, 6);
  Lines[0] := '# Magpilot environment file. Sourced by install-task.ps1 on every';
  Lines[1] := '# scheduled-task launch. Re-run the installer to change these.';
  Lines[2] := 'MAGPILOT_HUB_URL=' + SettingsPage.Values[0];
  Lines[3] := 'MAGPILOT_AGENT_TOKEN=' + SettingsPage.Values[1];
  Lines[4] := 'MAGPILOT_HUB_BEARER=' + SettingsPage.Values[2];
  Lines[5] := 'MAGPILOT_AGENT_PUBLIC_URL=' + SettingsPage.Values[3];
  SaveStringsToFile(EnvPath, Lines, False);
end;

procedure RunPwsh(ScriptPath, Args: String);
var
  ResultCode: Integer;
  Cmd: String;
begin
  Cmd := '-NoProfile -ExecutionPolicy Bypass -File "' + ScriptPath + '" ' + Args;
  if not Exec('powershell.exe', Cmd, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    Log('Failed to launch ' + ScriptPath);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if CurStep = ssInstall then
  begin
    // Stop the existing scheduled task (if any) so we can replace its exe.
    Exec('powershell.exe',
      '-NoProfile -Command "Stop-ScheduledTask -TaskName MagpilotAgent -ErrorAction SilentlyContinue"',
      '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Sleep(800);
  end;

  if CurStep = ssPostInstall then
  begin
    if WizardIsComponentSelected('agent') then
    begin
      WriteEnvFile();
      if WizardIsTaskSelected('schedtask') then
        RunPwsh(ExpandConstant('{app}\install-task.ps1'),
          '-InstallDir "' + ExpandConstant('{app}') + '"');
      if WizardIsTaskSelected('firewall') then
        RunPwsh(ExpandConstant('{app}\firewall.ps1'), '-Action Add');
    end;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  PathStr, NewPath, BinDir, Marker: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    // Stop + unregister the scheduled task; remove firewall rules. Both
    // helpers are idempotent and tolerate "already gone".
    RunPwsh(ExpandConstant('{app}\uninstall-task.ps1'), '');
    RunPwsh(ExpandConstant('{app}\firewall.ps1'), '-Action Remove');

    // Strip {app}\bin from system PATH if present.
    if RegQueryStringValue(HKEY_LOCAL_MACHINE,
         'SYSTEM\CurrentControlSet\Control\Session Manager\Environment', 'Path', PathStr) then
    begin
      BinDir := ExpandConstant('{app}\bin');
      Marker := ';' + Uppercase(PathStr) + ';';
      if Pos(';' + Uppercase(BinDir) + ';', Marker) > 0 then
      begin
        // StringChange's first param is `var`, so assign to NewPath first.
        NewPath := ';' + PathStr + ';';
        StringChange(NewPath, ';' + BinDir + ';', ';');
        // Trim leading/trailing semicolons added above.
        if Copy(NewPath, 1, 1) = ';' then NewPath := Copy(NewPath, 2, Length(NewPath));
        if Copy(NewPath, Length(NewPath), 1) = ';' then NewPath := Copy(NewPath, 1, Length(NewPath) - 1);
        RegWriteExpandStringValue(HKEY_LOCAL_MACHINE,
          'SYSTEM\CurrentControlSet\Control\Session Manager\Environment', 'Path', NewPath);
      end;
    end;
  end;
end;
