# Functionality: analysis stages

## Contents

- Analysis Process (10 Stages)
  - Stage 1: Build a Testable Reference Model
  - Stage 2: Happy Path Analysis
  - Stage 3: Systematic Input Coverage
  - Stage 4: Edge Cases and Boundary Conditions
  - Stage 5: State Transition and Workflow Analysis
  - Stage 6: Error Path Analysis
  - Stage 7: Retry and Idempotency Verification
  - Stage 8: Integration Contract Correctness
  - Stage 9: Regression and Completeness Check
  - Stage 10: Defect Reporting

## Analysis Process (10 Stages)

### Stage 1: Build a Testable Reference Model

Before analyzing code, establish what "correct" means:

**1a. Identify Explicit References:**
- Requirements specs, acceptance criteria, user stories, API contracts
- Stored data invariants and schema constraints
- Pre-existing tests that encode expected behavior
- Error message specifications, status code contracts

**1b. Reconstruct Missing References:**
- When requirements are incomplete, build a **reference model as properties**: preconditions, postconditions, invariants, and allowed state transitions
- Define **safety properties** ("what must NEVER happen" - e.g., data corruption, double-charge, unauthorized access)
- Define **liveness properties** ("what must EVENTUALLY happen" - e.g., progress, completion, delivery)
- Use these as your test oracle - if you can't state the expected behavior, that IS the first finding

**1c. Expose Ambiguity:**
- Flag any requirement that is ambiguous, contradictory, incomplete, or unverifiable
- Good requirements are: correct, unambiguous, complete, consistent, verifiable, traceable
- Treat ambiguity as a defect risk - do NOT silently pick an interpretation and assume it's correct
- When multiple interpretations exist, document them and flag for resolution

**1d. Build Traceability:**
- Map: needs → requirements → design elements → verification activities → results
- Use traceability to show what must be reviewed when something changes
- Missing traceability = unverifiable completeness claims and high regression risk

### Stage 2: Happy Path Analysis

Verify the primary use case works correctly end-to-end:
- Does the main workflow produce the expected output for typical inputs?
- Are all required side effects triggered (DB writes, events emitted, notifications sent)?
- Does the response shape match the contract (status codes, response body, headers)?
- Are success metrics/logs emitted correctly?

### Stage 3: Systematic Input Coverage

Replace ad hoc guessing with evidence-based techniques:

**3a. Equivalence Partitioning:**
- For each input, divide values into **equivalence classes** (groups processed the same way)
- Identify BOTH **valid partitions** AND **invalid partitions**
- Full coverage requires exercising ALL identified partitions at least once
- For each partition: does the code enforce the expected handling (accept, reject, normalize, default)?
- **Common miss**: untested invalid partitions (what happens with wrong type, negative values, empty strings, oversized input?)

**3b. Boundary Value Analysis (BVA):**
- Target the **edges of ordered partitions** - this is where mistakes concentrate
- Use **3-value BVA** (more rigorous): test the boundary value, one below, one above
- Check: off-by-one errors, wrong comparison operators (`<` vs `<=`), boundary not enforced
- **Common miss**: mis-implemented comparison operator that only fails at the exact boundary

**3c. Decision Table Testing (for business rules):**
- When outcomes depend on **combinations of conditions** (discount rules, permissions, routing, eligibility, fraud checks):
  - Enumerate ALL feasible condition combinations in a decision table
  - Identify the expected action/outcome for each combination
  - Check for: missing columns (unhandled combinations), contradictory rules, dead rules that can never fire
- Decision tables both generate tests AND surface ambiguous/contradictory requirements
- **Common miss**: overlooked condition combinations that are "rare but possible"

### Stage 4: Edge Cases and Boundary Conditions

Check ALL of these categories for every relevant input/parameter:

**Values:**
- Empty collections, empty strings, `null`/`undefined`/`None`
- Maximum and minimum values (int limits, string length limits, array size limits)
- Single-element collections (different behavior than multi-element?)
- Zero, negative numbers, very large numbers, `NaN`, `Infinity`
- Floating point comparison issues (equality via epsilon, not `==`)
- Integer overflow/underflow

