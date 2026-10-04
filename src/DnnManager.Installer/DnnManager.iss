; DNN Manager - Windows installer (Inno Setup 6).
;
; Build it with src\DnnManager.Installer\build.ps1, which publishes the app and passes the defines below:
;   AppVersion  the newest version tag, or -Version     (required)
;   FileVersion four-part file version, e.g. 1.7.0.0    (default AppVersion)
;   PublishDir  the self-contained publish output       (default bin\app)
;   OutputDir   where Setup is written                  (default ..\..\publish)
;   ImagesDir   wizard images made from the app icon    (optional)
;
; Installs per user, without administrator rights, into %LOCALAPPDATA%\Programs\DnnManager (like the VS Code
; user installer); run Setup with /ALLUSERS to install for all users into Program Files instead. The user's
; settings live in Documents\DnnManager, which Setup never writes to, so upgrading, reinstalling or
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
#define AppPublisher "Bond for web solutions"
#define AppUrl "https://github.com/Bond-for-web-solutions/DnnManager.NET"
; Identifies the installation for upgrades and uninstall - never change it.
#define AppGuid "AD68C57A-D887-4297-A905-B6F28C1D66D1"

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

; Per-user install without elevation; /ALLUSERS on the command line installs for everyone (asks for admin).
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=commandline
DefaultDirName={autopf}\DnnManager
; An upgrade goes where the previous version is, without asking again.
UsePreviousAppDir=yes
DisableDirPage=auto
DisableProgramGroupPage=yes
DisableWelcomePage=yes
LicenseFile=..\..\LICENSE
ShowLanguageDialog=no

ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0

; DNN Manager creates this mutex while it runs (src\DnnManager.Presentation\RunningMarker.cs), so Setup and the
; uninstaller ask to close it first - it runs elevated, so Setup couldn't close it itself.
AppMutex=DnnManager.NET.Running
; A Setup started by an older one (/HandedOver=1) has a mutex of its own: the older one may not have exited yet.
SetupMutex=DnnManager.NET.Setup{param:HandedOver|}

