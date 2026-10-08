---
name: architecture
description: >-
  Software architecture analysis, design and implementation: system purpose and constraints, quality attributes
  (ATAM), architecture style, component boundaries, integration and API contracts, data ownership and consistency,
  deployment topology, resilience, observability, testing strategy, cost and ADRs (ISO 42010, arc42, C4,
  twelve-factor, well-architected). Use for architecture reviews, system design, module structure, comparing
  architectural options, writing ADRs and architectural refactoring.
---

# Architecture

Evaluate, design and recommend architectural decisions with structured methods: ISO/IEC/IEEE 42010 architecture
description, ATAM quality-attribute analysis, arc42 documentation, C4 diagrams, cloud well-architected frameworks,
SRE practices and FinOps principles. Be thorough, make every trade-off explicit, and base each conclusion on
evidence from the code, configuration and documents.

## Mode

This skill both analyses and implements. Analyse and report by default. When the request is to build, fix or
change something, or carries `#fix`, also make the changes: fix Critical and High findings directly and list
Medium and Low ones in the report.

## What to cover

- Evaluate **system architecture** and component design
- Analyse **design patterns** and their appropriate application
- Assess **scalability**, **modularity**, and **extensibility**
- Analyze **coupling and cohesion** between components
- Analyse **API design**, contract definitions, and versioning strategy
- Evaluate **data architecture**, ownership, consistency models, and state management
- Assess **integration patterns** and communication protocols (sync/async, orchestration/choreography)
- Analyse **infrastructure architecture**, deployment topology, and environment parity
- Evaluate **reliability and resilience** mechanisms (SLOs, circuit breakers, retry strategies)
- Assess **observability** architecture (traces, metrics, logs, correlation)
- Analyse **cost efficiency** and FinOps alignment
- Evaluate **team topology alignment** (Conway's Law, cognitive load, ownership boundaries)
- Assess **testing strategy** alignment to architectural risk points
- Review **security posture** at the architecture level (authn/authz boundaries, secure-by-design)

## Review Inputs to Gather

Before analysis, gather or identify the absence of these artifacts - missing artifacts are findings:

- **Architecture description** with scope, context, constraints, and quality goals (arc42-style or equivalent)
- **Diagram set**: C4 diagrams (Context → Container → Component) or equivalent multi-level views
- **Decision log**: Architecture Decision Records (ADRs) for significant choices
- **Operational state**: deployments, environments, SLOs/SLIs, incident history, cost drivers
- **API contracts**: OpenAPI/GraphQL schemas, versioning policy, idempotency rules
- **Data model**: ownership rules, consistency requirements, migration strategy
- **Team structure**: who owns what, communication patterns, cognitive load assessment

## Workflow

Work through these stages. The questions and checks for each are in [reference/analysis-stages.md](reference/analysis-stages.md):
read all of it for a full-scope review, or only the stages that match a focused scope.

1. System Purpose, Scope, and Constraints
2. Quality Attribute Analysis (ATAM-style)
3. Architecture Style Assessment
4. Component Boundaries and Responsibilities
5. Communication Patterns and Integration
6. Data and State Management
7. Deployment Topology and Operability
8. Reliability, Resilience, and Fault Handling
9. Observability Architecture
10. Security Posture (Architecture Level)
11. Testing Strategy (Architecture Alignment)
12. Cost Efficiency and FinOps

## Reference

| File | Contents | Read |
|---|---|---|
| [reference/analysis-stages.md](reference/analysis-stages.md) | The analysis stages in full | Before analysing: all of it for a full-scope review, only the matching stages for a focused one |
| [reference/decisions-and-anti-patterns.md](reference/decisions-and-anti-patterns.md) | ADR categories, decision comparison framework, anti-patterns to hunt for, scalability and evolution, cross-cutting concerns checklist | Every analysis (the anti-pattern hunt and cross-cutting checklist are mandatory), and when comparing options or writing ADRs |
| [reference/report-template.md](reference/report-template.md) | The full architecture assessment report template | Before writing the report |

## Output Format

Write the report with the template in [reference/report-template.md](reference/report-template.md).

## Guidelines

- Favor composition over inheritance
- Prefer clear boundaries between components with enforced dependency direction
- Apply SOLID principles at the component level
- Consider the Strangler Fig pattern for incremental migrations - never recommend big-bang rewrites
- Document significant architectural decisions as ADRs (short, with context and consequences)
- Always consider the team's capacity, cognitive load, and communication structure when recommending changes
- Prefer the simplest option that meets quality goals with an evolution path - avoid premature complexity
- Never recommend patterns by popularity - compare on consistent, scored criteria
- Treat data ownership and consistency as first-class decisions, not implementation details
- Treat cost as a first-class attribute - include cost implications in every significant decision
- Check that runtime deployment matches intended architecture (not just code structure)
- Design for failure at integration points - assume dependencies will be temporarily unavailable
- Warn against retry without backoff, missing circuit breakers, and unbounded call chains
- Flag architectural drift (implementation diverging from constraints) as a real risk
- Ensure observability is correlated (traces+metrics+logs), not just "we have logs"
- Validate that testing strategy matches architectural risk points (boundaries, failure modes, migrations)
- Architecture must match how work happens - check Conway's Law alignment

## Follow-ups

After the report, offer these next steps in one line each; do not start them unprompted:

- Implement the architectural changes outlined in the report.
- Analyse the architecture for security concerns with the `security` skill.
- Check the performance implications with the `performance` skill.
