---
name: documentation
description: >-
  Writes and maintains a project's documentation for the people who use the application and the developers who
  take it over: README, user guide, docs/ folder (configuration, troubleshooting, architecture, development,
  database, API, testing, deployment, security), ADRs, changelog, code comments and Mermaid diagrams - all verified
  against the code. Use when asked to document, write or update docs, a user guide or a README, set up or reorganize
  a docs folder, explain the architecture or data flow, draw diagrams, prepare a handover, write release notes, or
  when a code change affects documented behaviour, UI, architecture, configuration, APIs, the database, setup or
  deployment. Works for any language or framework.
---

# Documentation

Documentation is part of the implementation, not a final chore. It has two readers, kept in separate documents:
**users**, who must install, use and troubleshoot the application from the user guide without asking anyone; and
**developers** new to the project, who must run, understand, debug, extend and deploy it without the original
developer.

## Workflow

1. **Inspect the project** - structure, entry points, configuration, data stores, APIs, build, tests, deployment.
   Read the code the documentation will describe.
2. **Read the existing documentation** - README, docs/, changelog, comments - and list where it disagrees with the
   code. When they disagree, find out which is outdated; don't "fix" a doc to match a bug.
3. **Plan the structure** - what this project needs, where each topic lives, and what moves, merges or goes. See
   [doc-types.md](reference/doc-types.md) for the layout and what each document holds. Follow the project's
   existing layout and terminology where it works.
4. **Write or update** - one authoritative place per topic, linked from elsewhere; concrete commands, names and
   examples; tables for settings and endpoints.
5. **Add diagrams where a picture explains better** - [mermaid.md](reference/mermaid.md). Render-check them.
6. **Verify every fact against the code**: folder names, classes, routes, tables, configuration keys and defaults,
   commands, versions, behaviour. Never invent missing information - mark it unknown or ask.
7. **Remove what is outdated** - docs, sections, diagrams and links for things that no longer exist; fix every link
   to anything that moved.
8. **Final review** (below), then report what changed and what is still unknown.

## Rules

- **No filler** - no empty files, marketing language, vague statements, long introductions or duplicated text.
  Create only documents the project actually needs.
- **Never delete documentation the project has** to fit a layout - move its content, or keep it. Removing a document
  that is still true (a user guide, a runbook) needs the owner's say-so.
- **Single source of truth** - explain each thing once, link to it elsewhere.
- **One term per concept** across code, UI, API, database and docs.
- **README stays short** - what it is, why, quick start, structure in a sentence or diagram, links to the docs.
- **Explain why**, not only what, for important decisions (ADRs for decisions that are costly to reverse).
- **Never put real secrets in documentation** - placeholders only.
- **A wrong doc or diagram is worse than none** - update or delete it in the same change as the code.
- **Code comments** explain non-obvious decisions, workarounds, limitations and assumptions - never what the code
  plainly says. Match the surrounding comment style.
- **Keep the repository's file conventions** (encoding, line endings).

## Style

Clear headings, short paragraphs, concrete explanations, examples, tables and diagrams - written for developers new
to the project. Commands copy-pasteable. Paths and names exactly as in the code.

## Final review

- Can a user install, use and troubleshoot the application from the user guide alone?
- Can a new developer set up, run, test, build and deploy from the docs alone?
- Can they find where code for a feature lives, and where to add new code?
- Can someone debugging find the logs, settings and known problems?
- Does every name, command and diagram match the code today? Do all links work?
