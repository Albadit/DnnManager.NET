# Architecture: decisions and anti patterns

## Technical Decision Governance

### ADR Categories to Review

For each category, check: was a decision explicitly made, justified, and maintained?

**1. Architecture Style Decision:**
- Monolith / modular monolith / microservices / event-driven / hybrid
- Trade-off: delivery speed and simplicity vs independent scaling and team autonomy
- "Monolith first" unless scale and org needs clearly justify distribution

**2. Integration Model:**
- Sync vs async, orchestration vs choreography, shared DB vs owned DB
- Trade-off: consistency and simplicity vs decoupling and resilience
- Sagas for distributed transactions (compensations, not ACID across boundaries)

**3. Operational Posture:**
- SLOs, incident response, release strategy, on-call
- Trade-off: feature velocity vs reliability
- Error budgets turn reliability into an explicit product decision

**4. Cost Governance:**
- Speed vs unit economics
- FinOps: collaborative, value-driven, with timely visibility and intentional trade-offs

**5. Data Ownership and Consistency:**
- Which data is strongly consistent vs eventually consistent?
- Who owns which domain data? Who can read/write?
- Schema evolution strategy (expand-contract)?

**6. Team and Boundary Alignment:**
- Conway's Law: system design mirrors communication structure
- Team Topologies: team types and interaction modes minimizing cognitive load
- Do system boundaries match team ownership?

### Decision Comparison Framework

When comparing architectural alternatives, score against consistent criteria (1-5, weights per organization):

| Criterion | What to Evaluate |
|---|---|
| **Complexity** | Conceptual (how hard to reason about) + operational (how hard to run) |
| **Cost** | Infrastructure + people/on-call + tooling (visible and attributable?) |
| **Scalability & Resilience** | Scale without redesign? Behavior under partial failures? |
| **Maintainability** | Change one area without touching many? Clear ownership and contracts? |
| **Delivery Speed** | Ship safely, frequently, and reversibly? |
| **Team Fit** | Alignment with team topology, cognitive load, and communication structure? |

**Default**: prefer simplest option meeting current goals with explicit evolution path. Adopt irreversible complexity only when clearly justified.

## Common Anti-Patterns to Hunt For

Every analysis MUST explicitly check for these recurring failure patterns:

| Anti-Pattern | Description | Signal |
|---|---|---|
| **Distributed Monolith** | Many services but tightly coupled releases, shared schemas, synchronous chains | All services must deploy together; shared DB or schema |
| **Retry Storms** | Unbounded retries without backoff amplifying outages | Cascading failures under load; missing circuit breakers |
| **Architectural Drift** | Implementation diverges from intended constraints over time | Diagrams don't match reality; forbidden dependencies appearing |
| **Un-owned Data** | Multiple components writing the same business facts | Unclear conflict resolution; hidden coupling via data replication |
| **Underspecified Contracts** | APIs/events without versioning, compatibility rules, or idempotency | Migrations break consumers; retries cause duplicates |
| **Implicit Scope Creep** | System accumulates responsibilities without revisiting purpose | Tangled domains; everything depends on one "god service" |
| **Chatty Service Graphs** | Many synchronous calls for single operations | High tail latency; failure amplification |
| **Big-Bang Migration** | Replacing entire system at once instead of incrementally | High risk; use Strangler Fig instead |
| **Accidental Coupling via Shared Infrastructure** | Shared caches, queues, or databases creating hidden dependencies | Failure in one domain affects another |
| **Missing Failure Domains** | No bulkheads; one dependency failure takes down everything | Total outage from partial failure |

## Scalability and Evolution Assessment

Scalability is not only "handle more traffic" - also assess:
- **Team scalability**: can more developers/teams work in parallel without blocking each other?
- **Change scalability**: can releases happen more frequently with less coordination?
- **Data scalability**: can data grow without schema redesigns or performance cliffs?

**Conway's Law**: organizations produce system designs mirroring their communication structures. Check whether system boundaries align to team ownership. Architecture that requires constant cross-team coordination for routine work will slow delivery.

**Team Topologies alignment**: are team types and interaction modes designed to reduce cognitive load? Does the platform/team structure support or fight the architecture?

**Incremental modernization**: for legacy systems, prefer Strangler Fig pattern (replace gradually behind stable interfaces) over big-bang rewrites. Lower risk, preserves uptime, manages organizational change.

## Cross-Cutting Concerns Checklist

These are where architectural success is won or lost - easy to postpone, hard to retrofit. Every analysis MUST evaluate:

- [ ] **Security**: authn/authz boundaries, least privilege, secure-by-design, OWASP/ASVS baseline
- [ ] **Reliability**: SLIs/SLOs/error budgets, resilience patterns, postmortems
- [ ] **Observability**: correlated traces/metrics/logs, service health dashboards, diagnosis capability
- [ ] **Testing**: pyramid, contract tests, resilience tests, migration tests
- [ ] **Cost**: visible, attributable, governed, included in ADRs
- [ ] **Data**: ownership, consistency model, evolution strategy, saga patterns
- [ ] **Contracts**: versioned, backward-compatible, idempotent, schema-validated
- [ ] **Deployment**: immutable artifacts, config separation, environment parity, independent deployability
- [ ] **Team alignment**: Conway's Law check, cognitive load, ownership clarity
