# Changelog

All notable changes to DnnManager.NET are documented here.

## v1.0.2 - 2026-09-03

A hardening and performance patch. No new features and no configuration
changes: existing projects, `appsettings.json` and `connections.json` all keep
working as they are.

### Fixed

- **Project names are now validated.** The name typed at "Setup a new DNN
  project" and "Clone a DNN project" was taken as free text and used to build a
  folder under `BaseDirectory`, an IIS site and application pool, a host header
  and a database name. A name containing `..` or a path separator resolved
  *outside* `C:\DNN` - and removing that project deletes the resolved path
  recursively. Names are now restricted to a single safe path segment (letters,
  digits, `-`, `_`, `.`), and both screens re-prompt on a bad name instead of
  failing later in the run. Existing names such as `metro_test` are unaffected.
- **Database names are quoted before reaching SQL Server.** They were
  interpolated straight into T-SQL, and the name a site actually uses is read
  out of its `web.config` - so it is not necessarily one this tool created.
- **Cancelling no longer orphans child processes.** Pressing Ctrl+C abandoned
  only the wait, leaving `docker`, `sqlcmd` and `powershell` running with
  container locks and file handles still held. The process tree is now stopped.
- **`connections.json` can no longer be lost.** It was rewritten in place, so a
  crash partway through truncated every saved credential; and an unreadable file
  silently started an empty store that the next save overwrote. Saves are now
  write-then-rename, and a damaged file is kept aside as `connections.json.corrupt`.
- **The FTP connection is always closed**, not just when a clone succeeds.
- The directory-size total for a project no longer collapses to 0 MB because of
  one unreadable subfolder.

### Performance

- **Project listing is faster.** "Show all projects info" built two
  `ServerManager` instances per project - each one loading IIS's
  `applicationHost.config` - and sized every site serially with one file-system
  call per file. It now takes a single IIS snapshot for all sites and sizes the
  projects in parallel, reading sizes from the directory scan itself. Measured
  on three real sites: ~60 ms down to ~32 ms warm, with identical totals.
- **Copying website files is faster**, most noticeably on large sites. Every
  single file triggered a `CreateDirectory` call and repainted a full-width
  console progress line - on an 18,000-file site the progress display cost more
  than the copy. Folders are now created once each and progress refreshes about
  ten times a second, in both the local-folder and FTP paths.

### Changed

- The build now stamps a version, so `dnnmgr.exe` reports 1.0.2 in its file
  properties instead of 1.0.0.0.

## v1.0.1

- Added `.gitignore` and the zip task to `.vscode/tasks.json`.

## v1.0.0

- First tagged release: VS Code tasks for building, cleaning and publishing.
