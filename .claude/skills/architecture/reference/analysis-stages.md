# Architecture: analysis stages

## Contents

- Analysis Process (12 Stages)
  - Stage 1: System Purpose, Scope, and Constraints
  - Stage 2: Quality Attribute Analysis (ATAM-style)
  - Stage 3: Architecture Style Assessment
  - Stage 4: Component Boundaries and Responsibilities
  - Stage 5: Communication Patterns and Integration
  - Stage 6: Data and State Management
  - Stage 7: Deployment Topology and Operability
  - Stage 8: Reliability, Resilience, and Fault Handling
  - Stage 9: Observability Architecture
  - Stage 10: Security Posture (Architecture Level)
  - Stage 11: Testing Strategy (Architecture Alignment)
  - Stage 12: Cost Efficiency and FinOps

## Analysis Process (12 Stages)

### Stage 1: System Purpose, Scope, and Constraints

Produce a concrete narrative answering:
- What **business capabilities and user value** does the system exist to deliver?
- What are the **primary user journeys** and **failure-intolerant flows** (payments, identity, orders, safety-critical)?
- What constraints are **hard boundaries** vs **negotiable preferences** (data residency, SLAs, required integrations, mandated platforms, time-to-market, staffing)?
- Define: 1) "in-scope capabilities," 2) "explicitly out-of-scope capabilities," 3) "interfaces and dependencies that bind you"

**Check for implicit scope creep**: systems accumulating responsibilities without revisiting original purpose → tangled domains, coupled data, unstable delivery.

### Stage 2: Quality Attribute Analysis (ATAM-style)

Express quality requirements as **scenarios** ("When X happens, the system should do Y within Z"). Evaluate ALL of these attributes:

| Quality Attribute | Key Questions |
|---|---|
| **Modularity** | Are components well-bounded? Can they be independently deployed? Is there rule-enforced dependency direction (not just team agreement)? |
| **Modifiability / Maintainability** | Can features change without cascading edits, unclear ownership, or frequent regressions? |
| **Scalability** | Can the system handle 10x/100x growth in traffic, data, developers, services, and release frequency? What are the bottlenecks? |
| **Performance Efficiency** | Can SLOs be met as load and data grow without constant "hero scaling"? |
| **Reliability / Resilience** | Can the system deliver acceptable service under faults and partial failures? Are SLIs/SLOs defined? |
| **Security** | Are confidentiality/integrity/availability protected under realistic threats? AuthN/AuthZ boundaries enforced? Secure-by-design? |
| **Operability** | Can the system be run, debugged, and changed safely? Deployability, observability, incident response? |
| **Extensibility** | How easily can new features be added? Is the system open for extension, closed for modification? |
| **Testability** | Can components be tested in isolation? Are dependencies injectable? Contract tests at boundaries? |
| **Deployability** | Can components be deployed independently? What's the deployment risk? Immutable artifacts? |
| **Observability** | Correlated traces, metrics, and logs across requests and components? Service health visibility? |
| **Cost Efficiency** | Are cost trade-offs conscious and measurable? Is spend visible and attributable? FinOps alignment? |

For each attribute, identify **risks**, **sensitivity points** (small changes cause large quality impact), and **trade-off points** (improving one attribute degrades another).

### Stage 3: Architecture Style Assessment

Evaluate the current style (monolith, modular monolith, microservices, event-driven, layered, hexagonal/ports-and-adapters, CQRS, etc.):

- The key is NOT the label but **whether the structure enforces boundaries and reduces accidental coupling**
- Check for **explicit mechanisms** that enforce boundaries (not just conventions):
  - Clear module/service ownership with rule-enforced dependency direction
  - Data ownership rules (who owns which state, who may read/write, how invariants are protected)
  - Clear contracts at integration points (schemas, versioning, idempotency, error semantics)

**Architecture style trade-offs to evaluate:**
- Monolith → simpler operations, faster initial delivery, harder to scale teams/independently
- Modular monolith → bounded modules with enforced dependency rules, good middle ground
- Microservices → independent deployment/scaling, BUT increased operational overhead, distributed failure modes, network complexity
- Event-driven → decoupled producers/consumers, BUT eventual consistency complexity, ordering challenges, debugging difficulty

