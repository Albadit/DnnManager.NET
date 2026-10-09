# DNN Manager - notes for Claude

## Skills

This repository has twelve skills in `.claude/skills/`. For every prompt, decide which of them the request needs, use
each one that applies, and name them in the first line of the reply (e.g. "Skills: functionality, documentation" or
"Skills: none").

**Code: one specialist per quality area.** A task that is mainly about one area loads that one skill; a review, an
audit or work that spans several areas goes through **orchestrator**, which scores the nine areas, runs the ones
that matter and merges their findings.

| Skill | Use it when the prompt… |
|---|---|
| **functionality** | builds a feature, fixes a bug, writes tests - business logic, edge cases, state, error paths |
| **security** | touches elevation, credentials, SQL, the hosts file, downloads and updates, file and zip handling |
| **reliability** | is about failure and recovery: undo/rollback, timeouts, retries, cancel and quit, partial state |
| **performance** | is about speed or resource use: UI-thread work, the monitor, logs, copies, memory |
| **usability** | changes the UI or its wording: flows, error messages, keyboard, contrast, screen readers |
| **maintainability** | refactors or cleans up: duplication, long methods, naming, tests, tech debt |
| **architecture** | changes structure: layers, abstractions, module boundaries, ADRs |
| **compliance** | handles personal data or secrets at rest: backups, exports, logs, the clipboard |
| **operations** | touches CI, the release scripts, the installer, self-update, logging or settings storage |
| **orchestrator** | asks for a review, an audit or "run all skills", or carries `#all`, `#eco`, `#sync`, `#fix`, `#deeper` or a dimension tag such as `#security` |

**Docs and releases.**

| Skill | Use it when the prompt… |
|---|---|
| **documentation** | writes or updates docs: README, `.docs/`, changelog, diagrams, a handover - or a code change alters documented behaviour, configuration, setup or architecture |
| **release-notes** | writes or reviews the notes for a release (`.docs/release-notes/vX.Y.Z.md`) - "the release notes for 1.7.0", "prepare the next release" - or redoes a release whose tag is made ("redo 1.7.6", a failed release workflow) |

A code skill and documentation apply together when a code change touches something the docs describe. None of them
applies to a quick question or a one-off command.

## Project conventions

- Files are UTF-8 without BOM with CRLF line endings (`.editorconfig`).
- Build into a separate folder when the IDE may be building too: `dotnet build DnnManager.csproj --artifacts-path <temp folder>`.
- Fast tests: `dotnet test tests\DnnManager.IntegrationTests --filter "TestCategory!=Integration"`.
- The owner commits; don't commit or push unless asked.
- Architecture, conventions and how to debug and release: [.docs/development.md](.docs/development.md).
