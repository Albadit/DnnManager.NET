# Documentation

Documentation is part of the implementation. Update it whenever architecture, functionality, configuration, data
structure, APIs, setup, deployment or important behaviour changes - in the same piece of work.

## Structure

A typical layout - create only what the project needs, never empty or filler files. Documentation has two
audiences: the people who **use** the application and the developers who **build** it; keep their documents apart.

```text
README.md              what it is, quick start, links (for both audiences)
CHANGELOG.md           user-visible changes per release
docs/
  user-guide.md        for users: installing, every screen, menu and action, workflows, limitations
  configuration.md     every setting (users and administrators)
  troubleshooting.md   symptom → cause → fix entries (users and developers)
  architecture.md      components, boundaries, data flow, decisions (with diagrams)
  development.md       prerequisites, setup, build, run, debug, conventions
  database.md          tables, ownership, relationships, migrations
  api.md               endpoints beyond what generated docs say
  testing.md           test types, commands, external dependencies
  deployment.md        build, release, deploy, rollback, verification (releasing.md for a shipped app)
  security.md          sensitive areas and how they are protected
  decisions/           ADRs (NNNN-title.md)
```

Small projects can merge these (e.g. development + testing + deployment in one file). Follow the project's
existing layout if it has one. **Never delete a document because this list doesn't name it** - move its content or
keep it; a user guide, an operations runbook or an integration guide is as valid as the files above.

## User guide

For the people who use the application - an app with a UI or a tool people run needs one; a library or a pure API
may not (its README and API docs serve the users). Written in the users' words, from what they see:
- installing, upgrading and uninstalling; first start
- every screen, page, menu and action: what it does, what it changes, what it asks first
- the main workflows end to end (e.g. create, import, back up, remove)
- what happens to their data (where it is kept, what an action deletes, what is kept)
- limitations and known behaviour that surprises people
Link to configuration and troubleshooting rather than repeating them; keep implementation details for the developer
docs. Use the UI's exact labels, in **bold**.

## README

Concise: what the application is and the problem it solves, main technologies, a sentence or diagram of the
architecture, prerequisites, setup, run, build, test, the important folders, and links to the detailed docs. Not the
whole manual.

## Architecture

Major components and their responsibilities, dependencies and boundaries, integrations, data flow, authentication and
authorization flow, configuration, background processes, caching - and **why** the important decisions were made.

**Folder structure**: for important folders, what belongs there, what doesn't, and when to add code there. Don't list
every obvious file.

**Data flows**: important end-to-end flows (e.g. `User → UI → API → Service → Database → Response → UI`), marking
where validation, authorization, business rules, transformations and error handling happen.

## Database

Important tables, relationships, ownership, constraints, indexes, migrations, delete and synchronization behaviour.
Clearly separate **application-owned** data from **external-system** data and mark read-only data.

## API

For important endpoints: route, method, purpose, permissions, parameters, request and response format, important
errors. Don't duplicate generated API docs unless adding context.

## Configuration

For every important setting: name, purpose, default, required or optional, where it is set, whether a restart is
needed, whether it is sensitive. Never real secrets - use placeholders.

## Development

Enough to start from the docs alone: prerequisites and versions, database requirements, environment variables, setup,
build, run and test commands, how to debug (including elevated or containerized processes), where logs are, and
project conventions (encoding, line endings, threading rules, SQL and process rules, secret handling).

## Testing

Test types and locations, commands, which tests need external systems, which are safe to run locally, required test
data, and how environment-dependent tests behave when the environment is missing.

## Deployment / releasing

Build process and artifacts, configuration, migrations, deployment order, rollback, health checks, permissions,
post-deployment verification, versioning and changelog steps. Deployment must not depend on one person's memory.

## Troubleshooting entries

```markdown
### <Symptom as the user sees it>
- **Likely cause:** …
- **Investigate:** where to look (log file, setting, command) and what to look for
- **Fix:** concrete steps
- **Verify:** how to confirm it is fixed
```

## Security

Authentication, authorization, roles and permissions, secrets handling, database access, external systems, admin
functionality, what runs with elevated rights and why.

## External systems

For each integration: purpose, owner, read-only or read/write, authentication, configuration, failure behaviour, and
the component responsible for it.

## Architecture Decision Records

For decisions that are important, non-obvious or costly to reverse - not for implementation details.

```markdown
# NNNN. <Decision title>
- **Status:** accepted | superseded by NNNN
- **Context:** the situation and forces
- **Problem:** what had to be decided
- **Options considered:** each with its main pros/cons
- **Decision:** what was chosen
- **Reasoning:** why this option
- **Consequences:** what becomes easier, harder, or must now be done
```

## Style

Write for competent developers new to the project. Clear headings, short paragraphs, concrete explanations,
examples, tables and diagrams. No marketing language, filler, vague statements, duplicated text or long
introductions. **Terminology**: one name per concept across code, UI, API, database and docs.

## Accuracy

- Verify every name against the implementation: folders, classes, routes, tables, configuration keys, commands,
  dependencies, behaviour.
- When code and docs disagree, find out which is outdated - don't just "fix" the doc to match a bug.
- Never invent missing information; mark it as unknown or ask.
- **Single source of truth**: explain each thing in one authoritative place and link to it elsewhere.
- Remove documentation for things that no longer exist.

## Code comments

Comment non-obvious decisions, workarounds, external limitations, important assumptions and unusual behaviour. Don't
comment what the code plainly says; prefer making the code readable. Match the density and style of the
surrounding code.
