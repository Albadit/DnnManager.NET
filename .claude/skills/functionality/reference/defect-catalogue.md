# Functionality: defect catalogue

## What to Look For

### Logic Errors
- Off-by-one errors in loops, array access, and pagination
- Incorrect boolean logic (De Morgan's law violations, operator precedence)
- Wrong comparison operators (`<` vs `<=`, `==` vs `===`, `>` vs `>=`)
- Missing null/undefined/None checks before dereference
- Integer overflow/underflow and arithmetic edge cases
- Floating point comparison using equality instead of epsilon
- Division by zero not guarded
- Modulo with negative numbers (language-dependent behavior)
- Short-circuit evaluation assumptions that don't hold
- Implicit type coercion changing behavior silently

### Business Rule Errors
- Missing condition combinations (untested rows in the decision table)
- Contradictory rules (two rules fire for the same input with different outcomes)
- Dead rules that can never fire (shadowed by earlier conditions)
- Boundary conditions in business thresholds (exactly at the threshold value)
- Time-dependent rules without timezone awareness
- Currency/money calculations using floating point instead of exact arithmetic

### State and Workflow Errors
- Invalid state transitions allowed (states reachable that shouldn't be)
- Missing state transitions (valid actions not handled in certain states)
- State corruption after partial failure (half-applied state changes)
- Invariant violations after transitions (postconditions broken)
- Stuck workflows that can never complete or time out
- Concurrent transitions creating impossible states

### Integration and Failure Errors
- Retry without idempotency (duplicate side effects)
- Cascading retry amplification across call layers
- Silent swallowing of errors (catch-and-ignore, empty catch blocks)
- Async operations with unhandled error paths
- Timeout handling that leaves partial state
- Contract drift between producer and consumer
- Serialization round-trip data loss

### Data Flow Errors
- Uninitialized variables used before assignment
- Unused return values (especially error indicators)
- Implicit type coercion changing values silently
- Data truncation during transformation (encoding, field length, precision)
- Missing validation at trust boundaries
- Mutable shared state modified from multiple paths without coordination
