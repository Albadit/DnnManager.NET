; DNN Manager - Windows installer (Inno Setup 6).
;
; Build it with installer\build.ps1, which publishes the app and passes the defines below:
;   AppVersion  version from DnnManager.csproj          (required)
;   PublishDir  the self-contained publish output       (default bin\app)
;   OutputDir   where Setup is written                  (default ..\publish)
;   ImagesDir   wizard images made from the app icon    (optional)
;
; Installs per user, without administrator rights, into %LOCALAPPDATA%\Programs\DNN Manager (like the VS Code
; user installer); run Setup with /ALLUSERS to install for all users into Program Files instead. The user's
; settings live in Documents\DNN Manager, which Setup never writes to, so upgrading, reinstalling or
; uninstalling keeps them.

#ifndef AppVersion
  #error AppVersion is not defined - build with installer\build.ps1 (or pass /DAppVersion=x.y.z to ISCC).
#endif
#ifndef PublishDir
  #define PublishDir "bin\app"
#endif
#ifndef OutputDir
  #define OutputDir "..\publish"
#endif

#define AppName "DNN Manager"
#define AppExe "dnnmgr.exe"
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
VersionInfoVersion={#AppVersion}
VersionInfoProductName={#AppName}
VersionInfoDescription={#AppName} Setup

; Per-user install without elevation; /ALLUSERS on the command line installs for everyone (asks for admin).
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=commandline
DefaultDirName={autopf}\{#AppName}
; An upgrade goes where the previous version is, without asking again.
UsePreviousAppDir=yes
DisableDirPage=auto
DisableProgramGroupPage=yes
DisableWelcomePage=yes
LicenseFile=..\LICENSE
ShowLanguageDialog=no

ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0

; DNN Manager creates this mutex while it runs (src\DnnManager.Presentation\RunningMarker.cs), so Setup and the
; uninstaller ask to close it first - it runs elevated, so Setup couldn't close it itself.
AppMutex=DnnManager.NET.Running
SetupMutex=DnnManager.NET.Setup

WizardStyle=modern
SetupIconFile=..\src\DnnManager.Presentation\Assets\dnn.ico
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
UninstalledAll=%1 was successfully removed from your computer.%n%nYour settings in Documents\DNN Manager were kept - delete that folder to remove them too.

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; The app is a self-contained single-file publish (dnnmgr.exe plus a few native DLLs) - no .NET install needed.
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
; The app asks for administrator rights itself (UAC) when it starts, as it does from the shortcuts.
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[Code]
// When DNN Manager is already installed, the first page asks what to do: repair (or update / install this
// version over it) or uninstall. Uninstall runs the installed uninstaller and closes Setup.
const
  UninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{' + '{#AppGuid}' + '}_is1';
  ActionUninstall = 1;

var
  MaintenancePage: TInputOptionWizardPage;
  InstalledVersion: String;
  InstalledUninstaller: String;
  ClosingAfterUninstall: Boolean;

function IsInstalled: Boolean;
begin
  Result := InstalledUninstaller <> '';
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
      InstalledVersion := '?';
  end;
  Result := True;
end;

// Below zero: the installed version is older than this Setup's; zero: the same; above zero: newer.
function CompareInstalledVersion: Integer;
var
  Installed, This: Int64;
begin
  if StrToVersion(InstalledVersion, Installed) and StrToVersion('{#AppVersion}', This) then
    Result := ComparePackedVersion(Installed, This)
  else if InstalledVersion = '{#AppVersion}' then
    Result := 0
  else
    Result := -1;
end;

procedure InitializeWizard;
var
  Compared: Integer;
  KeepLabel: String;
begin
  if not IsInstalled then Exit;

  MaintenancePage := CreateInputOptionPage(wpWelcome,
    '{#AppName} is already installed',
    'Choose what to do with the installed version.',
    '{#AppName} ' + InstalledVersion + ' is installed in:' + #13#10 + ExtractFileDir(InstalledUninstaller) + #13#10#13#10 +
      'Your settings in Documents\{#AppName} are kept either way.',
    True, False);

  Compared := CompareInstalledVersion;
  if Compared = 0 then
    KeepLabel := 'Repair - install version {#AppVersion} again'
  else if Compared < 0 then
    KeepLabel := 'Update to version {#AppVersion}'
  else
    KeepLabel := 'Install version {#AppVersion} over the newer ' + InstalledVersion;
  MaintenancePage.Add(KeepLabel);
  MaintenancePage.Add('Uninstall {#AppName}');
  MaintenancePage.SelectedValueIndex := 0;
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  // The license was accepted when it was first installed.
  Result := (PageID = wpLicense) and IsInstalled;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  ResultCode: Integer;
begin
  Result := True;
  if (MaintenancePage = nil) or (CurPageID <> MaintenancePage.ID) or
     (MaintenancePage.SelectedValueIndex <> ActionUninstall) then
    Exit;

  Result := False;
  WizardForm.Hide;
  try
    // Waits until the uninstall has finished; the uninstaller asks for confirmation itself.
    if not Exec(InstalledUninstaller, '', '', SW_SHOW, ewWaitUntilTerminated, ResultCode) then
    begin
      MsgBox('Could not start the uninstaller:' + #13#10 + SysErrorMessage(ResultCode), mbError, MB_OK);
      Exit;
    end;
  finally
    WizardForm.Show;
  end;

  // Still there: the uninstall was cancelled - stay on the page.
  if FileExists(InstalledUninstaller) then Exit;
  ClosingAfterUninstall := True;
  WizardForm.Close;
end;

procedure CancelButtonClick(CurPageID: Integer; var Cancel, Confirm: Boolean);
begin
  if ClosingAfterUninstall then Confirm := False;
end;
