# ADR 0002 - The layers are kept apart by a test, not by projects

**Status:** accepted (2026-10)

## Context

DNN Manager is one project (`DnnManager.csproj`) with four layers as folders and
namespaces under `src/`: Domain, Application, Infrastructure, Presentation. Nothing
in the build stopped one layer from using another it shouldn't, and the
architecture document's rule had drifted from the code: it allowed Presentation
three Infrastructure parts, while the code used about fifteen.

## Decision

- The rule is what the code needs: **Domain** depends on nothing, **Application** on
  Domain only, **Infrastructure** on both - never on Presentation. **Presentation**,
  the composition root, may use Infrastructure for what is the app's own rather
  than a project's (the monitor, keep warm, settings, updates, logs, the terminal,
  diagnostics); everything that changes a project goes through a use case.
- [`LayeringTests`](../../tests/DnnManager.IntegrationTests/LayeringTests.cs), a fast test,
  reads the source and fails on a `DnnManager.*` reference that breaks the rule.

## Consequences

- A wrong reference fails the fast tests - locally, in the release script and in CI.
- The check is textual (usings and fully qualified names), not the compiler's: it
  can't see a dependency through `var` or reflection. That is enough for the
  mistakes it is there for.
- The stronger option - Domain and Application in a project of their own, so the
  compiler enforces it - remains open. It costs a second project, its packaging
  into the single-file exe, and `InternalsVisibleTo` across projects; it would be
  done step by step (Domain first).