**Anti-pattern: Distributed monolith** - many services but tightly coupled releases, shared schemas, and synchronous call chains that fail together. Microservices in name, monolith in practice.

**Default recommendation**: Prefer the simplest option that meets current quality goals with an explicit path to evolve. Adopt irreversible complexity (distribution, custom platforms) only when constraints clearly justify it.

### Stage 4: Component Boundaries and Responsibilities

For each component/service/module:
- Does it have a **single clear owner** (team or person)?
- Does it minimize **shared mutable state**?
- Does it prevent **"reach into internals"** (other components bypassing its API)?
- Are business capabilities split such that **simple changes require constant cross-team coordination**? If yes, boundaries are misaligned
- Is ownership obvious from the code structure?

**Boundary alignment test**: Can a routine feature change be completed by one team without coordinating with others? If not, the boundary is likely wrong.

### Stage 5: Communication Patterns and Integration

**5a. Synchronous vs Asynchronous Coupling:**
- Where is synchronous request/response used? Is it justified?
- Synchronous coupling increases failure propagation risk and tail-latency sensitivity
- Are there "chatty" call graphs (many round-trips for single operations)?
- As systems scale: design for failure, manage retries carefully, avoid cascading failures

**5b. API and Contract Design:**
- HTTP APIs: are idempotency semantics defined? Idempotent methods must be retry-safe
- REST claims: do they actually follow REST constraints (stateless, cacheable, layered, uniform interface)?
- API versioning strategy: how are breaking changes managed?
- Error semantics: are error responses consistent, typed, and actionable?
- Contract-first design: do API schemas exist before implementation?

**5c. Integration Patterns:**
- Orchestration (centralized coordinator) vs choreography (event-driven, decentralized) - trade-offs explicit?
- Shared database vs owned database per service - data coupling assessed?
- Event-driven integration: are event envelopes standardized (CloudEvents or equivalent)?
- Webhook/callback patterns: are they idempotent and replay-safe?

### Stage 6: Data and State Management

**6a. Data Ownership:**
- Who owns which data? Who may read? Who may write?
- Are invariants protected at the ownership boundary?
- **Anti-pattern: Un-owned data** - multiple components writing the same business facts → unclear conflict resolution, hidden coupling through data replication

**6b. Consistency Model:**
- Which operations require **strong consistency** (and why)?
- Which can tolerate **eventual consistency** with compensations and UX design?
- CAP theorem awareness: under network partitions, you must trade consistency vs availability - is this trade-off explicit?
- Are consistency requirements documented per data domain?

**6c. Distributed Transactions and Sagas:**
- Do business workflows span multiple independently owned data stores?
- If yes: is a saga pattern (or equivalent) implemented with compensating transactions?
- Are saga handlers **idempotent and replay-safe**?
- Is there **end-to-end traceability** across saga steps (correlation IDs, distributed tracing)?
- Are failure/compensation paths tested?

**6d. Schema and Contract Evolution:**
- How are breaking changes to data schemas and API contracts managed?
- Is the **parallel change / expand-and-contract** pattern used for safe migrations?
  - Phase 1 (Expand): introduce new structure alongside old
  - Phase 2 (Migrate): dual write/backfill, shift reads
  - Phase 3 (Contract): remove old structure
- Is backward compatibility a release requirement for critical interfaces?
- Are there migration tests for schema/event evolution?

**6e. Eventing and Interoperability:**
- If event-driven: are event envelopes standardized (CloudEvents spec or equivalent)?
- Is event metadata consistent (source, type, time, correlation ID)?
- Are consumers resilient to schema evolution of events?

### Stage 7: Deployment Topology and Operability

**7a. Twelve-Factor Alignment:**
Check these deployability principles (language-agnostic baseline):
- Configuration separated from code (env vars, config service)?
- Backing services treated as attached resources (swappable)?
- Stateless processes where possible?
- Dev/staging/production environment parity?
- Immutable deploy artifacts?
- Port binding for self-contained services?
- Fast startup and graceful shutdown?

**7b. Runtime vs Architecture Alignment:**
- Does the **runtime deployment topology** actually match the intended architecture?
- Are there components that are architecturally separate but operationally coupled (shared hosts, shared DBs, coupled deployments)?
- Can components be deployed independently with confidence?

