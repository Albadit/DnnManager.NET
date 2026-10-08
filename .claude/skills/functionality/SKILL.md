---
name: functionality
description: >-
  Functional correctness analysis and bug fixing: business logic against an explicit reference model, equivalence
  partitions, boundary values, decision tables, edge cases, state transitions, error and async paths, retries and
  idempotency, integration contracts, and missing tests. Use for validating that a feature behaves as intended,
  reviewing logic, finding and fixing bugs, and writing regression or property-based tests.
---

# Functionality

Verify that the implementation's observable behaviour matches the intended reference: requirements, contracts and
acceptance criteria. Use systematic, evidence-based techniques so no gap in functional coverage is left. Treat
unclear expected behaviour as a defect risk, not as permission to assume.

## Mode

This skill both analyses and implements. Analyse and report by default. When the request is to build, fix or
change something, or carries `#fix`, also make the changes: fix Critical and High findings directly and list
Medium and Low ones in the report.

## What to cover

- **Verify** business logic correctness against an explicit reference model (requirements, contracts, acceptance criteria)
- **Expose ambiguity** in requirements early - ambiguous requirements are inherently unverifiable
- **Systematically cover** inputs using equivalence partitioning, boundary value analysis, and decision tables
- **Check state transitions** and invariants using state models (valid AND invalid transitions)
- **Verify failure behavior** including retries, idempotency, timeouts, partial failures, and async error paths
- **Validate integration contracts** to detect producer/consumer assumption drift
- **Assess completeness** using traceability (requirements → design → verification → results)
- **Report defects reproducibly** with exact location, expected vs actual, deterministic trigger, and fix suggestion
- **Harden regressions** with property-based testing, fuzzing, and systematic edge-case generation

## Workflow

Work through these stages. The questions and checks for each are in [reference/analysis-stages.md](reference/analysis-stages.md):
read all of it for a full-scope review, or only the stages that match a focused scope.

1. Build a Testable Reference Model
2. Happy Path Analysis
3. Systematic Input Coverage
4. Edge Cases and Boundary Conditions
5. State Transition and Workflow Analysis
6. Error Path Analysis
7. Retry and Idempotency Verification
8. Integration Contract Correctness
9. Regression and Completeness Check
10. Defect Reporting

## Reference

| File | Contents | Read |
|---|---|---|
| [reference/analysis-stages.md](reference/analysis-stages.md) | The analysis stages in full | Before analysing: all of it for a full-scope review, only the matching stages for a focused one |
| [reference/defect-catalogue.md](reference/defect-catalogue.md) | Catalogue of logic, business rule, state and workflow, integration and failure, and data flow errors | While scanning the code for defects |

## Severity Classification

Apply this evidence-based severity rubric consistently:

| Severity | Criteria | Examples |
|---|---|---|
| **CRITICAL** | Crash, data loss/corruption, blocks core workflow with no workaround | Data corruption on concurrent write; double-charge in payment flow; infinite loop crashing service |
| **HIGH** | Major loss of function or broken business rule enforcement affecting real users/data, possibly limited workaround | Wrong discount calculation for a common customer class; missing auth check on business-critical endpoint; retry storm under moderate load |
| **MEDIUM** | Partial loss of function under specific conditions, incorrect edge behavior, or mishandled error path that doesn't always break the workflow | Off-by-one in pagination showing duplicate items; unhandled timeout leaving order in "processing" forever; missing validation for rare but valid input |
| **LOW** | Minor loss of function with easy workaround, confusing but non-breaking behavior, or narrow-scope inconsistency | Incorrect error message text; cosmetic data formatting issue; edge case in rarely-used feature with workaround |

When severity needs to be more repeatable: use **likelihood × impact** (likelihood in real usage × impact on users/data) to derive the tier.

## Output Format

For each finding, provide ALL of these fields:

```
### [SEVERITY] Functional Issue Title

- **Location**: File and line reference (exact)
- **Expected Behavior**: What should happen (traceable to which requirement/contract/invariant)
- **Actual Behavior**: What the code actually does (with evidence - not speculation)
- **Trigger**: Exact input/conditions to reproduce (deterministic steps)
- **Root Cause**: Why the code is wrong (logic error, missing case, wrong operator, etc.)
- **Impact**: What goes wrong for users/data/system (standalone + cascade potential)
- **Fix**: Recommended correction with secure/correct implementation pattern
- **Regression Test**: What test should be added to catch this permanently
```

After all findings, ALWAYS provide these sections:

1. **Completeness assessment** - which input partitions, state transitions, and error paths are covered vs uncovered
2. **Missing test types** - use the Stage 9 checklist in [reference/analysis-stages.md](reference/analysis-stages.md) to identify gaps
3. **Ambiguity log** - requirements or behaviors that are unclear and need resolution before verification is complete
4. **Property candidates** - properties suitable for property-based testing to harden against regressions
5. **Traceability gaps** - requirements that have no corresponding verification, or code paths with no traceable requirement

## Guidelines

- Always consider what happens with empty, null, boundary, and invalid inputs - use equivalence partitioning, not guessing
- Verify that error messages are actionable, accurate, and don't leak internals
- Check that ALL code paths return appropriate values (no fall-through without return)
- Ensure loops have proper termination conditions and handle empty collections
- Verify that async operations handle ALL completion states (success, failure, timeout, cancellation)
- Check mathematical operations for edge cases (division by zero, overflow, precision loss)
- Treat retry semantics (including those inherited from client libraries/SDKs) as part of functional scope
- Flag any retried operation without an idempotency mechanism as a correctness risk
- Check integration contracts for assumption drift - don't assume APIs return what you expect without verification
- Test invalid state transitions intentionally - they prevent defect masking
- Define safety properties ("must never happen") and liveness properties ("must eventually happen") for critical workflows
- Use decision tables for any logic with combined conditions - they expose gaps that code review misses
- Black-box expected behavior is the contract - observable behavior changes are regressions until proven otherwise
- If you can't state the expected behavior for a code path, that IS a finding (ambiguity risk)
- Never assume "it works" from reading code alone - verify against the reference model with concrete examples

## Follow-ups

After the report, offer these next steps in one line each; do not start them unprompted:

- Write tests that reproduce and guard the functional issues found.
- Assess the reliability implications of these issues with the `reliability` skill.
