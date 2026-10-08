# Maintainability: analysis stages

## Contents

- Analysis Process (10 Stages)
  - Stage 1: Readability and Structure Assessment
  - Stage 2: Code Smell Detection
  - Stage 3: Complexity Analysis
  - Stage 4: Naming and Vocabulary Consistency
  - Stage 5: Duplication and DRY Violations
  - Stage 6: Test Quality and Coverage Strategy
  - Stage 7: Documentation and Self-Documenting Code
  - Stage 8: Technical Debt Inventory
  - Stage 9: Dependency Management and Versioning
  - Stage 10: Consistency and Convention Enforcement

## Analysis Process (10 Stages)

### Stage 1: Readability and Structure Assessment
- Can a new developer understand this code without explanation?
- Assess method/class length against targets (methods < 20 lines, classes < 200 lines)
- Check nesting depth - deeply nested code (> 3 levels) increases cognitive load and is harder to reason about
- Verify single responsibility: each class/module has one reason to change
- Large classes that handle multiple concerns (validation + payment + notification in one class) must be split into focused classes
- Long methods must be extracted into smaller, named functions - each doing one thing
- Use early returns to reduce nesting and improve readability
- **Target**: cyclomatic complexity < 10 per function; nesting depth ≤ 3

### Stage 2: Code Smell Detection

Systematically scan for these smells - each is a maintenance risk multiplier:

**Structural Smells (Impact: High):**
- **Long Method**: methods exceeding ~20 lines - extract smaller functions with descriptive names
- **God Class / Large Class**: class with too many responsibilities - split into focused classes following single responsibility principle
- **Feature Envy**: method that manipulates another class's data excessively - move method to the class it envies
- **Divergent Change**: one class modified for multiple unrelated reasons - separate concerns into distinct classes
- **Shotgun Surgery**: one conceptual change requires edits across many classes/files - consolidate related logic so a single change touches one place

**Data Smells (Impact: Medium):**
- **Primitive Obsession**: using raw primitives (strings, ints, arrays) where domain-specific types would be clearer - create value objects (e.g., `EmailAddress`, `Money`, `DateInterval`)
- **Data Clumps**: same group of fields/parameters appear together repeatedly (`clientName`, `clientEmail`, `clientPhone`) - extract a class (e.g., `ContactDetails`)
- **Long Parameter List**: methods with > 3-4 parameters - introduce parameter objects or builder patterns
- **Magic Numbers / Strings**: unexplained literal values (`1000 * 60 * 60 * 24`) - replace with well-named constants (`MILLISECONDS_PER_DAY`)

**Lifecycle Smells (Impact: Low-Medium):**
- **Dead Code**: unreachable or unused code, legacy files no longer referenced - remove it; if preservation is needed, it lives in version control history
- **Commented-Out Code**: lingering fragments "just in case" - remove; VCS preserves history
- **Speculative Generality**: abstractions built for future needs that never materialized - remove unused abstractions

### Stage 3: Complexity Analysis
- Measure **cyclomatic complexity** per function - target < 10; above 20 is a HIGH finding
- Measure **cognitive complexity** - accounts for nesting, breaks in linear flow, and recursion
- Measure **nesting depth** - target ≤ 3 levels; deeply nested conditionals/loops are hard to reason about
- Measure **method length** - target < 20 lines; above 30 is a finding
- Measure **class length** - target < 200 lines; above 300 is a finding
- Measure **parameter count** - target ≤ 4; above 4 suggests missing abstraction
- High complexity in controller or orchestration methods often indicates misplaced business logic (Feature Envy) - move logic into domain classes

### Stage 4: Naming and Vocabulary Consistency
- Names must be **descriptive, consistent, and pronounceable** - no single letters (except loop indices), no cryptic abbreviations (`mngr`, `proc`, `ctx`)
- Method names should describe what they do accurately - `getData()` is vague; `fetchUserMetrics()` is clear
- **Consistent vocabulary across modules**: if the domain uses "user", don't use `userInfo`, `userDetails`, `customerData`, and `clientRecord` interchangeably for the same concept - standardize on one term
- Align naming with **domain/ubiquitous language** - names should match how the team and stakeholders talk about the system
- Boolean names should read as assertions: `isActive`, `hasPermission`, `canRetry`
- Collections should be pluralized: `users`, `orderItems`
- Without consistent naming: developers misunderstand code intent, increasing time to debug and onboard