**7c. Cloud Well-Architected Alignment:**
Evaluate against the convergent pillars (AWS/Azure/GCP all use similar frameworks):
- **Security**: identity, access control, data protection, incident management
- **Reliability**: fault tolerance, recovery, scaling, SLOs
- **Performance Efficiency**: right-sizing, caching, CDN, data locality
- **Cost Optimization**: visibility, attribution, right-sizing, waste elimination
- **Operational Excellence**: automation, monitoring, runbooks, deployment safety

### Stage 8: Reliability, Resilience, and Fault Handling

**8a. SLIs, SLOs, and Error Budgets:**
- Are **SLIs** defined for the most important user journeys (latency, error rate, throughput, availability)?
- Are **SLOs** set as explicit, measurable reliability targets?
- Is there an **error budget policy** governing the reliability/velocity trade-off?
- Are SLOs connected to operational decisions (freeze releases when budget exhausted)?

**8b. Resilience Patterns:**
- **Timeouts**: are all external calls bounded by timeouts? What happens on timeout?
- **Retries**: bounded retries with exponential backoff and jitter? Max retry limits?
- **Circuit breakers**: do critical call paths have circuit breakers to stop cascading failures?
- **Bulkheads**: are failure domains isolated? Can one dependency's failure take down the whole system?
- **Graceful degradation**: can the system serve partial functionality when a dependency is down?
- **Anti-pattern: Retry storms** - unbounded or poorly tuned retries amplifying outages. Flag this explicitly

**8c. Incident Response and Learning:**
- Are there **postmortems** (blameless, written records of incidents, root causes, follow-ups)?
- Is incident response documented in runbooks tied to alert triggers?
- Are operational lessons fed back into architecture decisions?

### Stage 9: Observability Architecture

Observability is NOT "add logs." It requires correlation across requests and components:

- **Distributed tracing**: correlated traces across service boundaries (OpenTelemetry or equivalent)?
- **Metrics**: service-level (latency, error rate, throughput) AND business-level metrics?
- **Structured logging**: correlated with traces via shared context propagation (trace IDs, correlation IDs)?
- **Dashboards and alerting**: tied to SLOs and service health (not just infrastructure utilization)?
- **Diagnosis capability**: can an on-call engineer trace a user complaint through the full request path?
- **Vendor-neutral**: is the observability stack portable (OpenTelemetry standard)?

### Stage 10: Security Posture (Architecture Level)

Evaluate architecture-level security as a structural property, not just code-level:

- **AuthN/AuthZ boundaries**: are identity and authorization enforced at every service-to-service edge?
- **Least-privilege enforcement**: are services and components running with minimal required permissions?
- **Secure-by-design**: are design flaws addressed (not just relying on "secure coding")?
- **Security observability**: security regression tests, dependency vulnerability scanning, configuration drift controls?
- **OWASP baseline**: OWASP Top 10 awareness plus ASVS-style verifiable requirements mapped to risk/compliance needs?
- **NIST CSF 2.0 alignment**: GOVERN, IDENTIFY, PROTECT, DETECT, RESPOND, RECOVER functions covered?

### Stage 11: Testing Strategy (Architecture Alignment)

Testing defines how safely the system can evolve - it IS an architecture concern:

- **Test pyramid**: majority fast unit tests, smaller numbers of integration/UI tests?
- **Contract tests**: at every integration boundary (API, event, schema)?
- **Resilience tests**: for failure modes (chaos engineering, fault injection)?
- **Migration tests**: for schema/event evolution and backward compatibility?
- **Performance tests**: for SLO validation under load?
- **Security regression tests**: for auth, injection, access control?
- Does the testing strategy align to the **architecture's actual risk points**?

### Stage 12: Cost Efficiency and FinOps

- Is cost treated as a **first-class architectural attribute** (not an afterthought)?
- Are cost trade-offs **conscious and documented** (cost vs quality vs speed)?
- Is infrastructure spend **visible and attributable** to teams/services/features?
- Is there a cost governance operating model (FinOps-style)?
- Are there mechanisms to prevent **accidental overspend** (resource limits, alerts, right-sizing)?
- Are cost implications included in ADRs for major decisions?
