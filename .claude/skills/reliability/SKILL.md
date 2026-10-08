---
name: reliability
description: >-
  Reliability and resilience analysis and fixes: SLOs and error budgets, cascading failures, timeouts and deadline
  propagation, retries with backoff and jitter, circuit breakers, bulkheads, idempotency, transaction boundaries,
  durable multi-step workflows, health probes, graceful shutdown and degradation, backup restore drills and chaos
  testing. Use for failure-mode analysis, error-handling reviews, disaster recovery, and implementing resilience
  patterns.
---

# Reliability

Make sure systems are resilient, fault-tolerant and recoverable. Treat reliability as a measurable system property:
defined by SLOs and error budgets, defended by resilience patterns (timeouts, retries, circuit breakers, bulkheads),
validated by chaos experiments and restore drills, and maintained through graceful degradation under overload. Never
judge reliability without examining failure behaviour first.

## Mode

This skill both analyses and implements. Analyse and report by default. When the request is to build, fix or
change something, or carries `#fix`, also make the changes: fix Critical and High findings directly and list
Medium and Low ones in the report.

## What to cover

- Define and enforce **SLOs and error budgets** as the primary reliability control mechanism
- Assess **error handling** completeness and correctness on every code path
- Analyse **retry logic** with backoff, jitter, caps, and retry budgets - flag retry storm amplification across layers
- Evaluate **circuit breaker** patterns (Closed → Open → Half-Open), thresholds, monitoring, and fallback behavior
- Analyze **timeout and deadline propagation** end-to-end - every hop must have a timeout shorter than the caller's remaining budget
- Assess **bulkhead isolation** - resource pools per dependency or workload class
- Check **graceful degradation** strategies (load shedding, reduced retrieval, cached answers, safe minimal mode)
- Analyse **data consistency**, transaction boundaries, isolation levels, and safe retry semantics
- Assess **idempotency** of all side-effecting operations (RFC 9110 semantics + idempotency keys for non-idempotent operations)
- Evaluate **durable execution / checkpointing** for multi-step workflows (replay safety, crash recovery)
- Evaluate **health checks** (liveness, readiness, startup probes) and **graceful termination** (SIGTERM, preStop, drain)
- Assess **backup integrity and recovery** - restores must be periodically tested against RTO/RPO
- Evaluate **chaos engineering readiness** - controlled failure injection experiments
- Analyse **cascading failure dynamics** specific to AI workloads (429 retry storms, slow dependency saturation, partial outages)
- Define **alerting on SLO burn rates** - not infrastructure metrics

## AI-Specific SLIs to Define and Track

Before analysing reliability, establish what "reliable" means with explicit, user-centric SLIs:

- **Availability / success rate**: fraction of "answer requests" returning HTTP 2xx + valid output schema
- **Correctness proxy SLIs**: tool-call success rate, grounded answer rate (measured via evals)
- **Latency SLIs**: time to first token/byte (TTFT/TTFB) and time to final answer (TTLB) - especially for streaming
- **Degradation SLIs**: fraction of requests served from fallback mode (cache, cheaper model, reduced retrieval) during dependency incidents

Define **multiple SLO classes** for heterogeneous workloads (interactive chat vs. batch jobs vs. API consumers) - a single global target hides user-facing pain.

## Workflow

Work through these stages. The questions and checks for each are in [reference/analysis-stages.md](reference/analysis-stages.md):
read all of it for a full-scope review, or only the stages that match a focused scope.

1. SLO Definition and Error Budget Policy
2. Alerting on SLO Burn Rates
3. Cascading Failure Analysis
4. Timeout and Deadline Propagation
5. Retry Logic - Backoff, Jitter, Caps, and Retry Budgets
6. Circuit Breakers
7. Bulkhead Isolation
8. Idempotency and Safe Side Effects
9. Transaction Boundaries and Data Consistency
10. Durable Execution for Multi-Step Workflows
11. Health Checks, Probes, and Graceful Lifecycle Management
12. Recovery Engineering and Chaos Readiness

## Reference

| File | Contents | Read |
|---|---|---|
| [reference/analysis-stages.md](reference/analysis-stages.md) | The analysis stages in full | Before analysing: all of it for a full-scope review, only the matching stages for a focused one |
| [reference/patterns-and-checklist.md](reference/patterns-and-checklist.md) | Resilience patterns with when to use, implementation and pitfalls; failure-mode checklist | When recommending a pattern, and to confirm coverage before writing the report |

## Output Format

For each finding, provide ALL of these fields:

```
### [SEVERITY] Reliability Issue

- **Location**: File and line reference (or system component / dependency boundary)
- **Failure Mode**: What can go wrong (specific scenario, not generic)
- **Blast Radius**: Which users/features/services are affected; upstream/downstream impact
- **Probability**: How likely this is to occur (with reasoning: load patterns, dependency quality, change frequency)
- **Current Handling**: What happens now when this failure occurs
- **Recommendation**: Specific resilience pattern to apply (with implementation details)
- **Recovery**: How the system recovers (automatic vs. manual; time to recovery)
- **Verification**: How to prove the fix works (chaos experiment, load test, or specific metric)
```

After all findings, ALWAYS provide these sections:

1. **SLO assessment** - are SLOs defined? Are they user-centric? Is there an error budget policy with enforcement?
2. **Cascading failure map** - which dependency failures cascade and through what mechanism (retry storm, pool saturation, queue buildup)?
3. **Resilience pattern coverage** - which patterns are present, which are missing, and where compound gaps exist (e.g., retries without idempotency)
4. **Recovery readiness** - backup/restore status, RTO/RPO gaps, chaos experiment coverage
5. **Degradation strategy** - what happens under partial outage: which features degrade, which are shed, what is the "safe minimal mode"?
6. **Priority remediation** - ordered by blast radius × probability, highest first

## Guidelines

- **Assume every external call will eventually fail** - design for failure, not just for success
- **SLOs and error budgets are the reliability control mechanism** - without them, optimization and alerting target the wrong thing
- **Changes are the primary source of instability** (~70% of outages) - assess release risk controls
- Timeouts must be shorter than the caller's timeout (**cascading deadlines**)
- Retries must have **limits, exponential backoff, and jitter** - and the retried operation must be idempotent
- **Flag retry storm amplification explicitly** whenever retries exist at multiple layers
- Circuit breakers must have **monitoring, alerts, and defined fallback behavior**
- **Bulkheads isolate blast radius** - shared pools are a reliability anti-pattern
- **Idempotency is the foundation for safe retries** - assess idempotency before approving retry logic
- For AI workflows: assess **durable execution** for multi-step sequences (plan → retrieve → tool → answer)
- **Graceful degradation is a design requirement**, not an afterthought - define "safe minimal mode"
- It is better to serve degraded results than to collapse trying to serve everything at full fidelity
- **Backup is not reliable unless restore is proven** - periodic restore drills are mandatory
- **Chaos engineering proves failure handling** - unit tests alone are insufficient; inject real failure modes
- Probe separation (liveness vs. readiness vs. startup) prevents cascading failures during deployment
- Graceful termination must be **time-bounded** (preStop timing relative to grace period)
- Every error path should be tested - untested error handling is unreliable error handling
- Document the system's failure modes, degradation strategies, and recovery procedures
- Track reliability improvements via **reduced error-budget burn** in subsequent experiments

## Follow-ups

After the report, offer these next steps in one line each; do not start them unprompted:

- Implement the reliability improvements recommended in the report.
- Analyse the operational implications with the `operations` skill.
