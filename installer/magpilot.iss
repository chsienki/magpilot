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

// V1 of magpilot-pairing (2026-06-09): the installer no longer collects
// hub URL / agent token / hub bearer / public URL via a wizard page.
// The agent ships in a "disconnected" state -- it boots with a random
// MAGPILOT_AGENT_TOKEN that install-task.ps1 generates the first time
// the env file is created, then sits idle until the user runs
//
//     magpilot --magpilot-pair=<bundle>
//
// from the launcher with a bundle copied from the hub's /admin/enroll
// page. That subcommand overwrites magpilot.env with the hub-supplied
// values and restarts the scheduled task. The old four-field wizard
// page (with its hub URL / agent token / hub bearer / public URL
// fields + the upgrade-path pre-population from an existing env file)
// is gone; install-task.ps1 preserves any existing magpilot.env on
// upgrade so a paired agent stays paired across re-installs.

procedure RunPwsh(ScriptPath, Args: String);
var
  ResultCode: Integer;
  Cmd: String;
begin
  Cmd := '-NoProfile -ExecutionPolicy Bypass -File "' + ScriptPath + '" ' + Args;
  Log('RunPwsh: ' + Cmd);
  if not Exec('powershell.exe', Cmd, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    Log('  failed to launch ' + ScriptPath)
  else
    Log('  exit code: ' + IntToStr(ResultCode));
end;

// Capture an environment variable from the installer's host process.
// {%USERNAME} expands at install-script-PARSE time, which doesn't let us
// distinguish 'SYSTEM' from a real user. GetEnv is evaluated when this
// runs, so we get the actual environment of the installer process.
function GetCallerUser(): String;
var
  user, domain: String;
begin
  user   := GetEnv('USERNAME');
  domain := GetEnv('USERDOMAIN');
  if (user = '') or (Length(user) = 0) then
    Result := ''
  else if Copy(user, Length(user), 1) = '$' then
    // Machine account (e.g. SANDBOX$). Don't claim it as the install
    // user; let install-task.ps1's discovery chain pick a real user.
    Result := ''
  else if domain <> '' then
    Result := domain + '\' + user
  else
    Result := user;
end;

function BoolStr(b: Boolean): String;
begin
  if b then Result := 'true' else Result := 'false';
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
  installUser, installTaskArgs: String;
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
    Log('ssPostInstall hook entered.');
    Log('  WizardIsComponentSelected(agent)=' + BoolStr(WizardIsComponentSelected('agent')));
    Log('  WizardIsTaskSelected(schedtask)=' + BoolStr(WizardIsTaskSelected('schedtask')));
    Log('  WizardIsTaskSelected(firewall)=' + BoolStr(WizardIsTaskSelected('firewall')));

    if WizardIsComponentSelected('agent') then
    begin
      // V1 of magpilot-pairing (2026-06-09): no installer-collected
      // secrets. install-task.ps1 creates a minimal disconnected
      // magpilot.env (just a random MAGPILOT_AGENT_TOKEN) on a fresh
      // install, preserves any existing file on upgrade. The user
      // pairs the agent with a hub afterwards via
      //   magpilot --magpilot-pair=<bundle>
      // copied from the hub's /admin/enroll page.

      // Pass -User if we have a real one. install-task.ps1 has its own
      // discovery chain when -User is empty (Win32_ComputerSystem, quser,
      // env fallback) and refuses to register against a machine account.
      installUser := GetCallerUser();
      installTaskArgs := '-InstallDir "' + ExpandConstant('{app}') + '"';
      if installUser <> '' then
      begin
        installTaskArgs := installTaskArgs + ' -User "' + installUser + '"';
        Log('  install-task user: ' + installUser);
      end
      else
        Log('  install-task user: <unset, install-task.ps1 will auto-discover>');

      if WizardIsTaskSelected('schedtask') then
        RunPwsh(ExpandConstant('{app}\install-task.ps1'), installTaskArgs);
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