WizardStyle=modern
SetupIconFile=..\DnnManager.Presentation\Assets\dnn.ico
#ifdef ImagesDir
WizardImageFile={#ImagesDir}\wizard-100.bmp,{#ImagesDir}\wizard-200.bmp
WizardSmallImageFile={#ImagesDir}\wizard-small-100.bmp,{#ImagesDir}\wizard-small-200.bmp
#endif
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExe}

OutputDir={#OutputDir}
OutputBaseFilename=DnnManagerSetup-{#AppVersion}-x64
Compression=lzma2/max
SolidCompression=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Messages]
UninstalledAll=%1 was successfully removed from your computer.%n%nYour settings in Documents\DnnManager were kept - delete that folder to remove them too.

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[InstallDelete]
; Up to 1.3.0 the program was dnnmanager.exe. Windows keeps a file name's old casing when it is overwritten, so
; it is deleted first and the upgrade really installs DnnManager.exe.
Type: files; Name: "{app}\dnnmanager.exe"

[Files]
; The app is a self-contained single-file publish (DnnManager.exe plus a few native DLLs) - no .NET install needed.
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
; The app asks for administrator rights itself (UAC) when it starts, as it does from the shortcuts.
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; "Start DNN Manager when you sign in" (Settings - General) is a scheduled task - it goes with the app. Best effort:
; nothing happens when there is no task, or when this uninstaller may not delete it.
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /TN ""DNN Manager"" /F"; Flags: runhidden; RunOnceId: "RemoveStartupTask"

[Code]
// What Setup installs comes from GitHub:
// - a new install: the newest release;
// - DNN Manager installed: the first page offers "Update to version X" (when GitHub has a newer release), "Repair",
//   which installs the installed version again, and "Uninstall", which runs the installed uninstaller.
// Setup installs the version it carries itself; any other version it downloads from GitHub (that release's Setup,
// checked against GitHub's SHA-256 and its version) and hands over to it with /HandedOver=1 - the new Setup skips
// the pages already answered here - and this Setup closes. Silent runs (the in-app update) install what they carry.
// Offline: the newest version is the one this Setup carries, and when a download fails Setup offers to install that.
const
  UninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{' + '{#AppGuid}' + '}_is1';
  ReleasesApi = 'https://api.github.com/repos/Bond-for-web-solutions/DnnManager.NET/releases/';

var
  MaintenancePage: TInputOptionWizardPage;
  DownloadPage: TDownloadWizardPage;
  InstalledVersion: String;
  InstalledUninstaller: String;
  // GitHub's newest release - or this Setup's version, when that is newer or GitHub couldn't be asked (Offline).
  NewestVersion: String;
  Offline: Boolean;
  // The maintenance page's options; UpdateOption is -1 when there is no newer release.
  UpdateOption, UninstallOption: Integer;
  Closing: Boolean;

function IsInstalled: Boolean;
begin
  Result := InstalledUninstaller <> '';
end;

// Started by an older Setup that downloaded this one.
function IsHandedOver: Boolean;
begin
  Result := ExpandConstant('{param:HandedOver|}') <> '';
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

function InitializeSetup: Boolean;
var
  Value: String;
begin
  // HKA is HKCU for a per-user Setup and HKLM for /ALLUSERS - the installation this Setup would replace.
  if RegQueryStringValue(HKA, UninstallKey, 'UninstallString', Value) and FileExists(RemoveQuotes(Value)) then
  begin
    InstalledUninstaller := RemoveQuotes(Value);
    if not RegQueryStringValue(HKA, UninstallKey, 'DisplayVersion', InstalledVersion) then
      InstalledVersion := '';
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

procedure InitializeWizard;
var
  OfflineNote: String;
begin
  DownloadPage := CreateDownloadPage('Downloading {#AppName}',
    'The version to install is downloaded from GitHub - Setup then continues with it.', nil);
  DownloadPage.ShowBaseNameInsteadOfUrl := True;

  // Started by an older Setup: what to do was chosen there.
  if not IsInstalled or IsHandedOver then Exit;

  if Offline then
    OfflineNote := #13#10#13#10 + 'GitHub can''t be reached - Setup installs the version it carries, {#AppVersion}.';
  MaintenancePage := CreateInputOptionPage(wpWelcome,
    '{#AppName} is already installed',
    'Choose what to do with the installed version.',
    '{#AppName} ' + RepairVersion + ' is installed in:' + #13#10 + ExtractFileDir(InstalledUninstaller) + #13#10#13#10 +
      'Your settings in Documents\DnnManager are kept either way.' + OfflineNote,
    True, False);
  UpdateOption := -1;
  if IsNewer(NewestVersion, InstalledVersion) then
    UpdateOption := MaintenancePage.Add('Update to version ' + NewestVersion);
  MaintenancePage.Add('Repair - install version ' + RepairVersion + ' again');
  UninstallOption := MaintenancePage.Add('Uninstall {#AppName}');
  MaintenancePage.SelectedValueIndex := 0;
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  // The license was accepted when it was first installed, or in the older Setup that started this one.
  Result := (PageID = wpLicense) and (IsInstalled or IsHandedOver);
end;

// Downloads that version's Setup from GitHub, checks it, starts it and closes this Setup. Raises an exception with
// the reason when it can't.
procedure HandOver(const Version: String);
var
  Json, AssetName, Digest, Sha256, Downloaded, Folder, NewSetup, FileVersion, Mode: String;
  P, ResultCode: Integer;
begin
  // GitHub's SHA-256 of the release's Setup ("digest", in its asset - before its browser_download_url).
  Json := GitHubRelease('tags/v' + Version);
  AssetName := 'DnnManagerSetup-' + Version + '-x64.exe';
  P := Pos('"name":"' + AssetName + '"', Json);
  if P = 0 then RaiseException('Release ' + Version + ' on GitHub has no ' + AssetName + '.');
  Json := Copy(Json, P, Length(Json));
  P := Pos('"browser_download_url"', Json);
  if P > 0 then Json := Copy(Json, 1, P);
  Digest := JsonString(Json, 'digest');
  if CompareText(Copy(Digest, 1, 7), 'sha256:') = 0 then Sha256 := Lowercase(Copy(Digest, 8, 64));

  DownloadPage.Clear;
  DownloadPage.Add('{#AppUrl}/releases/download/v' + Version + '/' + AssetName, AssetName, Sha256);
  DownloadPage.Show;
  try
    DownloadPage.Download;
  finally
    DownloadPage.Hide;
  end;

  Downloaded := ExpandConstant('{tmp}\' + AssetName);
  if not GetVersionNumbersString(Downloaded, FileVersion) or (FileVersion <> Version + '.0') then
    RaiseException(AssetName + ' is not version ' + Version + ' (it reports ' + FileVersion + ').');

  // Out of {tmp}, which goes when this Setup closes - into the in-app update's folder, which DNN Manager clears.
  Folder := AddBackslash(GetTempDir) + 'DnnManager-update\' + Version;
  NewSetup := Folder + '\' + AssetName;
  if not ForceDirectories(Folder) or not FileCopy(Downloaded, NewSetup, False) then
    RaiseException('Could not copy the download to ' + Folder + '.');

  if IsAdminInstallMode then Mode := '/ALLUSERS' else Mode := '/CURRENTUSER';
  if not Exec(NewSetup, '/SP- /HandedOver=1 ' + Mode, '', SW_SHOW, ewNoWait, ResultCode) then
    RaiseException('Could not start ' + NewSetup + ': ' + SysErrorMessage(ResultCode));
  Log('Started ' + NewSetup + ' - this Setup closes.');
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
    // Waits until the uninstall has finished; the uninstaller asks for confirmation itself.
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
  Closing := True;
  WizardForm.Close;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  // A new install: the newest version.
  if (CurPageID = wpLicense) and not IsInstalled then
  begin
    Result := InstallVersion(NewestVersion);
    Exit;
  end;
  if (MaintenancePage = nil) or (CurPageID <> MaintenancePage.ID) then Exit;

  if MaintenancePage.SelectedValueIndex = UninstallOption then
  begin
    RunUninstaller;
    Result := False;
  end
  else if MaintenancePage.SelectedValueIndex = UpdateOption then
    Result := InstallVersion(NewestVersion)
  else
    Result := InstallVersion(RepairVersion);
end;

procedure CancelButtonClick(CurPageID: Integer; var Cancel, Confirm: Boolean);
begin
  // Closing after the uninstall or the hand-over: nothing to confirm.
  if Closing then Confirm := False;
end;
