; DNN Manager - Windows installer (Inno Setup 6).
;
; Build it with src\DnnManager.Installer\build.ps1, which publishes the app and passes the defines below:
;   AppVersion  the newest version tag, or -Version     (required)
;   FileVersion four-part file version, e.g. 1.7.0.0    (default AppVersion)
;   PublishDir  the self-contained publish output       (default bin\app)
;   OutputDir   where Setup is written                  (default ..\..\publish)
;   ImagesDir   wizard images made from the app icon    (optional)
;   SignToolName a Sign Tool defined with ISCC's /S     (optional - Setup and its uninstaller are then signed)
;   LauncherPath DnnManager-launcher.exe, built with Native AOT (optional - a release always has it; without it the
;               sign-in task and the administrator prompt start DnnManager.exe itself, as up to 1.8.1)
;
; Installs for all users into Program Files (Windows asks for administrator rights when Setup starts): DNN Manager
; always runs as administrator, so it belongs in a folder only administrators can change. Setup /CURRENTUSER still
; installs for the current user only, into %LOCALAPPDATA%\Programs\DnnManager - and an installation made that way
; (the default up to 1.8.1) is updated where it is: the in-app update passes /CURRENTUSER, and a Setup started with
; administrator rights offers to move it to Program Files or starts again for this user only (InitializeSetup). The
; user's settings live in Documents\DnnManager, which Setup never writes to, so upgrading, moving, reinstalling or
; uninstalling keeps them.
;
; What it installs comes from GitHub: a new install and Update the newest release, Repair the installed version
; again. Any version but its own it downloads and hands over to (see HandOver below) - an old Setup never has to be
; replaced.

#ifndef AppVersion
  #error AppVersion is not defined - build with src\DnnManager.Installer\build.ps1 (or pass /DAppVersion=x.y.z to ISCC).
#endif
#ifndef FileVersion
  #define FileVersion AppVersion
#endif
#ifndef PublishDir
  #define PublishDir "bin\app"
#endif
#ifndef OutputDir
  #define OutputDir "..\..\publish"
#endif

#define AppName "DNN Manager"
#define AppExe "DnnManager.exe"
; Starts DnnManager.exe without the .NET variables of the user's environment - for the sign-in task and the administrator
; prompt (src\DnnManager.Launcher; keep in step with LaunchEnvironment.LauncherFileName).
#define LauncherExe "DnnManager-launcher.exe"
#define AppPublisher "Albadit"
#define AppUrl "https://github.com/Albadit/DnnManager.NET"
; Identifies the installation for upgrades and uninstall - never change it.
#define AppGuid "AD68C57A-D887-4297-A905-B6F28C1D66D1"
; There while DNN Manager runs, and set to ask it to quit (src\DnnManager.Presentation\RunningMarker.cs, SingleInstance.cs).
#define RunningMutex "DnnManager.NET.Running"
#define QuitEvent "DnnManager.NET.Quit"