**Strings and Data:**
- Unicode, multi-byte characters, emoji, RTL text, zero-width characters
- Special characters that may be parsed (SQL, HTML, regex, JSON, path separators)
- Whitespace-only strings, leading/trailing whitespace
- Strings that look like other types ("true", "null", "0", "undefined")
- Data truncation during transformation (encoding changes, field-length limits)

**Time and Locale:**
- Timezone handling (UTC vs local, DST transitions, timezone-naive datetimes)
- Date boundaries (month rollover, year rollover, leap year Feb 29, epoch timestamps)
- Locale-specific: number formatting, currency, decimal separators
- Clock skew between distributed components

**Concurrency:**
- Race conditions in concurrent/parallel code
- Concurrent access to shared mutable state
- Read-modify-write without atomicity
- Ordering assumptions in event-driven or message-based systems
- Rare interleavings in distributed workflows (the state space is enormous - "reasoning by test cases" breaks down)

### Stage 5: State Transition and Workflow Analysis

**5a. State Model:**
- Identify ALL states an entity can be in (explicit and implicit)
- Map ALL valid transitions between states (trigger event → resulting state)
- Define what must be true AFTER each transition (postconditions/invariants)
- Check: can the system reach an **impossible/undefined state**?

**5b. Invalid Transition Testing:**
- Intentionally attempt invalid transitions (transitions that should NOT be allowed)
- Verify the system rejects them and stays in the correct state
- **Critical**: invalid transition testing prevents **defect masking** (one defect hiding another)
- Common miss: can you go from "cancelled" back to "active"? From "shipped" to "pending"?

**5c. Workflow Invariants:**
- What properties must hold ACROSS the entire workflow lifecycle?
- Example: "total number of items in + items out = items processed" (accounting invariant)
- Example: "once an order is confirmed, its total must not change" (business invariant)
- Check invariants at every state transition, not just at the end

**5d. Concurrent and Distributed Workflows:**
- For workflows spanning multiple components: are there interleaving-dependent bugs?
- Explicitly define safety properties ("data must never be corrupted") and liveness properties ("every request must eventually complete or fail cleanly")
- Check: can two concurrent operations leave the system in an inconsistent state?
- Check: can a long-running saga/workflow be left in a permanently stuck state?

### Stage 6: Error Path Analysis

**6a. Error Handling Completeness:**
- For every operation that can fail: is the error caught, propagated, or swallowed?
- Are error messages actionable and accurate (not misleading or leaking internals)?
- Do all code paths return appropriate values (no functions that fall off the end)?
- Do error handlers preserve the system's invariants?

**6b. Partial Failure Scenarios:**
- What happens when only SOME of a multi-step operation succeeds?
- Is there cleanup/rollback for the completed steps?
- Can the operation be safely retried? Is it idempotent?
- Does partial failure leave corrupted or inconsistent state?

**6c. Async Error Paths:**
- **Lost exceptions**: Is every async operation (Promise, Future, Task, coroutine) properly awaited/handled?
- Unhandled promise rejections: if concurrent async work fails, is the error propagated or silently lost?
- Detached tasks: if a Future completes with an exception but is never awaited, the error is never propagated to user code
- `Promise.all()` vs `Promise.allSettled()`: which semantics are needed? Is the error handling correct for the chosen approach?
- `CompletableFuture.get()`/`join()`: exceptional completion must be verified to surface, not silently swallowed

**6d. Timeout and Cancellation:**
- What happens when an operation times out? Is partial work cleaned up?
- Can cancelled operations leave side effects (DB writes, messages sent)?
- Are timeout values appropriate (not too short for normal operation, not too long for failure detection)?

### Stage 7: Retry and Idempotency Verification

This is where "looks correct" code frequently fails in production:

**7a. Retry Analysis:**
- Where do retries exist in the system (application code, client libraries, middleware, infrastructure)?
- **Cascading retry amplification**: if retries occur at multiple layers, load can multiply catastrophically
  - Example: 5-deep call stack × 3 retries at each layer = 243× load amplification
