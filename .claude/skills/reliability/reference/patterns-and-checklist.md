# Reliability: patterns and checklist

## Reliability Patterns Reference

| Pattern | When to Use | Implementation | Key Pitfall |
|---------|-------------|----------------|-------------|
| **Retry with Backoff + Jitter** | Transient failures (network, throttling, 429s) | Exponential backoff + random jitter + max retries + max elapsed time | Retry storms without jitter/caps; compounding across layers |
| **Circuit Breaker** | Cascading failure prevention | Closed → Open (after N failures) → Half-Open (probe); monitor trips | No fallback when open; threshold too sensitive/insensitive |
| **Bulkhead** | Resource isolation per dependency | Separate thread/connection pools per dependency class | Shared pools where one slow dep starves all |
| **Timeout + Deadline Propagation** | Every external call | Each hop timeout < caller's remaining budget; cancel on expiry | Missing timeouts; deadlines not propagated downstream |
| **Fallback / Degradation** | Degraded service delivery during incidents | Cache, default values, reduced retrieval, safe minimal mode | All-or-nothing; no degradation path designed |
| **Idempotency** | Safe retries for side-effecting operations | RFC 9110 semantics + idempotency keys + server-side dedup | Retries without idempotency → duplicate side effects |
| **Retry Budget** | Service mesh / multi-layer retry control | Cap fraction of requests that can be retries (Envoy) | Unbounded retries at proxy + app + SDK layers |
| **Health Probes** | Availability monitoring in orchestrated environments | Liveness + readiness + startup probes (separated) | Single "health" endpoint; no readiness/liveness separation |
| **Graceful Termination** | Safe shutdown during deploys/scale-down | Stop new traffic → drain in-flight → SIGTERM → grace period | preStop timing relative to grace period; no drain |
| **Durable Execution** | Multi-step AI workflows (plan → retrieve → tool → answer) | Event history + deterministic replay; idempotent steps | Naive restart re-runs all steps; duplicated tool calls |
| **Error Budget Policy** | Balancing reliability vs. innovation velocity | Track burn rate; freeze launches when budget exhausted | No policy enforcement; budget is just a dashboard number |

## Failure Mode Checklist

### Cascading Failures and Overload
- [ ] Overload behavior explicitly designed (load shedding, degradation, not "try everything and collapse")
- [ ] 429 / throttling retry storms identified and capped across all layers
- [ ] Partial outage handling verified (outlier ejection, circuit breakers)
- [ ] Dependency fan-out bounded under degraded conditions
- [ ] Slow dependency doesn't saturate shared thread/connection pools

### Network and External Calls
- [ ] Timeouts on every external call (LLM, retrieval, DB, tools)
- [ ] Deadline propagation end-to-end (each hop < caller's remaining budget)
- [ ] Connection timeouts AND read/write timeouts configured separately
- [ ] DNS resolution failures handled
- [ ] Partial response handling (especially streaming)
- [ ] Connection pool exhaustion mitigated with limits

### Retries and Circuit Breakers
- [ ] Retries use exponential backoff + jitter + max retries + max elapsed time
- [ ] Cross-layer retry multiplication identified and bounded (retry budgets)
- [ ] Circuit breakers on all remote dependency calls
- [ ] Fallback behavior defined for every circuit breaker (cache, degraded, default)
- [ ] Circuit breaker trips monitored and alerted

### Data Integrity and Idempotency
- [ ] All side-effecting operations are idempotent (or protected by idempotency keys)
- [ ] Transaction boundaries explicitly defined with appropriate isolation
- [ ] Transactional failures retried safely with idempotency
- [ ] Dedup tables / idempotency key storage in place for non-idempotent operations
- [ ] Reconciliation jobs for critical data paths
- [ ] Multi-step workflows use durable execution or checkpointing

### Health and Lifecycle
- [ ] Liveness, readiness, and startup probes are separated and correctly configured
- [ ] Graceful termination: SIGTERM handled, in-flight work drained within grace period
- [ ] preStop timing accounted for relative to terminationGracePeriodSeconds
- [ ] Shutdown mode readiness gating stops new traffic quickly

### Resource Exhaustion
- [ ] Memory limits configured per container/process
- [ ] Connection pool limits set per dependency
- [ ] Queue size limits with backpressure
- [ ] Thread/goroutine/worker limits enforced
- [ ] Disk space monitoring and alerting

### Recovery and Validation
- [ ] Backup integrity verified via periodic restore drills (not just "we take backups")
- [ ] RTO/RPO objectives defined and tested
- [ ] Chaos experiments planned for critical failure modes (429 injection, dependency drop, pod kill, retry storm)
- [ ] Failed chaos experiments tracked as prioritized reliability work
- [ ] Error budget burn tracked across chaos runs to measure improvement
