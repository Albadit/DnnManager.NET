# DNN Manager - notes for Claude

## Skills

This repository has three skills in `.claude/skills/`. For every prompt, decide which of them the request needs, use
each one that applies, and name them in the first line of the reply (e.g. "Skills: software-engineering, documentation" or
"Skills: none").

| Skill | Use it when the prompt… |
|---|---|
| **software-engineering** | changes or reviews code: a feature, a bug fix, a refactor, UI/UX changes, performance, resource use, security, cleanup, an audit |
| **documentation** | writes or updates docs: README, `docs/`, changelog, diagrams, a handover - or a code change alters documented behaviour, configuration, setup or architecture |
| **release-notes** | writes or reviews the notes for a release (`docs/release-notes/vX.Y.Z.md`) - "the release notes for 1.7.0", "prepare the next release" - or redoes a release whose tag is made ("redo 1.7.6", a failed release workflow) |

software-engineering and documentation apply together when a code change touches something the docs describe. Neither applies to a quick question
or a one-off command.

## Project conventions

- Files are UTF-8 without BOM with CRLF line endings (`.editorconfig`).
- Build into a separate folder when the IDE may be building too: `dotnet build DnnManager.csproj --artifacts-path <temp folder>`.
- Fast tests: `dotnet test tests\DnnManager.IntegrationTests --filter "TestCategory!=Integration"`.
- The owner commits; don't commit or push unless asked. Commits and PRs carry no AI attribution (no
  `Co-Authored-By: Claude …`, no "Generated with Claude Code").
- Architecture, conventions and how to debug and release: [docs/development.md](docs/development.md).
