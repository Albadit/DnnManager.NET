# Orchestrator: invocation and synthesis

## Contents

- Run the specialists
  - Scope per weight
  - Specialist prompt
  - Parallel (`#sync`, the default)
  - Sequential (`#eco`)
- Synthesise
  - Weighting
  - Rank by severity, not volume
  - Merge shared root causes
  - Verify before reporting
  - Report format
  - Follow-ups

## Run the specialists

### Scope per weight

- **FULL** → the specialist covers its whole checklist over the full input.
- **FOCUSED** → the specialist examines **only** the signals found while scoring. Example: _"Focus only on the
  database query patterns - check for N+1 queries, missing indexes and unoptimised joins. Skip general performance
  analysis."_
- **SKIP** → not run, and not mentioned unless the user asks.

### Specialist prompt

Each specialist runs from this prompt. A subagent starts with no context, so the prompt must carry everything:

```
Load the `<skill>` skill with the Skill tool and follow it.
Target: <files, paths or feature; the user's request in their words>
Scope: full | focused on: <detected signals>
Action: analyse and report only | analyse, then fix Critical and High findings (edit files directly; list Medium/Low)
Earlier findings (sequential runs only): <one line of critical findings, or none>
Return findings in the skill's output format, each with file:line evidence or marked "needs verification".
```

### Parallel (`#sync`, the default)

Run each scored specialist in its own `general-purpose` subagent (Agent tool), sending all the calls **in one
message** so they run at the same time. Their checklists and file reads stay out of the main context. Wait for all
of them, then synthesise. Best when 2-3 dimensions scored, the input is small, or speed matters.

With `#fix`, the subagents **analyse only** - parallel edits to the same files would collide. Apply the fixes
yourself after synthesis, one dimension at a time, starting with the highest-ranked findings.

### Sequential (`#eco`)

Run the specialists one at a time in this session: load each skill with the Skill tool, complete its analysis, then
move to the next. Order: FULL dimensions first (strongest signals first), then FOCUSED ones. Best when 4+
dimensions scored, the codebase is large, or token cost matters.

From the second specialist on, carry a one-line summary of earlier critical findings, e.g. _"Security already
flagged SQL injection in the search endpoint - focus reliability on the error paths around it."_ With `#fix`, fix
each specialist's Critical and High findings before moving on.

## Synthesise

### Weighting

- **FULL findings** → listed first, in detail; they drive the narrative.
- **FOCUSED findings** → included but condensed, after the full findings.
- **SKIPPED dimensions** → absent; don't pad the report with "nothing found in X".

### Rank by severity, not volume

- A specialist with 15 low-severity issues does **not** outweigh one with a single critical issue.
- Rank by **severity, then weight** - never by finding count.
- A Critical finding from a FOCUSED specialist outranks a Medium finding from a FULL one.

### Merge shared root causes

When several specialists flag the same root cause from different angles, **merge them into one finding** with the
combined severity. Example: Security flags "unsanitised input" and Reliability flags "crash on malformed input" -
one Critical finding with both impacts.

### Verify before reporting

Specialist findings are hypotheses. Check every Critical and High finding against the code before it goes into the
report; drop or downgrade those that don't hold up, and say which ones you could not verify.

### Report format

```markdown
# Quality review - [what was reviewed]

**Scope**: [files/components]
**Dimensions**: [N] of 9 - [list, each marked full or focused]
**Run**: parallel | sequential
**Action**: analyse only | analyse and fix

## Critical
[Immediate action - from any specialist, ranked by severity, then weight]

## High
## Medium
## Low
[Condensed]

## Coverage

| Dimension | Weight | Findings | Top issue |
|-----------|--------|----------|-----------|
| Security | FULL | 3 | SQL injection in user search endpoint |
| Performance | FOCUSED | 1 | Unindexed query on orders table |

## Recommended actions (priority order)
1. **[CRITICAL]** ...
2. **[HIGH]** ...
```

With `#fix`, also list what changed for each finding and how it was verified (tests, build, render).

### Follow-ups

End with one line offering the next steps; do not start them unprompted: re-run in parallel or sequentially, change
which dimensions run, deep-dive one dimension (`#deeper <dimension>`), or fix the findings (`#fix`).
