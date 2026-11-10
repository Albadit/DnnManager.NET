# ADR 0005 - Deployments compare through a baseline, never by time

**Status:** accepted (2026-11)

## Context

The Source Control page pushes a project's files to a server and pulls changes made on
the server back - both ways, file by file. To offer the right action it has to
tell, for each file, *which side changed*. Two obvious answers are wrong:

- **The newer time wins.** A server's clock and the PC's don't agree, FTP lists
  times to the minute or the day, and an upload sets the server file's time to
  when it arrived. "Newer" would push a stale file over a hotfix made on the
  live site, or pull the server's copy over the user's work.
- **Different means "push".** Two differing files say nothing about which one is
  the change - a file edited on the server looks exactly like one edited here.

The question is the one git answers with a merge base.

## Decision

- Every connection has a **baseline** (`deploy_baseline`): per file, the content
  (SHA-256, size) the project and the server last agreed on. A side has changed
  when its content differs from the baseline's;
  [`SyncComparer`](../../src/DnnManager.Application/Deployment/SyncComparer.cs)
  turns the three states into push, pull, conflict or synchronized. A file the
  same on both sides is synchronized whatever the baseline says.
- The baseline moves only when both sides agree: after a successful push or
  pull, a resolved conflict (to the server's content - what is left is a push),
  or a scan finding a file identical on both sides. A failed or cancelled
  operation leaves it as it was, and puts the files back
  ([ADR 0001](0001-undo-on-failure.md)).
- **Reading only what changed** without trusting times across machines: the
  baseline also keeps how the *server* listed the file when they agreed (size
  and time, `remote_size` / `remote_modified_utc`) - valid only while the
  server holds that content. The same listing again means the same content; a
  size that differs from both the project's and the baseline's means a change;
  anything else is hashed - by the server (`sha256sum`, FTP's `HASH`) when it
  can, otherwise read. The project's hashes are kept with their size and time
  (`deploy_local_hashes`) and read again only when those change.
- Every write is **checked first** (still as the scan saw it), **backed up**
  into a content-addressed snapshot store, then applied - and recorded in the
  history with each file's hashes before and after and its baseline before, so
  a rollback can put contents *and* baseline back.

## Consequences

- A push never overwrites a change made on the server, and a pull never a
  change made in the project, unless the user resolves the conflict - which
  happens in the project, so the server changes only by a push.
- The first comparison of a project and a server reads the server's files of the
  same size as the project's once (unless the server hashes them itself); after
  that, only what changed.
- A change on the server that keeps a file's size and its listed time - possible
  only where the server lists times coarsely (FTP without `MLSD`, to the minute)
  and within that minute of the last sync - goes unseen until the file changes
  again. SFTP lists seconds.
- Deleting the baseline (removing the connection) makes the next comparison a
  first one: files that differ become conflicts, since nothing says which side
  changed.
