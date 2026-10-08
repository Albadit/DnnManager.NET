---
name: orchestrator
description: >-
  Multi-area quality review: scores a request across nine quality dimensions (security, architecture, functionality,
  performance, reliability, maintainability, usability, compliance, operations), runs only the specialist skills
  that matter for it - in parallel or one by one - and merges their findings into one report ranked by severity. Use
  for broad analyses, audits or reviews that span several quality areas, or when the message carries the tags #all,
  #eco, #sync, #fix, #deeper or a dimension tag such as #security.
argument-hint: Describe what you want done, optionally with #tags
---

# Orchestrator

Route the request to the specialist skills that matter for it and run only those. Not every task needs every
specialist: what the request touches decides which dimensions are relevant and how deeply each is examined.

| Skill | Focus area |
|-------|-----------|
| `security` | OWASP Top 10:2025, API Top 10:2023, threat modelling, attack surfaces, security fixes |
| `architecture` | System design, patterns, boundaries, scalability, refactoring |
| `functionality` | Business logic, algorithms, edge cases, correctness, bug fixes, tests |
| `performance` | Queries, loops, caching, concurrency, memory, optimisations |
| `reliability` | Fault tolerance, retries, circuit breakers, error handling, resilience patterns |
| `maintainability` | Code quality, readability, duplication, refactoring, tech debt reduction |
| `usability` | UX, accessibility (WCAG), API ergonomics, error messages, UX fixes |
| `compliance` | GDPR, HIPAA, PCI DSS, data handling, audit trails, compliance fixes |
| `operations` | CI/CD, Docker, Kubernetes, monitoring, deployment, IaC, ops implementation |

## Routing

**Single-dimension task** (build a feature, fix a bug, refactor, set up CI): no scoring. Load the one best-fit skill
with the Skill tool and do the task under its guidance:

- Building features, fixing bugs, writing tests → `functionality`
- Security hardening, auth, input validation → `security`
- Refactoring, code quality, tech debt → `maintainability`
- System design, API design, module structure → `architecture`
- Optimisation, caching, query tuning → `performance`
- Error handling, retries, fault tolerance → `reliability`
- UI/UX, accessibility, API ergonomics → `usability`
- GDPR, PII, audit logging, data protection → `compliance`
- CI/CD, Docker, deployment, monitoring → `operations`

**Multi-dimensional task** (an analysis, audit or review, or work touching several quality areas): follow the
workflow below.

Don't stop to ask questions unless the request is too vague to act on (no code and no task description). Otherwise
score, run and synthesise in one flow.

## Tags

Tags can appear anywhere in the message, are case-insensitive and can be combined.

| Tag | Effect |
|-----|--------|
| _(none)_ | Score automatically, run in parallel, analyse only |
| `#sync` | Parallel: each scored specialist runs in its own subagent at the same time (the default) |
| `#eco` | Sequential: specialists run one at a time in this session; lower token cost, and later ones get earlier findings |
| `#all` | Run all 9 specialists at full scope |
| `#security`, `#architecture`, `#functionality`, `#performance`, `#reliability`, `#maintainability`, `#usability`, `#compliance`, `#operations` | Run only the tagged specialists, at full scope; no scoring |
| `#deeper <dimension>` | Raise that dimension to full scope for a deep-dive |
| `#fix` | Analyse **and** fix Critical and High findings |

```
analyse this code #eco                      -> score automatically, sequential
build a login page #security #usability     -> those 2 specialists handle the build
analyse this code #all                      -> all 9, parallel
analyse this code #all #eco #fix            -> all 9, sequential, fix Critical/High
analyse this code #security #performance    -> only those 2
deploy this app #operations #fix            -> operations sets up the deployment
analyse this code #deeper security          -> deep-dive security
```

## Workflow (multi-dimensional tasks)

1. **Score** the request across the nine dimensions and show the score card - [reference/scoring.md](reference/scoring.md).
2. **Run** the scored specialists in parallel or one at a time - [reference/invocation-and-synthesis.md](reference/invocation-and-synthesis.md), "Run the specialists".
3. **Synthesise** their findings into one ranked report - [reference/invocation-and-synthesis.md](reference/invocation-and-synthesis.md), "Synthesise".

## Reference

| File | Contents | Read |
|---|---|---|
| [reference/scoring.md](reference/scoring.md) | What counts as input, signal matrix, scoring rules, score card | For a multi-dimensional task, before scoring |
| [reference/invocation-and-synthesis.md](reference/invocation-and-synthesis.md) | Scope per weight, the prompt for each specialist, parallel and sequential runs, weighting, merging, verification, report format, follow-ups | After scoring, before running the specialists |

## Limits

- **Minimum**: 1 specialist - there is always at least one relevant dimension.
- **Typical**: 2-4 specialists - most code has 2-4 dominant concerns.
- **At most 5 at full scope**; any others run focused.
- Skipping a low-relevance dimension costs nothing; missing a high-relevance one costs everything.

## Rules

- **Score before running specialists** on a multi-dimensional task.
- **Show the score card** so the user can see the routing and override it with tags.
- **Re-score on new context** - if the user adds information after the first scoring, score again and adjust.
