# Orchestrator: scoring

Scan the input quickly and give each of the 9 dimensions a weight - **FULL**, **FOCUSED** or **SKIP** - by matching
it against the signals below.

## What counts as input

Score only on what is **already visible** - no tool calls during scoring:

- Code the user pasted or attached
- Files open or selected in the editor (IDE context)
- Files or paths the user explicitly named
- The task description itself ("build a login page" implies Security + Usability + Functionality)

For a task description with no code ("build a REST API"), score on **what the task will involve**.

## Signal matrix

| Dimension | HIGH signals | MEDIUM signals |
|-----------|--------------|----------------|
| **Security** | Auth/session handling, user input processing, SQL/NoSQL queries, file uploads, crypto operations, secrets/keys/tokens, deserialization, CORS/CSP headers, privilege/role checks, payment processing | Third-party API calls, config files, environment variables, cookie handling, URL construction |
| **Architecture** | System/service boundaries, component decomposition, dependency injection, abstract interfaces, event/message systems, module structure, API contract definitions, microservice communication | Class hierarchies, import structure, file organization, naming conventions, simple factory patterns |
| **Functionality** | Business logic with branching, algorithms, data transformations, state machines, multi-condition validation, calculations, rule engines, workflow orchestration | Simple CRUD operations, straightforward mapping, basic configuration, trivial getters/setters |
| **Performance** | Database queries (especially joins/subqueries), nested loops, large dataset processing, caching logic, concurrent/async patterns, memory-intensive allocation, batch/bulk operations, streaming | Single simple queries, small iterations, small fixed-size operations, basic string manipulation |
| **Reliability** | External service calls, network I/O, retry/backoff logic, circuit breakers, timeout handling, queue/message processing, distributed state coordination, failover mechanisms, health checks | Try/catch blocks, null checks, simple error returns, single-point operations |
| **Maintainability** | Complex inheritance chains, large functions (>50 LOC), deep nesting (>3 levels), visible code duplication, missing/inadequate tests, tight coupling across modules, magic numbers/strings | Medium-sized functions, minor duplication, reasonable structure with some improvement opportunity |
| **Usability** | UI components, form handling, user-facing error messages, accessibility attributes (ARIA), API response format design, CLI interfaces, end-user documentation | Internal APIs, developer tooling, log message formatting, admin-only interfaces |
| **Compliance** | PII/PHI data handling, payment card data, consent management, data retention/deletion logic, audit trail logging, user data export (DSAR), encryption at rest/transit | General application logging, analytics event tracking, third-party SDK integration |
| **Operations** | CI/CD pipeline configs, Dockerfiles/Compose, K8s manifests, monitoring/alerting rules, log aggregation config, deployment scripts, health/readiness probes, feature flags, IaC (Terraform/Pulumi) | Build scripts, environment variable configs, simple shell scripts, Makefile targets |

## Scoring rules

1. **Use only what is visible** - never search or read files while scoring.
2. **For each dimension**, note which HIGH and MEDIUM signals are present.
3. **Assign the weight:**

   | Weight | Criteria | What the specialist does |
   |--------|----------|--------------------------|
   | **FULL** | ≥2 HIGH signals, or 1 HIGH + ≥2 MEDIUM | Full-scope analysis across its whole checklist |
   | **FOCUSED** | 1 HIGH signal, or ≥2 MEDIUM | Examines only the detected signals |
   | **SKIP** | No HIGH signal and ≤1 MEDIUM | Not run |

4. **Cap FULL at 5.** If more than 5 dimensions score FULL, re-check them and move the weakest to FOCUSED. A request
   with concentrated signals gets fewer, deeper analyses; one with diffuse signals gets broader, lighter ones.
5. **Count distinct signal types, not occurrences.** A file with 20 SQL queries is **one** Security signal (SQL
   injection surface), not 20.
6. **When unsure between FOCUSED and SKIP, choose FOCUSED** - a focused analysis is cheap; a missed critical
   finding is expensive.

## Score card

Show the scoring so the user can see the routing, then **continue straight away** - the card is information, not a
request for confirmation:

```
Scores
| Weight  | Dimension   | Signals                                          |
|---------|-------------|--------------------------------------------------|
| FULL    | Security    | auth handling, SQL queries, user input validation |
| FULL    | Reliability | external API calls, retry logic, timeouts         |
| FOCUSED | Performance | database queries                                  |
| SKIP    | Architecture, Functionality, Maintainability, Usability, Compliance, Operations |
Running 3 of 9 (2 full, 1 focused) - parallel - analyse only
```