- Are retries bounded (max attempts, backoff, jitter)?
- Do retries have circuit breakers to stop hammering failed dependencies?
- **Client library retries**: SDK retry behavior (e.g., AWS SDK retries, HTTP client retries) is PART of the effective behavior - treat it as functional scope

**7b. Idempotency Verification:**
- For any operation that can be retried or receive duplicate messages:
  - Is there an explicit idempotency mechanism (idempotency keys, deduplication store, transactional outbox)?
  - HTTP semantics: PUT and DELETE are idempotent by spec; POST is NOT - if POST is retried, how are duplicate side effects prevented?
  - Can duplicate messages cause multi-write bugs, double-charges, duplicated notifications?
- **If no idempotency mechanism exists for a retried operation, that is a correctness risk - flag it**
- Check: is the idempotency key scoped correctly (per-user? per-request? per-operation?)?

**7c. At-Least-Once vs Exactly-Once:**
- In message-based systems: what delivery guarantee is assumed vs what is actually provided?
- If at-least-once: are consumers idempotent?
- If assuming exactly-once: verify the mechanism (transactional outbox, dedup store, etc.)

### Stage 8: Integration Contract Correctness

**8a. Producer/Consumer Assumption Drift:**
- Do consumers make assumptions about API response shapes that aren't guaranteed by the contract?
- Are there implicit assumptions about field presence, types, ordering, or null behavior?
- If schemas change (new fields added, old fields removed, types changed): will consumers break?
- Are there consumer-driven contract tests (Pact or equivalent) to detect incompatibility before release?

**8b. External Dependency Behavior:**
- Are external API error codes and edge cases handled (rate limits, partial responses, pagination, eventual consistency)?
- Do SDK/client library defaults match your assumptions (timeouts, retries, connection pooling)?
- What happens when a dependency is temporarily unavailable? Permanently removed?
- Are dependency version constraints explicit and tested?

**8c. Data Contract Validation:**
- Schema validation at every trust boundary (API input, message consumption, file parsing)?
- Are there unknown fields that get silently passed through (and could break downstream)?
- Serialization/deserialization round-trip correctness: does data survive encode → store → decode without loss or corruption?

### Stage 9: Regression and Completeness Check

**9a. Missing Test Cases Checklist:**
After analysis, identify whether these test types exist or are missing:
- [ ] Untested **invalid equivalence partitions** (wrong types, out-of-range, forbidden values)
- [ ] Untested **boundary neighbors** (especially where comparison operators change behavior)
- [ ] Missing **decision-table columns** for feasible condition combinations
- [ ] Missing **invalid transition attempts** in stateful workflows
- [ ] Missing **retry/idempotency tests** validating behavior under duplicated requests
- [ ] Missing **partial failure tests** (multi-step operation with step N failing)
- [ ] Missing **async error path tests** (unhandled rejections, detached tasks)
- [ ] Missing **concurrency tests** (race conditions, read-modify-write, interleaving)
- [ ] Missing **integration contract tests** (consumer expectations vs provider reality)
- [ ] Missing **timeout/cancellation tests** (cleanup, side effects, state after timeout)

**9b. Property-Based Testing Opportunities:**
- Identify properties that must hold for ALL inputs in a domain (not just specific examples)
- Generate randomized inputs (including edge cases you wouldn't think of) to validate properties
- When a failure is found: reduce to the simplest failing example → stable regression test
- Especially valuable for: parsing, serialization/deserialization, calculation logic, data transformations

**9c. Fuzzing Opportunities:**
- For parsing, serialization, protocol handling, and file processing: can fuzzing discover crashes, unhandled exceptions, timeouts, or resource exhaustion on malformed input?
- Even non-security fuzzing finds functional robustness issues

**9d. Regression Risk Assessment:**
- Do existing tests pass? If any are broken, is it from the change under review or pre-existing?
- Does the change modify observable behavior that existing consumers depend on?
- Is backward compatibility maintained for critical interfaces?
- Black-box tests should stay stable across refactors - if behavior didn't change, tests shouldn't break

### Stage 10: Defect Reporting

For every finding, use the exact format under Output Format in [SKILL.md](../SKILL.md), with concrete evidence.
