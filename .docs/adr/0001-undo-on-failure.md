# ADR 0001 - A failed operation is undone, like a cancelled one

**Status:** accepted (2026-10)

## Context

Operations that make something - New project, Clone, Import, Host project - make it
in steps: a folder, an IIS site and app pool, a database, a login, files written
into the site. Each step notes in [`OperationUndo`](../../src/DnnManager.Application/UseCases/OperationUndo.cs)
how to take back what it is about to make, before it starts. Until 1.8.0 that
journal was only run when the user pressed **Cancel**. An operation that *failed* -
GitHub unreachable during the download, the SQL Server down, an IIS error - left
what it had made so far: an empty project folder, a site without a database, a
database without a site. The next attempt under the same name was refused ("a
project named … already exists"), and the user had to find and remove the
leftovers by hand. Quitting while an operation ran cut it off without running the
journal at all.

## Decision

- [`OperationRunner`](../../src/DnnManager.Presentation/Services/OperationRunner.cs) runs the
  operation's journal after a **failure** (a failed `Result` or an exception) as it
  does after a cancel, and says in the result whether everything was put back.
- A use case that wants what it made kept after a failure says so with
  `OperationUndo.Keep()` before failing. New project does this when DNN's own
  installation fails: the folder, site and database are left to look into, as the
  user guide has always said.
- An undo step that itself makes or loses something (Restore, run by an upgrade's
  undo) doesn't add to the journal while it runs.
- Quitting while an operation runs cancels it and closes the window only once it
  has stopped and undone what it did (`OperationRunner.WhenIdleAsync`). Setup, which
  is waiting to replace the files, gives it 20 seconds.

## Consequences

- A failure leaves the PC as it was - the user can try again at once, under the
  same name.
- What can't be taken back (a database that was replaced, files copied over a
  folder that was there) is said in the Output tab, after a failure as after a
  cancel.
- Quitting can take a while: an operation that ignores its cancel (Restore, once it
  has started changing the site) runs to its end first.
- Options considered: *keep everything for inspection* (the old behaviour - it
  blocked retrying and left orphans nobody cleaned up) and *undo only before the
  install step* (two rules to explain instead of one, with `Keep()` covering the
  case where leftovers help).
