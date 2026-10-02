# Architecture, structure and handover

## Folder structure

A new developer should know, without asking, where to put: UI, business/domain logic, data access, infrastructure
and integrations, APIs, models and DTOs, configuration, reusable components, and tests.

- Organize by clear responsibility - architectural layer, feature or domain - and use the same rule throughout.
- Flag grab-bag folders (`Helpers`, `Utils`, `Common`, `Misc`, an oversized `Services`) that hold unrelated
  things; list what each file really is and propose the smallest regrouping that makes it obvious.
- Check that dependencies point the intended way (e.g. domain/application don't depend on UI or infrastructure).
  Be pragmatic: flag violations that create real coupling or confusion, and document deliberate exceptions.
- Estimate churn before proposing moves; moving files has a cost (history, merge conflicts, open branches). Prefer
  doing large moves on a quiet branch.

## Maintainability

Prefer clear names, focused classes and functions, explicit dependencies, predictable patterns, simple control flow,
reusable components and clear ownership.

Look for and fix:
- giant classes or functions - split along existing seams (a pure rule set out of a UI class, a reader out of a
  scheduler), not by line count
- deep nesting, magic values, duplicated logic (formatters, parsers, connection builders, "is X" checks - a
  second copy is where bugs diverge)
- hidden side effects, tight coupling, clever code that needs explaining

## Consistency

The same problem is solved the same way: naming, folders, API patterns, validation, error handling and messages,
logging, configuration, permissions, UI components, spacing, tables, forms and dialogs. When two patterns exist,
pick the better one and converge - in the code you touch, at least.

## Technical debt

Find and remove safely: unused files, classes, members, resources and styles; dead or commented-out code; obsolete
compatibility code (or write down how long it is supported); old experiments; temporary workarounds that became
permanent; duplicate implementations; unused dependencies; outdated configuration and stray build artifacts.

**Verify before deleting**: search the whole repo, including markup/templates, reflection and DI registration,
string-built names, tests and docs.

## Avoiding overengineering

Don't introduce microservices, repositories, factories, event buses, extra interfaces or layers because they are
popular. Add an abstraction when there are two real implementations, a test that needs a seam, or a boundary that
keeps changing. The simplest architecture that stays maintainable wins.

## Platform-native features

Before building something, check whether the framework, runtime, OS, database or hosting platform already provides
it reliably: scheduling, caching, logging, configuration, secrets storage, file watching, retry policies, background
services, process isolation, locale formatting. Prefer the proven native solution.

## Developer handover

Assume someone else takes over tomorrow. Without the original developer they must be able to learn:

| Question | Where it should be answered |
|---|---|
| What does the application do, for whom? | README |
| How do I run, build and test it? | README (short) + `docs/development.md` |
| How is it configured? Which settings, defaults, secrets? | `docs/configuration.md` |
| How is it built and why that way? | `docs/architecture.md` + ADRs |
| Where does code for X live; where do I add Y? | architecture doc's folder section |
| How does data flow through it? | architecture/data-flow docs with diagrams |
| How do I debug a problem; where are the logs? | `docs/development.md` or `troubleshooting.md` |
| How is it released and deployed, and rolled back? | `docs/deployment.md` |
| What conventions must I follow (encoding, threading, SQL, secrets)? | a short conventions section |

Fill the gaps that matter for this project with the **documentation** skill.