### Stage 5: Duplication and DRY Violations
- Duplicate code requires fixes in multiple places - every copy is a **point of failure** needing separate maintenance
- Scan for:
  - Copy-pasted logic across classes/modules (e.g., same data-mapping code in multiple DAOs)
  - Near-duplicates with minor variations (often hiding a missing abstraction)
  - Repeated boilerplate that could be extracted into shared utilities, base classes, or higher-order functions
- Refactor strategy:
  - Extract common logic into shared utility functions or base classes
  - Use inheritance, composition, generics, or higher-order functions to eliminate repetition
  - Ensure the shared abstraction is genuinely shared behavior - don't force unrelated code together just because it looks similar
- **DRY violations are HIGH findings** when they affect frequently-changed code - bug fixes or schema changes must be applied to each copy

### Stage 6: Test Quality and Coverage Strategy
- **Coverage targets**: > 80% for critical paths; coverage in the upper 80s or 90s is expected for thoughtful testing; very low coverage (< 50%) is a sign of trouble
- Coverage is necessary but not sufficient - tests must have **meaningful assertions** that validate behavior, not just execute lines
- Assess:
  - Are critical code paths (business logic, error handling, edge cases) covered?
  - Are error paths and boundary conditions tested?
  - Are tests focused and readable - each test validates one behavior?
  - Are tests independent - no order dependencies, no shared mutable state?
  - Are there **flaky tests** (randomly failing)? Flaky tests must be fixed or removed - they erode trust in the suite
- Test maintainability:
  - Tests with complex setup (huge object graphs, many parameters) are hard to maintain - use test fixtures, builder patterns, or factory methods
  - Tests should specify only what matters for the scenario - use builders with sensible defaults
  - Avoid over-broad integration-style tests masquerading as unit tests - they are brittle and slow
- **Missing test coverage for error handling and edge cases is a MEDIUM+ finding**

### Stage 7: Documentation and Self-Documenting Code
- **Self-documenting code through naming** is the first priority - if names are clear, less documentation is needed
- **Comments should explain "why"**, not "what" - if a comment restates the code, it's noise; if it explains a non-obvious design decision, it's valuable
- Remove outdated or misleading comments - wrong comments are worse than no comments
- Remove commented-out code - it confuses developers and has no value (VCS preserves history)
- Required documentation:
  - **High-level architecture overview** (README or docs) - system purpose, component relationships, data flow
  - **Module/package descriptions** - what each major module does and its public API
  - **Complex logic rationale** - why a non-obvious approach was chosen (algorithm choice, workaround, trade-off)
  - **Public API documentation** - parameters, return values, usage examples, error conditions
- Without documentation: new developers spend excessive time learning the codebase through trial and error

### Stage 8: Technical Debt Inventory
- Catalog accumulated debt: outdated patterns, missing abstractions, known shortcuts, legacy modules, TODO/FIXME/HACK comments
- Classify debt by impact:
  - **High-impact**: debt in frequently-changed code - compounds with every change
  - **Medium-impact**: debt in stable code - annoying but not actively slowing work
  - **Low-impact**: dead code, legacy files - clutter but minimal active cost
- Prioritize remediation by **impact on development velocity** - focus on debt in hot paths (frequently-modified files)
- Regular cleanup reduces cognitive load - allocate time each sprint to pay down highest-impact debt
- Archive or remove legacy files/modules no longer referenced - if they must be preserved, branch or tag them

### Stage 9: Dependency Management and Versioning
- **Semantic versioning** for both internal releases and dependencies - developers must know if an update is backward-compatible or breaking
- Lock dependencies to fixed versions (lock files committed to VCS) - loose constraints (`*`, `>=`) lead to inconsistent installs and surprise breakage
- Identify **outdated dependencies** - libraries multiple major versions behind are a security and compatibility risk
- Use dependency management tools (Dependabot, Renovate, npm audit, pip-audit) to catch outdated or vulnerable packages automatically
- Review transitive dependencies - vulnerabilities often hide in indirect dependencies
- **Outdated dependencies with known CVEs are HIGH findings**

### Stage 10: Consistency and Convention Enforcement
- Verify the codebase follows a **consistent style** - formatting, naming, file organization, error handling patterns
- Use automated linters and formatters (ESLint, Prettier, Black, Checkstyle, etc.) enforced in CI - style discussions should be resolved by tooling, not code review
- Verify consistent error handling patterns across the codebase (e.g., always use Result types, always throw specific exceptions, always log + rethrow)
- Verify consistent project structure - similar modules organized the same way
- Inconsistent conventions increase cognitive load for every developer on every change
