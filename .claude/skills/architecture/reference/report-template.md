# Architecture: report template

## Output Format

```
## Architecture Assessment

### System Overview
- System purpose and non-negotiable constraints
- Current architecture style (monolith, microservices, event-driven, etc.)
- Key components, their responsibilities, and ownership
- Communication patterns (sync/async, orchestration/choreography)
- Data ownership and consistency model
- Deployment topology

### Quality Attribute Risk Register
For each quality attribute (modularity, scalability, reliability, security, operability, cost, etc.):
- Current state assessment
- Risks and sensitivity points
- Trade-off points (improving one degrades another)

### Findings
For each finding:
- **Area**: Component/layer affected
- **Issue**: What the concern is (with evidence from code/config)
- **Quality Attributes Impacted**: Which attributes are degraded
- **Risk Level**: High / Medium / Low
- **Recommendation**: Specific improvement with rationale and trade-off analysis
- **Anti-Pattern**: If applicable, which known anti-pattern this matches
- **Migration Path**: How to implement the change safely (expand-contract if needed)

### Architecture Decision Records (ADRs)
For significant decisions:
- **Decision**: What was decided (or should be decided)
- **Context**: Why the decision was needed (quality attributes, constraints, incidents)
- **Options Considered**: With scored comparison (complexity, cost, scalability, maintainability, delivery speed, team fit)
- **Consequences**: Trade-offs accepted, what this rules out
- **Supersedes**: Link to prior decisions if applicable

### Missing Controls Checklist
What evidence/artifacts SHOULD exist but don't:
- Architecture description, diagrams, ADRs
- SLOs, error budget policy, runbooks
- API contracts, versioning policy
- Data ownership documentation
- Cost attribution and governance
- Testing strategy alignment

### Improvement Roadmap

**High-priority fixes** (reduce immediate risk, prevent compounding failures):
- SLIs/SLOs for critical user journeys + error budget policy
- Resilience at integration points (timeouts, bounded retries, circuit breakers)
- Baseline observability (correlated traces/metrics/logs)
- ADRs for architecturally significant decisions
- Security baseline (OWASP/ASVS)

**Medium-term improvements** (increase maintainability, reduce systemic coupling):
- Revalidate service/module boundaries using domain boundaries
- Standardize saga/distributed transaction approach
- Standardize event envelopes (CloudEvents)
- Safe evolution patterns (parallel change / expand-contract)
- Twelve-factor operability alignment

**Long-term strategic changes** (sustained scaling of product, platform, teams):
- Explicit architecture style decision (monolith vs microservices vs hybrid)
- Continuous architecture review (well-architected framework approach)
- Cost governance operating model (FinOps)
- Socio-technical alignment (team topology, cognitive load, Conway's Law)
- Incremental legacy modernization (Strangler Fig)

For each roadmap item: migration strategy, team impact, and breaking change management
```
