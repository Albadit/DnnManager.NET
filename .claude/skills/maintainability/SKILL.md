---
name: maintainability
description: >-
  Code quality and maintainability analysis and refactoring: readability, code smells, complexity metrics, naming and
  vocabulary consistency, duplication, test quality and coverage, documentation, technical debt, dependency health and
  conventions. Use for code-quality reviews, refactoring, cleaning up dead or duplicated code, and reducing technical
  debt.
---

# Maintainability

Make sure code is readable, well structured and easy to evolve. Treat maintainability as measurable: assess it
through code smells, complexity metrics, duplication ratios, naming consistency, test coverage, documentation
completeness and dependency health. Code smells are warning signs that code will be harder to maintain - ignoring
them raises the risk of bugs and compounds technical debt. Every finding must cite the specific smell or metric
violated, its impact on development velocity, and an actionable refactoring with an effort estimate.

## Mode

This skill both analyses and implements. Analyse and report by default. When the request is to build, fix or
change something, or carries `#fix`, also make the changes: fix Critical and High findings directly and list
Medium and Low ones in the report.

## What to cover

- Assess **code readability** - can a new developer understand this code without tribal knowledge?
- Identify **code smells** and anti-patterns (Long Method, God Class, Feature Envy, Primitive Obsession, Shotgun Surgery, Divergent Change, Data Clumps, Dead Code, Magic Numbers)
- Measure **complexity** - cyclomatic complexity, cognitive complexity, nesting depth, method/class length
- Evaluate **naming conventions** - descriptive, consistent, pronounceable names aligned with domain language; no abbreviations, no inconsistent vocabulary across modules
- Detect **code duplication** and DRY violations - every copy is a point of failure needing separate maintenance
- Assess **test quality** and coverage strategy - coverage of critical paths and edge cases, test readability, flaky test detection
- Evaluate **documentation** completeness - self-documenting code via naming + comments that explain "why" + high-level architecture overview
- Identify **technical debt** - dead code, commented-out code, legacy modules, accumulated shortcuts
- Analyse **dependency management** - semantic versioning, lock files, outdated/vulnerable packages, update strategy

## Workflow

Work through these stages. The questions and checks for each are in [reference/analysis-stages.md](reference/analysis-stages.md):
read all of it for a full-scope review, or only the stages that match a focused scope.

1. Readability and Structure Assessment
2. Code Smell Detection
3. Complexity Analysis
4. Naming and Vocabulary Consistency
5. Duplication and DRY Violations
6. Test Quality and Coverage Strategy
7. Documentation and Self-Documenting Code
8. Technical Debt Inventory
9. Dependency Management and Versioning
10. Consistency and Convention Enforcement

## Reference

| File | Contents | Read |
|---|---|---|
| [reference/analysis-stages.md](reference/analysis-stages.md) | The analysis stages in full | Before analysing: all of it for a full-scope review, only the matching stages for a focused one |
| [reference/smells-and-metrics.md](reference/smells-and-metrics.md) | Code smell table with indicators, remedies and impact; metric targets and finding thresholds | When classifying a smell or rating a finding by metric |

## Output Format

For each finding, provide ALL of these fields:

```
### [SEVERITY] Maintainability Issue

- **Location**: File and line reference (specific, not vague)
- **Smell**: Code smell category (from the reference table)
- **Impact**: Effect on development velocity - how it slows future changes, increases bug risk, or blocks onboarding
- **Current State**: What the code looks like now (specific: line count, complexity score, duplication instances)
- **Recommendation**: How to improve it - specific refactoring with target structure (e.g., "extract into PaymentService with methods X, Y, Z")
- **Effort**: Low / Medium / High (with reasoning)
```

After all findings, ALWAYS provide these sections:

1. **Complexity hotspot map** - files/methods with highest complexity scores, ordered by complexity × change frequency (highest maintenance risk first)
2. **Duplication report** - identified duplication clusters, estimated lines duplicated, consolidation strategy
3. **Naming consistency audit** - inconsistent vocabulary found, recommended standard terms
4. **Test coverage gaps** - critical paths without tests, flaky tests identified, coverage improvement plan
5. **Technical debt inventory** - catalogued debt items classified by impact (high/medium/low) with recommended sprint allocation
6. **Dependency health** - outdated packages, known CVEs, versioning strategy gaps
7. **Priority remediation** - ordered by impact on development velocity (high-impact debt in frequently-changed code first); HIGH smell findings in hot-path files always at the top

## Guidelines

- **Code should be self-documenting through good naming** - if you need a comment to explain what code does, the names are wrong
- **Comments should explain "why", not "what"** - rationale for non-obvious decisions, not restatements of code
- **Remove commented-out code and dead code** - VCS preserves history; clutter increases cognitive load
- **Prefer explicit over implicit behavior** - no surprising side effects, no hidden state mutations
- **Follow the project's existing conventions** - consistency within the codebase trumps personal preference
- **Favor readability over cleverness** - write code for the next developer, not to impress
- **Keep functions focused on a single task** - one level of abstraction per function
- **Use early returns to reduce nesting** - flat code is easier to reason about than deeply nested code
- **Consistent vocabulary aligned with domain language** - standardize on one term per concept across the codebase
- **Replace magic numbers/strings with named constants** - self-documenting and less error-prone
- **Every copy-paste is a future bug** - extract shared logic; DRY violations in frequently-changed code are HIGH priority
- **Test coverage must include error paths and edge cases** - not just happy paths
- **Flaky tests must be fixed or removed** - they erode trust in the entire test suite
- **Use test builders/fixtures** - keep test setup minimal, readable, and focused on what matters for the scenario
- **Lock dependencies and update regularly** - loose version constraints are a ticking time bomb
- **Enforce style via automated tooling in CI** - linters and formatters eliminate style debates in code review
- **Allocate sprint time for tech debt** - ignoring debt compounds it; prioritize by impact on frequently-changed code
- **Measure complexity with tools** - don't eyeball it; use static analysis (SonarQube, ESLint complexity rules, radon, etc.)

## Follow-ups

After the report, offer these next steps in one line each; do not start them unprompted:

- Refactor the code to resolve the maintainability issues found.
- Assess the architectural implications with the `architecture` skill.
