# ADR 0004 - What the user's account can change goes through a few checked choke points

**Status:** accepted (2026-10)

## Context

DNN Manager is a desktop app that runs elevated, as the same user who signs in.
That user's everyday programs run without administrator rights - and they can
change a lot of what DNN Manager reads and acts on:

- its settings (`Documents\DnnManager\dnnmanager.db`) and the user's environment
  variables (`DNNMANAGER_*`, but also .NET's own: a profiler, a startup hook, a
  diagnostic port);
- the folders it writes, unpacks and deletes in: `%TEMP%`, Documents, the
  projects folder - and a site's folder, which its app pool can write too;
- the programs it starts: anything on the user's PATH or in a per-user install;
- a site's `web.config`, which names the database DNN Manager drops or signs in
  to, and is XML the site can change.

Each of these is a way for a program without administrator rights to get them:
a junction under a folder DNN Manager deletes, a `docker.exe` earlier on the
PATH, a `CORECLR_PROFILER` that loads a DLL into the elevated process, a
projects folder set to `C:\Windows`. Up to 1.8.1 the checks were made at call
sites, one by one, and each new feature had to remember them.

## Options

1. **A check at each call site** - as before. Cheap to start, but nothing tells a
   new call site it needs one, and the checks drift apart.
2. **A few choke points, each enforced by tests** - one class per kind of input
   that every use goes through, and tests that fail when code goes around them.
3. **A broker split**: a small elevated service that does only the
   administrator work, with a UI that runs as the user. The cleanest boundary,
   but a second process and a protocol between them, an installer service, and
   most of the app rewritten.

## Decision

Option 2. The choke points:

- **Settings and environment**:
  [`SettingRules`](../../src/DnnManager.Application/Configuration/SettingRules.cs) holds
  every value that feeds an administrator action (the projects folder, release
  sources, the hostname suffix, the collation, Docker names, IIS feature names),
  for the saved settings (`UserSettings.Validate`) and the `DNNMANAGER_*`
  overrides (`AppOptions.Problems`) alike. A saved value that breaks a rule goes
  back to its default; overrides that break one are all ignored.
- **Folders others can write**:
  [`SafePath`](../../src/DnnManager.Application/SafePath.cs) (is a path inside a
  folder, links on the way, deleting a tree) and
  [`SafeZip`](../../src/DnnManager.Infrastructure/Files/SafeZip.cs) (every zip
  entry unpacked) are the only ways to write, extract or delete there, on top of
  Windows' redirection trust for the process
  ([`RedirectionTrust`](../../src/DnnManager.Infrastructure/Files/RedirectionTrust.cs)).
  A site's XML is read through
  [`SiteXml`](../../src/DnnManager.Infrastructure/WebConfigs/SiteXml.cs) (no DTD).
- **Starting a program elevated**: only
  [`ProcessRunner`](../../src/DnnManager.Infrastructure/Processes/ProcessRunner.cs)
  and [`ElevatedStart`](../../src/DnnManager.Infrastructure/Processes/ElevatedStart.cs),
  which take only a copy administrators alone can change
  (`TrustedPrograms.Resolve`) and give it
  [`ChildEnvironment`](../../src/DnnManager.Infrastructure/Processes/ChildEnvironment.cs)'s
  environment. A program for the user starts as the user.
- **DNN Manager's own start**: the launcher
  ([`src/DnnManager.Launcher`](../../src/DnnManager.Launcher/DnnManager.Launcher.csproj)),
  a Native AOT exe that drops .NET's variables before it starts `DnnManager.exe` -
  what the sign-in task and the UAC relaunch start.
- **Terminals**: without administrator rights by default (a restricted copy of
  DNN Manager's token); an Administrator terminal only when asked for, with
  shells installed for all users and no profile scripts.
- **The tests**: `LayeringTests` scans the source and fails on a `Process.Start`
  or `new ProcessStartInfo` outside the allowed files, and on a Presentation use
  of an Infrastructure namespace not on its list; `ElevationBoundaryTests`,
  `SecurityHardeningTests`, `SettingsResilienceTests` and
  `LauncherEnvironmentTests` hold the rules themselves ([testing.md](../testing.md)).

## Consequences

- A new feature that deletes, unpacks, starts a program or reads a setting gets
  the checks by using the one class there is for it; one that goes around
  `ProcessRunner` / `ElevatedStart` fails the fast tests.
- The source scan is textual, like the layering test: it doesn't see a process
  started through reflection or a library. Code review still has to look.
- Residual risks are accepted and listed under
  [Known open risks](../security.md#known-open-risks): the portable exe has no
  launcher and extracts its native DLLs into `%TEMP%\.net`; the SQL container's
  `sa` password is in its environment and SqlPackage's passwords on its command
  line; an Administrator terminal runs what is typed with those rights; an
  unelevated terminal runs in the elevated sign-in session.
- **The installer's default follows from it**: Setup installs for all users, into
  Program Files, where only administrators can change the exe that runs elevated -
  and the sign-in task is made only for such an installation. A per-user
  installation (`/CURRENTUSER`, or one made by 1.8.1 and older) keeps working,
  with a warning, and is offered the move.
- Option 3 (a broker) stays open as the stronger boundary. The choke points are
  where its interface would be cut.
