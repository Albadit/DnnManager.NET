# ADR 0006 - macOS and Linux through Avalonia, beside the WPF app

**Status:** accepted (2026-11)

## Context

DNN Manager was one WPF project for Windows. Users on macOS and Linux want it too -
above all Source Control, which only talks to FTP and SFTP servers and a folder of
files. Three things stood in the way:

- **WPF is Windows only.** Its 26,000 lines of interface (and 7,000 of XAML) can't
  run anywhere else.
- **DNN needs IIS.** DNN Platform runs on .NET Framework 4.8 in IIS - Windows only,
  with no container for macOS or Linux either. Making and running sites can't
  move, whatever the interface.
- **The layers were in the WPF assembly**, compiled as `net10.0-windows`, with
  Windows' DPAPI (secrets), registry and services used freely.

The interface had to be free to build and ship: no paid UI toolkit, no paid
signing.

## Decision

- **Avalonia** (MIT) for the cross-platform interface: closest to WPF (XAML,
  bindings, styles), one codebase for Windows, macOS and Linux, drawn with Skia,
  testable headless. .NET MAUI has no Linux desktop; Uno and Electron were further
  from the existing code.
- **The layers become libraries for every OS.** `src/DnnManager.Core` (`net10.0`)
  compiles Domain, Application and Infrastructure from their folders, unmoved;
  `src/DnnManager.Ui` holds what both interfaces share and neither draws - the
  activity log and Output's model, the operation runner, the live settings - with
  the UI thread handed to it (`UiThread.Post`). The WPF app references both and
  keeps working as it did; its tests are unchanged.
- **The cross-platform app is a new project**, `src/DnnManager.Desktop`, beside the
  WPF app - not a rewrite of it in place. It starts with Source Control and
  Settings; the other pages move over one at a time. While both exist, each page is
  in one place of record: the WPF app's for what isn't moved yet.
- **What only Windows has is said, not hidden or faked.**
  [`Application/Platform.cs`](../../src/DnnManager.Application/Platform.cs) says
  which features the system has and why not (`Platform.WhyNot`); the pages that
  need IIS say it and point to a Windows VM. Core keeps the Windows-only code and
  builds it everywhere (CA1416 is off there, documented in the project); code that
  macOS and Linux run checks `OperatingSystem.IsWindows()` itself.
- **Secrets everywhere, free:** DPAPI on Windows as before; elsewhere AES-256-GCM
  with a per-user key file of mode 600 in the user's configuration folder
  ([`UserCipher`](../../src/DnnManager.Infrastructure/Settings/UserCipher.cs)).
  Values carry their scheme (`dpapi:`, `aes:`), so either reads what it wrote.
- **Shipping, free:** self-contained single-file programs from CI; the macOS app
  signed ad hoc on a macOS runner (Apple Silicon needs a signature; ad hoc needs no
  account), not notarized - the first start is Right-click → Open.

## Consequences

- Two interfaces for a while: a change to a page that both have is made in both.
  The aim is one - the Avalonia one - once every page is moved; the WPF app then
  goes, and with it the duplication.
- The shared database and settings mean the two apps see the same servers and
  history; a database written by the newer app is read by the older one as ever
  (the migrations, `AppDatabase`).
- CA1416 doesn't warn in Core: a Windows-only call reachable on macOS or Linux is
  found by the tests (which CI runs on all three) and by review, not the compiler.
- macOS users see Gatekeeper's warning on first start, until the app is notarized
  (an Apple Developer account, paid).