[Setup]
AppId={{{#AppGuid}}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
VersionInfoVersion={#FileVersion}
VersionInfoProductVersion={#FileVersion}
VersionInfoProductTextVersion={#AppVersion}
VersionInfoProductName={#AppName}
VersionInfoDescription={#AppName} Setup

; For all users (administrative install mode): {autopf} is Program Files, {autoprograms} and {autodesktop} the common
; Start menu and desktop, HKA (and the uninstall entry) HKLM. /CURRENTUSER on the command line installs for this user
; only, without administrator rights: {autopf} is %LOCALAPPDATA%\Programs, the user's Start menu and desktop, HKCU.
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=commandline
DefaultDirName={autopf}\DnnManager
; An upgrade goes where the previous version is, without asking again - the one in HKA's uninstall entry, so only an
; installation of the same mode.
UsePreviousAppDir=yes
DisableDirPage=auto
DisableProgramGroupPage=yes
DisableWelcomePage=yes
LicenseFile=..\..\LICENSE
ShowLanguageDialog=no

ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0

; No AppMutex: a running DNN Manager isn't a reason to stop at the start. Setup and the uninstaller ask it to quit just
; before they replace or remove its files (CloseDnnManager below) - it runs elevated, so they can't close it themselves.
; A Setup started by another one - handed over to (/HandedOver=1), started again on its first page (/Done=…), or for
; this user only - has a mutex of its own: the one that started it may still run (it waits for one it handed over to).
; /Again counts the Setups started that way, so no two of them have the same.
SetupMutex=DnnManager.NET.Setup{param:HandedOver|}{param:Done|}{param:Again|}

WizardStyle=modern
SetupIconFile=..\DnnManager.Presentation\Assets\dnn.ico
#ifdef ImagesDir
WizardImageFile={#ImagesDir}\wizard-100.bmp,{#ImagesDir}\wizard-200.bmp
WizardSmallImageFile={#ImagesDir}\wizard-small-100.bmp,{#ImagesDir}\wizard-small-200.bmp
#endif
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExe}

OutputDir={#OutputDir}
OutputBaseFilename=DnnManager_Setup-{#AppVersion}-x64
Compression=lzma2/max
SolidCompression=yes

#ifdef SignToolName
; Signed: Setup, its uninstaller and the copies of itself it runs. The Sign Tool's command comes with ISCC's /S switch.
SignTool={#SignToolName}
SignedUninstaller=yes
#endif

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Messages]
; What stays and what can go too is asked just before this (OfferToRemoveData) - this message can't know the answer.
UninstalledAll=%1 was successfully removed from your computer.

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[InstallDelete]
; Up to 1.3.0 the program was dnnmanager.exe. Windows keeps a file name's old casing when it is overwritten, so
; it is deleted first and the upgrade really installs DnnManager.exe.
Type: files; Name: "{app}\dnnmanager.exe"
#ifndef LauncherPath
; Built without the launcher: one an earlier version installed mustn't stay behind, older than the exe it starts.
Type: files; Name: "{app}\{#LauncherExe}"
#endif

[Files]
; The app is a self-contained single-file publish (DnnManager.exe plus a few native DLLs) - no .NET install needed.
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
#ifdef LauncherPath
Source: "{#LauncherPath}"; DestDir: "{app}"; DestName: "{#LauncherExe}"; Flags: ignoreversion
#endif

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
; The app asks for administrator rights itself (UAC) when it starts, as it does from the shortcuts. Not when Setup goes
; back to its first page after a Repair or Update - it starts DNN Manager again itself if it was running.
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent; Check: not GoesBackWhenDone

; "Start DNN Manager when you sign in" (Settings - General) is a scheduled task - it goes with the app: the uninstaller
; removes it in [Code] (RemoveStartupTask), with administrator rights when it must.

[Code]
// What Setup installs comes from GitHub:
// - a new install: the newest release;
// - DNN Manager installed: the first page has a button for each choice - "Update to X" (when GitHub has a newer
//   release), "Repair", which installs the installed version again, and "Uninstall", which runs the installed
//   uninstaller. When one is done, Setup starts again on that page (/Done=…) - after an uninstall, as a new install.
// Setup installs the version it carries itself; any other version it downloads from GitHub (that release's Setup,
// checked against GitHub's SHA-256 and its version) and hands over to it with /HandedOver=1 - the new Setup skips
// the pages already answered here (and, with /Back=…, the first page comes back when it is done) - and this Setup
// waits for it, hidden, then closes. Silent runs (the in-app update) install what they carry.
// DNN Manager installed for the current user only (HKCU), and this Setup for all users: it offers to move that
// installation to Program Files, or starts again for this user only to update it where it is - never a second copy.
// Offline: the newest version is the one this Setup carries, and when a download fails Setup offers to install that.
// A running DNN Manager is closed just before its files are replaced or removed - not when Setup starts.
const
  UninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{' + '{#AppGuid}' + '}_is1';
  ReleasesApi = 'https://api.github.com/repos/Albadit/DnnManager.NET/releases/';
  EventModifyState = $0002;
  ActionUpdate = 1;
  ActionRepair = 2;
  ActionUninstall = 3;
  // "Start DNN Manager when you sign in" - keep in step with StartupTask.TaskName (src\DnnManager.Infrastructure\Startup).
  StartupTaskName = 'DNN Manager';

var
  MaintenancePage: TWizardPage;
  DownloadPage: TDownloadWizardPage;
  InstalledVersion: String;
  InstalledUninstaller: String;
  // GitHub's newest release - or this Setup's version, when that is newer or GitHub couldn't be asked (Offline).
  NewestVersion: String;
  Offline: Boolean;
  // The first page's button that was clicked.
  Action: Integer;
  // Set when Setup goes back to its first page once the install is done: 'Repaired' or 'Updated' - from that page's
  // button, or /Back=… from the older Setup that handed over to this one.
  BackWhenDone: String;
  // DNN Manager was running and was asked to quit for the install - it is started again afterwards.
  WasRunning: Boolean;
  Closing: Boolean;
  // DNN Manager installed for this user only, moved to Program Files by this (all users) Setup: that installation's
  // uninstaller, run once the new one is in place.
  PerUserUninstaller: String;
  // This Setup's exe as it started - it is started again only while it is still that file (StartSetup).
  SetupSha256: String;
  // The installation has started (ssInstall): from then on Inno Setup's Exec may run Setup's own exe (StartSetup).
  InstallStarted: Boolean;

function OpenEvent(DesiredAccess: DWORD; InheritHandle: BOOL; Name: String): THandle;
  external 'OpenEventW@kernel32.dll stdcall';
function SetEvent(Event: THandle): BOOL;
  external 'SetEvent@kernel32.dll stdcall';
function CloseHandle(Handle: THandle): BOOL;
  external 'CloseHandle@kernel32.dll stdcall';
function GetFileAttributes(Path: String): DWORD;
  external 'GetFileAttributesW@kernel32.dll stdcall';

// /Done and /Back say what was done, for the first page: only these words - anything else is ''. They go on the command
// line of a Setup started with administrator rights (StartAgain, HandOver), so nothing a caller typed may reach it.
function DoneParam(const Value: String): String;
begin
  if (Value = 'Updated') or (Value = 'Repaired') or (Value = 'Uninstalled') then Result := Value else Result := '';
end;

function IsInstalled: Boolean;
begin
  Result := InstalledUninstaller <> '';
end;

// Started by an older Setup that downloaded this one.
function IsHandedOver: Boolean;
begin
  Result := ExpandConstant('{param:HandedOver|}') <> '';
end;

// The [Run] entry's check: no "Launch DNN Manager" on a Finished page that isn't shown.
function GoesBackWhenDone: Boolean;
begin
  Result := BackWhenDone <> '';
end;

function IsDnnManagerRunning: Boolean;
begin
  Result := CheckForMutexes('{#RunningMutex}');
end;

// Waits up to Seconds for DNN Manager to be gone.
function WaitUntilClosed(Seconds: Integer): Boolean;
var
  I: Integer;
begin
  for I := 1 to Seconds * 4 do
  begin
    if not IsDnnManagerRunning then Break;
    Sleep(250);
  end;
  Result := not IsDnnManagerRunning;
end;

// True when Path is an absolute path (C:\… or \\server\…) with none of the characters a Windows path can't have, no
// control characters and none of PowerShell's typographic quotes - so it can go into a command line in quotes.
function IsPlainPath(const Path: String): Boolean;
var
  I, C: Integer;
begin
  Result := (Length(Path) >= 3) and (((Path[2] = ':') and (Path[3] = '\')) or (Copy(Path, 1, 2) = '\\'));
  if not Result then Exit;
  for I := 1 to Length(Path) do
  begin
    C := Ord(Path[I]);
    if (C < 32) or ((C >= $2018) and (C <= $201E)) or (Pos(Path[I], '"<>|*?') > 0) or ((Path[I] = ':') and (I <> 2)) then
    begin
      Result := False;
      Exit;
    end;
  end;
end;

// Text as a PowerShell string literal: in single quotes, where nothing but a single quote means anything - doubled.
function PowerShellString(const Text: String): String;
begin
  Result := Text;
  StringChangeEx(Result, '''', '''''', True);
  Result := '''' + Result + '''';
end;

// Ends the installed DNN Manager's process - only the one in Folder, never the update helper (a copy of the exe in
// TEMP). It runs elevated, so ending it needs administrator rights: Windows asks (UAC) unless Setup has them. Folder
// can come from the current user's registry or uninstall log, which programs without those rights can change: it goes
// into the elevated command only as a plain path, quoted as a PowerShell string.
procedure EndDnnManager(const Folder: String);
var
  Exe: String;
  ResultCode: Integer;
begin
  Exe := AddBackslash(Folder) + '{#AppExe}';
  if not IsPlainPath(Exe) then
  begin
    Log('Not ending DNN Manager - its folder isn''t a plain path: ' + Folder);
    Exit;
  end;
  if not ShellExec('runas', ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -NonInteractive -Command "Get-Process DnnManager -ErrorAction SilentlyContinue | ' +
    'Where-Object { $_.Path -eq ' + PowerShellString(Exe) + ' } | Stop-Process -Force"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    Log('Could not end DNN Manager: ' + SysErrorMessage(ResultCode));
end;

// Closes the running DNN Manager in Folder before its files are replaced or removed - without asking: it is asked to
// quit (1.7.6 and newer listen, and quit without questions - a running operation is cancelled), and when it is still
// there after a few seconds (an older version, or it doesn't answer) its process is ended. False when it is still
// running and the user chose Cancel.
function CloseDnnManager(const Folder: String): Boolean;
var
  Event: THandle;
begin
  repeat
    Event := OpenEvent(EventModifyState, False, '{#QuitEvent}');
    if Event <> 0 then
    begin
      SetEvent(Event);
      CloseHandle(Event);
      WaitUntilClosed(10);
    end;
    if IsDnnManagerRunning then
    begin
      Log('DNN Manager didn''t quit when asked - ending its process.');
      EndDnnManager(Folder);
      WaitUntilClosed(5);
    end;
    Result := not IsDnnManagerRunning;
  until Result or (SuppressibleMsgBox('{#AppName} couldn''t be closed.' + #13#10#13#10 +
    'Quit it (right-click its icon by the clock, then Quit DNN Manager), then choose Retry.',
    mbError, MB_RETRYCANCEL, IDCANCEL) <> IDRETRY);
end;

// True when there is a "Start DNN Manager when you sign in" task; Target is the exe it starts ('' when that can't be
// read).
function FindStartupTask(var Target: String): Boolean;
var
  Output: TExecOutput;
  Xml: String;
  I, P, ResultCode: Integer;
begin
  Target := '';
  try
    Result := ExecAndCaptureOutput(ExpandConstant('{sys}\schtasks.exe'), '/Query /TN "' + StartupTaskName + '" /XML', '',
      SW_SHOWNORMAL, ewWaitUntilTerminated, ResultCode, Output) and (ResultCode = 0);
  except
    Log('Could not read the sign-in task: ' + GetExceptionMessage);
    Result := Exec(ExpandConstant('{sys}\schtasks.exe'), '/Query /TN "' + StartupTaskName + '"', '', SW_HIDE,
      ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
    Exit;
  end;
  if not Result then Exit;
  // <Command>C:\…\DnnManager.exe</Command>, XML-escaped (StartupTask.cs).
  for I := 0 to GetArrayLength(Output.StdOut) - 1 do Xml := Xml + Output.StdOut[I];
  P := Pos('<Command>', Xml);
  if P = 0 then Exit;
  Xml := Copy(Xml, P + Length('<Command>'), Length(Xml));
  P := Pos('</Command>', Xml);
  if P = 0 then Exit;
  Target := Trim(Copy(Xml, 1, P - 1));
  StringChangeEx(Target, '&quot;', '"', True);
  StringChangeEx(Target, '&apos;', '''', True);
  StringChangeEx(Target, '&lt;', '<', True);
  StringChangeEx(Target, '&gt;', '>', True);
  StringChangeEx(Target, '&amp;', '&', True);
  Target := RemoveQuotes(Target);
end;

// Removes the "Start DNN Manager when you sign in" task of the DNN Manager AppExe - it starts DNN Manager with the
// highest rights at every sign-in, so it mustn't outlive that exe. The task starts AppExe itself (up to 1.8.1) or the
// launcher beside it. A task that starts another copy of DNN Manager, one that is still there, is kept. DNN Manager
// registers it elevated, so deleting it can take administrator rights: when the uninstaller doesn't have them and the
// task is still there, Windows asks (UAC).
procedure RemoveStartupTask(const AppExe: String);
var
  Target: String;
  ResultCode: Integer;
begin
  if not FindStartupTask(Target) then
  begin
    Log('There is no sign-in task.');
    Exit;
  end;
  if (Target <> '') and (CompareText(Target, AppExe) <> 0) and
    (CompareText(Target, AddBackslash(ExtractFileDir(AppExe)) + '{#LauncherExe}') <> 0) and FileExists(Target) then
  begin
    Log('The sign-in task starts ' + Target + ', another copy of DNN Manager - it is kept.');
    Exit;
  end;
  Exec(ExpandConstant('{sys}\schtasks.exe'), '/Delete /TN "' + StartupTaskName + '" /F', '', SW_HIDE,
    ewWaitUntilTerminated, ResultCode);
  if not FindStartupTask(Target) then
  begin
    Log('Removed the sign-in task.');
    Exit;
  end;
  Log('The sign-in task could not be removed (schtasks exit code ' + IntToStr(ResultCode) + ') - again, with administrator rights.');
  if not ShellExec('runas', ExpandConstant('{sys}\schtasks.exe'), '/Delete /TN "' + StartupTaskName + '" /F', '',
    SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    Log('Could not start schtasks with administrator rights: ' + SysErrorMessage(ResultCode))
  else
    Log('schtasks with administrator rights ended with exit code ' + IntToStr(ResultCode) + '.');
  if FindStartupTask(Target) then
  begin
    Log('The sign-in task is still there.');
    SuppressibleMsgBox('The scheduled task "' + StartupTaskName + '", which started {#AppName} when you signed in, ' +
      'could not be removed.' + #13#10#13#10 + 'Delete it in Task Scheduler (Task Scheduler Library).', mbError, MB_OK, IDOK);
  end
  else
    Log('Removed the sign-in task with administrator rights.');
end;

// True when version A is newer than B ("1.7.2" or "1.7.2.0"); False when either isn't a version.
function IsNewer(const A, B: String): Boolean;
var
  PackedA, PackedB: Int64;
begin
  Result := (StrToVersion(A, PackedA) or StrToVersion(A + '.0', PackedA)) and
            (StrToVersion(B, PackedB) or StrToVersion(B + '.0', PackedB)) and
            (ComparePackedVersion(PackedA, PackedB) > 0);
end;

// The text of "Key":"..." in GitHub's (compact) JSON; '' when there is none.
function JsonString(const Json, Key: String): String;
var
  Rest: String;
  P: Integer;
begin
  Result := '';
  P := Pos('"' + Key + '":"', Json);
  if P = 0 then Exit;
  Rest := Copy(Json, P + Length(Key) + 4, Length(Json));
  P := Pos('"', Rest);
  if P > 0 then Result := Copy(Rest, 1, P - 1);
end;

// GitHub's JSON for a release - Path is 'latest' or 'tags/v1.7.2'. Raises an exception when it can't be had.
function GitHubRelease(const Path: String): String;
var
  Http: Variant;
  Status: Integer;
begin
  Http := CreateOleObject('WinHttp.WinHttpRequest.5.1');
  // Resolve, connect, send and receive within a few seconds.
  Http.SetTimeouts(5000, 5000, 5000, 10000);
  Http.Open('GET', ReleasesApi + Path, False);
  Http.SetRequestHeader('User-Agent', 'DnnManager-Setup/{#AppVersion}');
  Http.SetRequestHeader('Accept', 'application/vnd.github+json');
  Http.Send('');
  Status := Http.Status;
  if Status <> 200 then RaiseException('GitHub answered ' + IntToStr(Status) + ' for ' + ReleasesApi + Path + '.');
  Result := Http.ResponseText;
end;

// /Again for a Setup this one starts: one more than its own.
function NextAgain: String;
begin
  Result := IntToStr(StrToIntDef(ExpandConstant('{param:Again|0}'), 0) + 1);
end;

// Starts this Setup's exe again with Params - as the user who started Setup, without administrator rights, when
// AsOriginalUser - and doesn't wait for it. Params are built here from fixed switches, DoneParam's words and numbers
// only, never from text a caller passed. Once the installation has started, Inno Setup's Exec runs the exe itself.
// Before that Exec refuses to run Setup's own exe ("Can't be used to execute Setup itself until the installation has
// started" - its help on Exec), so it goes through cmd's start, which doesn't wait: /d (no AutoRun commands), /v:off
// (no !…! expanded, whatever the registry says) and the path in quotes, checked to have no quote, no % and no control
// character - cmd sees nothing in it to act on. With administrator rights, only while the exe is still the file that
// started: it is where the user saved it, a folder programs without those rights can change.
function StartSetup(const Params: String; AsOriginalUser: Boolean): Boolean;
var
  SetupExe, CommandLine: String;
  ResultCode: Integer;
begin
  Result := False;
  SetupExe := ExpandConstant('{srcexe}');
  // cmd expands %…% even in quotes; ! is refused too, in case delayed expansion were on after all.
  if not IsPlainPath(SetupExe) or (Pos('%', SetupExe) > 0) or (Pos('!', SetupExe) > 0) then
  begin
    Log('Not starting Setup again - its path can''t go on a command line: ' + SetupExe);
    Exit;
  end;
  if not AsOriginalUser and IsAdmin then
    try
      if GetSHA256OfFile(SetupExe) <> SetupSha256 then
      begin
        Log('Not starting Setup again with administrator rights - ' + SetupExe + ' has changed since it started.');
        Exit;
      end;
    except
      Log('Not starting Setup again - ' + SetupExe + ' can''t be read: ' + GetExceptionMessage);
      Exit;
    end;
  try
    if InstallStarted then
    begin
      // No shell between: the exe, its switches.
      if AsOriginalUser then
        Result := ExecAsOriginalUser(SetupExe, Params, '', SW_SHOW, ewNoWait, ResultCode)
      else
        Result := Exec(SetupExe, Params, '', SW_SHOW, ewNoWait, ResultCode);
      if not Result then Log('Could not start Setup again: ' + SysErrorMessage(ResultCode));
      Exit;
    end;
    CommandLine := '/d /v:off /c start "" "' + SetupExe + '" ' + Params;
    if AsOriginalUser then
      Result := ExecAsOriginalUser(ExpandConstant('{cmd}'), CommandLine, '', SW_HIDE, ewWaitUntilTerminated, ResultCode)
    else
      Result := Exec(ExpandConstant('{cmd}'), CommandLine, '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    if not Result then Log('Could not start Setup again: ' + SysErrorMessage(ResultCode))
    else if ResultCode <> 0 then
    begin
      Result := False;
      Log('Could not start Setup again: cmd ended with exit code ' + IntToStr(ResultCode) + '.');
    end;
  except
    Result := False;
    Log('Could not start Setup again: ' + GetExceptionMessage);
  end;
end;

// DNN Manager is installed for this user only, and this Setup runs for all users: True to move it to Program Files
// (PerUserUninstaller), False to stop here - this Setup was started again for this user only, or Cancel was chosen.
// Silent (a script that didn't say /CURRENTUSER) and handed over (the older Setup asked): moved.
function MovePerUserInstallation(const Uninstaller: String): Boolean;
var
  Version: String;
begin
  Result := True;
  if not RegQueryStringValue(HKCU, UninstallKey, 'DisplayVersion', Version) then Version := '';
  if not WizardSilent and not IsHandedOver then
    case MsgBox('{#AppName} ' + Version + ' is installed for your account only, in:' + #13#10 +
      ExtractFileDir(Uninstaller) + #13#10#13#10 +
      'Programs you run can change that folder, and {#AppName} runs as administrator - one of them could use it to ' +
      'get administrator rights.' + #13#10#13#10 +
      'Yes (recommended): move it to Program Files - it is installed for all users, and the copy for your account ' +
      'is removed. Your settings in Documents\DnnManager are kept; turn on "Start DNN Manager when you sign in" ' +
      'again if you use it.' + #13#10 +
      'No: update it where it is - Setup starts again for your account only.',
      mbConfirmation, MB_YESNOCANCEL) of
      IDNO:
        begin
          Result := False;
          if not StartSetup('/SP- /CURRENTUSER /Again=' + NextAgain, True) then
            MsgBox('Setup could not be started again for your account only. Run it from a command prompt with ' +
              '/CURRENTUSER:' + #13#10#13#10 + '"' + ExpandConstant('{srcexe}') + '" /CURRENTUSER', mbError, MB_OK);
          Exit;
        end;
      IDCANCEL:
        begin
          Result := False;
          Exit;
        end;
    end;
  PerUserUninstaller := Uninstaller;
  Log('DNN Manager ' + Version + ' is installed for this user only, in ' + ExtractFileDir(Uninstaller) +
    ' - it is moved to Program Files.');
end;

function InitializeSetup: Boolean;
var
  Value: String;
begin
  Result := False;
  // HKA is HKLM for a Setup for all users (the default) and HKCU for /CURRENTUSER - the installation this Setup would
  // replace.
  if RegQueryStringValue(HKA, UninstallKey, 'UninstallString', Value) and FileExists(RemoveQuotes(Value)) then
  begin
    InstalledUninstaller := RemoveQuotes(Value);
    if not RegQueryStringValue(HKA, UninstallKey, 'DisplayVersion', InstalledVersion) then
      InstalledVersion := '';
  end
  // Not installed for all users, but for this one: moved, or updated where it is by this Setup started for this user.
  else if IsAdminInstallMode and RegQueryStringValue(HKCU, UninstallKey, 'UninstallString', Value) and
    FileExists(RemoveQuotes(Value)) then
    if not MovePerUserInstallation(RemoveQuotes(Value)) then Exit;
  BackWhenDone := DoneParam(ExpandConstant('{param:Back|}'));
  // What StartSetup checks before it starts this Setup again with administrator rights.
  if IsAdmin and not WizardSilent then
    try
      SetupSha256 := GetSHA256OfFile(ExpandConstant('{srcexe}'));
    except
      Log('Could not read ' + ExpandConstant('{srcexe}') + ': ' + GetExceptionMessage);
    end;

  if not WizardSilent and not IsHandedOver then
    try
      NewestVersion := JsonString(GitHubRelease('latest'), 'tag_name');
      if (NewestVersion <> '') and ((NewestVersion[1] = 'v') or (NewestVersion[1] = 'V')) then Delete(NewestVersion, 1, 1);
      Log('GitHub''s newest release is ' + NewestVersion + '.');
    except
      Offline := True;
      Log('GitHub could not be asked for the newest release - installing {#AppVersion}: ' + GetExceptionMessage);
    end;
  if (NewestVersion = '') or IsNewer('{#AppVersion}', NewestVersion) then NewestVersion := '{#AppVersion}';
  Result := True;
end;

// The version Repair installs: the installed one, or this Setup's when the installed one is unknown.
function RepairVersion: String;
begin
  if InstalledVersion <> '' then Result := InstalledVersion else Result := '{#AppVersion}';
end;

// ─── The first page, when DNN Manager is installed: a button for each choice ───

// Text on the first page at Left, Top - as wide as the page leaves, as high as its lines.
function AddText(const Text: String; Left, Top: Integer): TNewStaticText;
begin
  Result := TNewStaticText.Create(MaintenancePage);
  Result.Parent := MaintenancePage.Surface;
  Result.AutoSize := False;
  Result.WordWrap := True;
  Result.Left := Left;
  Result.Top := Top;
  Result.Width := MaintenancePage.SurfaceWidth - Left;
  Result.Caption := Text;
  Result.AdjustHeight;
end;

// A button and, beside it, what it does - below Top, which moves past them.
procedure AddAction(const Caption, Note: String; Click: TNotifyEvent; var Top: Integer);
var
  Button: TNewButton;
  Text: TNewStaticText;
begin
  Button := TNewButton.Create(MaintenancePage);
  Button.Parent := MaintenancePage.Surface;
  Button.Caption := Caption;
  Button.Left := 0;
  Button.Top := Top;
  Button.Width := ScaleX(150);
  Button.Height := WizardForm.NextButton.Height + ScaleY(6);
  Button.OnClick := Click;
  Text := AddText(Note, Button.Width + ScaleX(14), Top);
  Text.Top := Top + (Button.Height - Text.Height) div 2;
  Top := Top + Button.Height + ScaleY(10);
end;

// A button was clicked: on as Next would go - the button is the page's Next, which is hidden there.
procedure Choose(Chosen: Integer);
begin
  Action := Chosen;
  WizardForm.NextButton.Visible := True;
  WizardForm.NextButton.OnClick(WizardForm.NextButton);
  // Still here: the uninstall was cancelled, or a download failed and Setup's own version was declined.
  if WizardForm.CurPageID = MaintenancePage.ID then WizardForm.NextButton.Visible := False;
end;

procedure UpdateClick(Sender: TObject);
begin
  Choose(ActionUpdate);
end;

procedure RepairClick(Sender: TObject);
begin
  Choose(ActionRepair);
end;

procedure UninstallClick(Sender: TObject);
begin
  Choose(ActionUninstall);
end;

procedure InitializeWizard;
var
  Top: Integer;
  Done, OfflineNote: String;
  DoneText: TNewStaticText;
begin
  DownloadPage := CreateDownloadPage('Downloading {#AppName}',
    'The version to install is downloaded from GitHub - Setup then continues with it.', nil);
  DownloadPage.ShowBaseNameInsteadOfUrl := True;

  // Started by an older Setup: what to do was chosen there. A silent run (the in-app update) installs what it carries -
  // nobody can click the page's buttons, and without one its Next aborts Setup.
  if not IsInstalled or IsHandedOver or WizardSilent then Exit;

  MaintenancePage := CreateCustomPage(wpWelcome, '{#AppName} is installed', 'Update, repair or uninstall it.');
  Top := 0;
  // Back here after Repair or Update.
  Done := DoneParam(ExpandConstant('{param:Done|}'));
  if Done = 'Repaired' then Done := 'Repair finished.'
  else if Done = 'Updated' then Done := 'Update finished.'
  else Done := '';
  if Done <> '' then
  begin
    DoneText := AddText(Done, 0, Top);
    DoneText.Font.Style := [fsBold];
    DoneText.AdjustHeight;
    Top := DoneText.Height + ScaleY(10);
  end;
  if Offline then
    OfflineNote := #13#10#13#10 + 'GitHub can''t be reached - Repair installs the version this Setup carries, {#AppVersion}.';
  Top := Top + AddText('{#AppName} ' + RepairVersion + ' is installed in:' + #13#10 + ExtractFileDir(InstalledUninstaller) + #13#10#13#10 +
    'Your settings in Documents\DnnManager are kept, unless you choose to remove them too when you uninstall.' + OfflineNote, 0, Top).Height + ScaleY(18);

  if IsNewer(NewestVersion, InstalledVersion) then
    AddAction('Update to ' + NewestVersion, 'Installs the newest release from GitHub.', @UpdateClick, Top);
  AddAction('Repair', 'Installs version ' + RepairVersion + ' again.', @RepairClick, Top);
  AddAction('Uninstall', 'Removes {#AppName} from this PC.', @UninstallClick, Top);
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if MaintenancePage = nil then Exit;
  // The page's buttons go on; Setup only offers to close.
  WizardForm.NextButton.Visible := CurPageID <> MaintenancePage.ID;
  if CurPageID = MaintenancePage.ID then
    WizardForm.CancelButton.Caption := 'Close'
  else
    WizardForm.CancelButton.Caption := SetupMessage(msgButtonCancel);
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  // The license was accepted when it was first installed, or in the older Setup that started this one. Going back to
  // the first page, Setup ends without its Finished page.
  Result := ((PageID = wpLicense) and (IsInstalled or IsHandedOver)) or ((PageID = wpFinished) and GoesBackWhenDone);
end;

// Starts this Setup again, on its first page, and closes this one - Done says on that page what was done.
procedure StartAgain(const Done: String);
var
  Mode: String;
begin
  if IsAdminInstallMode then Mode := '/ALLUSERS' else Mode := '/CURRENTUSER';
  StartSetup('/SP- /Done=' + DoneParam(Done) + ' ' + Mode + ' /Again=' + NextAgain, False);
end;

// Downloads that version's Setup from GitHub, checks it and runs it - this Setup waits for it, hidden, and then
// closes (or, when the first page is to come back, starts again). Raises an exception with the reason when it can't
// be run.
procedure HandOver(const Version: String);
var
  Json, AssetName, Digest, Sha256, NewSetup, FileVersion, Params: String;
  P, ResultCode: Integer;
  ComesBackHere: Boolean;
begin
  // GitHub's SHA-256 of the release's Setup ("digest", in its asset - before its browser_download_url).
  Json := GitHubRelease('tags/v' + Version);
  // DnnManager_Setup-<version>-x64.exe - or, for a release up to 1.7.6 (Repair of one), DnnManagerSetup-<version>-x64.exe.
  AssetName := 'DnnManager_Setup-' + Version + '-x64.exe';
  P := Pos('"name":"' + AssetName + '"', Json);
  if P = 0 then
  begin
    AssetName := 'DnnManagerSetup-' + Version + '-x64.exe';
    P := Pos('"name":"' + AssetName + '"', Json);
  end;
  if P = 0 then RaiseException('Release ' + Version + ' on GitHub has no DnnManager_Setup-' + Version + '-x64.exe.');
  Json := Copy(Json, P, Length(Json));
  P := Pos('"browser_download_url"', Json);
  if P > 0 then Json := Copy(Json, 1, P);
  Digest := JsonString(Json, 'digest');
  if CompareText(Copy(Digest, 1, 7), 'sha256:') = 0 then Sha256 := Lowercase(Copy(Digest, 8, 64));
  // It is run with administrator rights: a download that can't be checked isn't run.
  if Length(Sha256) <> 64 then RaiseException('GitHub lists no SHA-256 for ' + AssetName + ', so the download can''t be checked.');

  DownloadPage.Clear;
  DownloadPage.Add('{#AppUrl}/releases/download/v' + Version + '/' + AssetName, AssetName, Sha256);
  DownloadPage.Show;
  try
    DownloadPage.Download;
  finally
    DownloadPage.Hide;
  end;

  // Run from {tmp}, where it was downloaded and checked: with administrator rights Inno Setup makes that folder one
  // only administrators can change (for this user: read and run), and it goes when this Setup closes - so this Setup
  // waits until the new one is done.
  NewSetup := ExpandConstant('{tmp}\' + AssetName);
  if not GetVersionNumbersString(NewSetup, FileVersion) or (FileVersion <> Version + '.0') then
    RaiseException(AssetName + ' is not version ' + Version + ' (it reports ' + FileVersion + ').');
  // Checked once more right before it runs.
  if Lowercase(GetSHA256OfFile(NewSetup)) <> Sha256 then
    RaiseException(AssetName + ' changed after it was downloaded, so it isn''t run.');

  if IsAdminInstallMode then Params := '/ALLUSERS' else Params := '/CURRENTUSER';
  Params := '/SP- /HandedOver=1 ' + Params + ' /Again=' + NextAgain;
  // Back to the first page when it is done: a Setup newer than 1.8.1 leaves that to this one (/ReturnToCaller=1) -
  // started from its own exe, in this Setup's {tmp}, the first page couldn't stay once this Setup has closed. An older
  // one starts its own exe again; one older than 1.7.6 ignores /Back and shows its Finished page.
  ComesBackHere := (BackWhenDone <> '') and IsNewer(Version, '1.8.1');
  if BackWhenDone <> '' then Params := Params + ' /Back=' + BackWhenDone;
  if ComesBackHere then Params := Params + ' /ReturnToCaller=1';

  WizardForm.Hide;
  try
    if not Exec(NewSetup, Params, '', SW_SHOW, ewWaitUntilTerminated, ResultCode) then
      RaiseException('Could not start ' + NewSetup + ': ' + SysErrorMessage(ResultCode));
  finally
    // Also before closing - a hidden wizard ignores Close.
    WizardForm.Show;
  end;
  Log(NewSetup + ' ended with exit code ' + IntToStr(ResultCode) + ' - this Setup closes.');
  // Done: the first page says so. Cancelled or failed: the first page again, as it was.
  if ComesBackHere then
    if ResultCode = 0 then StartAgain(BackWhenDone) else StartAgain('');
  Closing := True;
  WizardForm.Close;
end;

// Installs that version: this Setup's own carries on (True); any other is downloaded and handed over to (False).
// When the download fails - offline, say - Setup offers its own version instead.
function InstallVersion(const Version: String): Boolean;
begin
  Result := (Version = '{#AppVersion}');
  if Result then Exit;
  try
    HandOver(Version);
  except
    Result := MsgBox('{#AppName} ' + Version + ' could not be downloaded from GitHub:' + #13#10 + GetExceptionMessage + #13#10#13#10 +
      'Install version {#AppVersion}, which this Setup carries, instead?', mbConfirmation, MB_YESNO) = IDYES;
  end;
end;

procedure RunUninstaller;
var
  ResultCode: Integer;
begin
  WizardForm.Hide;
  try
    // Waits until the uninstall has finished; the uninstaller asks for confirmation itself, and closes a running DNN
    // Manager.
    if not Exec(InstalledUninstaller, '', '', SW_SHOW, ewWaitUntilTerminated, ResultCode) then
    begin
      MsgBox('Could not start the uninstaller:' + #13#10 + SysErrorMessage(ResultCode), mbError, MB_OK);
      Exit;
    end;
  finally
    // Also before closing - a hidden wizard ignores Close.
    WizardForm.Show;
  end;

  // The uninstaller runs a copy of itself from TEMP, which deletes unins000.exe only after this Exec has
  // returned - so check its registry key, which is gone by now. Still there: the uninstall was cancelled - stay
  // on the page.
  if RegKeyExists(HKA, UninstallKey) then Exit;
  // Back to the first page - DNN Manager isn't installed now, so that is a new install's.
  StartAgain('Uninstalled');
  Closing := True;
  WizardForm.Close;
end;

// True when Folder is in Program Files - which only administrators can change.
function IsInProgramFiles(const Folder: String): Boolean;
begin
  Result := (Pos(Lowercase(AddBackslash(ExpandConstant('{commonpf64}'))), Lowercase(AddBackslash(Folder))) = 1) or
            (Pos(Lowercase(AddBackslash(ExpandConstant('{commonpf32}'))), Lowercase(AddBackslash(Folder))) = 1);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  // Elsewhere, a folder made by Setup can usually be changed by every user's programs (C:\ lets them change what is
  // made in it) - and DNN Manager runs as administrator.
  if (CurPageID = wpSelectDir) and IsAdminInstallMode and not IsInProgramFiles(WizardDirValue) then
  begin
    Result := MsgBox('{#AppName} runs as administrator. Outside Program Files, programs running without administrator ' +
      'rights can usually change the folder it is installed in - and so get those rights through it.' + #13#10#13#10 +
      'Install in ' + WizardDirValue + ' anyway?', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES;
    Exit;
  end;
  // A new install: the newest version.
  if (CurPageID = wpLicense) and not IsInstalled then
  begin
    Result := InstallVersion(NewestVersion);
    Exit;
  end;
  if (MaintenancePage = nil) or (CurPageID <> MaintenancePage.ID) then Exit;

  case Action of
    ActionUninstall:
      begin
        RunUninstaller;
        Result := False;
      end;
    ActionUpdate:
      begin
        BackWhenDone := 'Updated';
        Result := InstallVersion(NewestVersion);
      end;
    ActionRepair:
      begin
        BackWhenDone := 'Repaired';
        Result := InstallVersion(RepairVersion);
      end;
  else
    // Next itself is hidden on this page - only its buttons go on.
    Result := False;
  end;
end;

// Just before the files are replaced: a running DNN Manager is asked to quit (not for a new install - another copy, a
// portable one, can go on).
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if not IsInstalled or not IsDnnManagerRunning then Exit;
  WasRunning := True;
  if not CloseDnnManager(ExtractFileDir(InstalledUninstaller)) then
    Result := '{#AppName} is still running. Quit it - right-click its icon by the clock, then Quit DNN Manager - and run Setup again.';
end;

// Once DNN Manager is installed for all users: the copy that was installed for this user only goes - its sign-in task
// (with this Setup's administrator rights) and then its own uninstaller, run without them: it is in a folder the
// user's programs can change. It closes that copy if it runs, and keeps the settings (Documents\DnnManager).
procedure RemovePerUserInstallation;
var
  Folder: String;
  I, ResultCode: Integer;
begin
  Folder := ExtractFileDir(PerUserUninstaller);
  RemoveStartupTask(AddBackslash(Folder) + '{#AppExe}');
  try
    if not ExecAsOriginalUser(PerUserUninstaller, '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART', '', SW_SHOW,
      ewWaitUntilTerminated, ResultCode) then
      Log('Could not start ' + PerUserUninstaller + ': ' + SysErrorMessage(ResultCode))
    else
      Log(PerUserUninstaller + ' ended with exit code ' + IntToStr(ResultCode) + '.');
  except
    Log('Could not start ' + PerUserUninstaller + ': ' + GetExceptionMessage);
  end;
  // It finishes in a copy of itself: its registry key goes last.
  for I := 1 to 40 do
  begin
    if not RegKeyExists(HKCU, UninstallKey) then Break;
    Sleep(250);
  end;
  if RegKeyExists(HKCU, UninstallKey) then
  begin
    Log('The installation for this user only, in ' + Folder + ', is still there.');
    SuppressibleMsgBox('{#AppName} is now installed for all users, in:' + #13#10 + ExpandConstant('{app}') + #13#10#13#10 +
      'The copy installed for your account only, in ' + Folder + ', could not be removed. Uninstall it in Settings - ' +
      'Apps - Installed apps: of the two {#AppName} entries, the one that isn''t in Program Files.', mbInformation, MB_OK, IDOK);
  end
  else
    Log('Removed the installation for this user only, in ' + Folder + '.');
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if CurStep = ssInstall then InstallStarted := True;
  if (CurStep = ssPostInstall) and (PerUserUninstaller <> '') then RemovePerUserInstallation;
  if (CurStep <> ssDone) or not GoesBackWhenDone then Exit;
  // Quit for the install: running again, as before (it asks for administrator rights itself).
  if WasRunning then
    ExecAsOriginalUser(ExpandConstant('{app}\{#AppExe}'), '', ExpandConstant('{app}'), SW_SHOW, ewNoWait, ResultCode);
  // Handed over to by a Setup that waits for this one and shows its first page again itself (HandOver).
  if ExpandConstant('{param:ReturnToCaller|}') = '' then StartAgain(BackWhenDone);
end;

procedure CancelButtonClick(CurPageID: Integer; var Cancel, Confirm: Boolean);
begin
  // Closing after the uninstall or the hand-over, or from the first page where nothing has started: nothing to confirm.
  if Closing or ((MaintenancePage <> nil) and (CurPageID = MaintenancePage.ID)) then Confirm := False;
end;

// True when Folder is a folder of its own - not a link or junction, which a delete with administrator rights would
// otherwise reach through (DelTree removes one found inside without following it, but not the one it is given).
function IsPlainFolder(const Folder: String): Boolean;
var
  Attributes: DWORD;
begin
  Attributes := GetFileAttributes(Folder);
  Result := (Attributes <> $FFFFFFFF) and ((Attributes and $10) <> 0) and ((Attributes and $400) = 0);
end;

// Deletes Folder and everything in it; logs what it couldn't.
procedure RemoveFolder(const Folder: String);
begin
  if not DirExists(Folder) then Exit;
  if not IsPlainFolder(Folder) then
    Log('Not removing ' + Folder + ' - it is a link or junction.')
  else if DelTree(Folder, True, True, True) and not DirExists(Folder) then
    Log('Removed ' + Folder + '.')
  else
    Log('Could not remove all of ' + Folder + ' - something in it is in use.');
end;

// Deletes the passwords DNN Manager saved in Windows' Credential Manager: the generic credentials whose target starts
// with DnnManager/ (src\DnnManager.Infrastructure\Settings\WindowsCredentialStore.cs) - read from cmdkey /list, whose
// labels are in Windows' language but whose targets aren't. A target with anything but plain characters is left (and
// logged): it goes on cmdkey's command line.
procedure RemoveSavedCredentials;
var
  Output: TExecOutput;
  Line, Target: String;
  I, J, P, ResultCode: Integer;
  Plain: Boolean;
begin
  try
    if not ExecAndCaptureOutput(ExpandConstant('{sys}\cmdkey.exe'), '/list', '', SW_HIDE, ewWaitUntilTerminated,
      ResultCode, Output) or (ResultCode <> 0) then
    begin
      Log('Could not list the saved credentials (cmdkey exit code ' + IntToStr(ResultCode) + ').');
      Exit;
    end;
  except
    Log('Could not list the saved credentials: ' + GetExceptionMessage);
    Exit;
  end;
  for I := 0 to GetArrayLength(Output.StdOut) - 1 do
  begin
    // "    Target: LegacyGeneric:target=DnnManager/sql-sa" - the target is the word that holds "target=DnnManager/".
    Line := Trim(Output.StdOut[I]);
    P := Pos('target=DnnManager/', Line);
    if P = 0 then Continue;
    J := P;
    while (J > 1) and (Line[J - 1] <> ' ') do J := J - 1;
    Target := Copy(Line, J, Length(Line));
    Plain := True;
    for P := 1 to Length(Target) do
      if Pos(Target[P], 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789._-:=/@') = 0 then Plain := False;
    if not Plain then
    begin
      Log('Not removing the saved credential ' + Target + ' - its name has characters cmdkey can''t be given safely.');
      Continue;
    end;
    if Exec(ExpandConstant('{sys}\cmdkey.exe'), '/delete:' + Target, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0) then
      Log('Removed the saved credential ' + Target + '.')
    else
      Log('Could not remove the saved credential ' + Target + ' (cmdkey exit code ' + IntToStr(ResultCode) + ').');
  end;
end;

// After an uninstall with its window (never a silent one - the in-app move to Program Files and scripts keep everything):
// says what DNN Manager leaves behind, and offers to remove the user's DNN Manager data too - No unless chosen.
procedure OfferToRemoveData;
var
  Documents, ProgramData: String;
begin
  Documents := ExpandConstant('{userdocs}\DnnManager');
  ProgramData := ExpandConstant('{commonappdata}\DnnManager');
  if MsgBox('{#AppName} is removed. What it made for you is still here:' + #13#10#13#10 +
    '- ' + Documents + ': your settings (dnnmanager.db), logs, project backups (with copies of their databases) and ' +
    'deployment packages (with connection strings).' + #13#10 +
    '- Windows Credential Manager: the passwords DNN Manager saved (DnnManager/...).' + #13#10 +
    '- ' + ProgramData + ': DNN Manager''s temporary files.' + #13#10 +
    '- Docker: the SQL Server container (dnn-sqlserver, unless you renamed it) and its volume with your projects'' ' +
    'databases - remove them in Docker Desktop if you no longer need them.' + #13#10 +
    '- Your projects'' folders, IIS sites and hosts entries.' + #13#10#13#10 +
    'Also remove your DNN Manager data - ' + Documents + ', ' + ProgramData + ' and the saved passwords? ' +
    'The backups go with it, and it can''t be undone. Docker, your projects and IIS aren''t touched.',
    mbConfirmation, MB_YESNO or MB_DEFBUTTON2) <> IDYES then
  begin
    Log('The user''s DNN Manager data is kept.');
    Exit;
  end;
  RemoveFolder(Documents);
  // Machine-wide - only an uninstall for all users, which has administrator rights, removes it.
  if IsAdminInstallMode then RemoveFolder(ProgramData);
  RemoveSavedCredentials;
end;

// The uninstaller - started from Setup, or from Windows' Installed apps: once its "Are you sure" is answered, a running
// DNN Manager is closed, so its files can be removed. Still running (Cancel on Setup's question): the files in use are
// removed when Windows restarts. Once it has uninstalled, the sign-in task goes too, and - asked, with its window - the
// user's data.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if (CurUninstallStep = usUninstall) and IsDnnManagerRunning then
    CloseDnnManager(ExpandConstant('{app}'));
  if CurUninstallStep = usPostUninstall then
  begin
    RemoveStartupTask(ExpandConstant('{app}\{#AppExe}'));
    if not UninstallSilent then OfferToRemoveData;
  end;
end;
