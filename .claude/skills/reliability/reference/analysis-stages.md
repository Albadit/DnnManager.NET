# Reliability: analysis stages

## Contents

- Analysis Process (12 Stages)
  - Stage 1: SLO Definition and Error Budget Policy
  - Stage 2: Alerting on SLO Burn Rates
  - Stage 3: Cascading Failure Analysis
  - Stage 4: Timeout and Deadline Propagation
  - Stage 5: Retry Logic - Backoff, Jitter, Caps, and Retry Budgets
  - Stage 6: Circuit Breakers
  - Stage 7: Bulkhead Isolation
  - Stage 8: Idempotency and Safe Side Effects
  - Stage 9: Transaction Boundaries and Data Consistency
  - Stage 10: Durable Execution for Multi-Step Workflows
  - Stage 11: Health Checks, Probes, and Graceful Lifecycle Management
  - Stage 12: Recovery Engineering and Chaos Readiness

## Analysis Process (12 Stages)

### Stage 1: SLO Definition and Error Budget Policy
- Verify explicit SLOs exist for each workload class (availability, latency p95/p99, correctness proxy)
- It is unrealistic and undesirable to insist SLOs are met 100% of the time - instead define an **error budget** (how much unreliability you can "spend")
- Changes are a major source of instability (~70% of outages tied to changes) - reliability requires controlling release risk, especially for fast-moving AI prompts, models, and toolchains
- Error budgets are the tool to balance reliability vs. innovation: track budget burn and enforce policy when budget is exhausted
- **Error budget policy**: when budget is burning quickly, reduce change velocity, shift engineering to reliability defects, and/or freeze risky launches until back in budget
- Without SLOs: optimization and alerting target the wrong thing (e.g., CPU) while users see failures (timeouts, partial answers, tool errors)

### Stage 2: Alerting on SLO Burn Rates
- Alerts must fire on **SLO burn and significant error-budget consumption** - not infrastructure metrics ("CPU > 80%")
- Threshold alerts on infra metrics have low precision (fire on noise) and low recall (miss real user-impact events)
- Use **multi-window burn-rate alerts** to detect both "fast catastrophes" and "slow burns"
- Route alerts to incident response + rollback playbooks
- Goal: be notified for events that consume a large fraction of the error budget

### Stage 3: Cascading Failure Analysis
- AI applications are inherently dependency-heavy (LLM APIs, retrieval stores, databases, tool APIs, streaming) - susceptible to **cascading failures** where partial failure amplifies load and causes more failures
- Overload is the most common cause of cascading failure - failure grows over time due to positive feedback
- **AI-specific cascade triggers**:
  - Provider throttling (429s) → naive retries → retry storms → amplified load (thundering herd)
  - Slow dependency (vector search, database, tool API) → threads saturate → queue growth → timeouts → more retries
  - Partial outages (some hosts bad) → load balancer keeps sending traffic to them unless outliers are ejected
- For each dependency boundary, verify: What happens when this dependency is slow? Down? Throttling? Partially failed?
- It is better to allow some user-visible errors or lower-quality results than try to fully serve every request past the breaking point
- Design explicitly for overload and degraded results

### Stage 4: Timeout and Deadline Propagation
- Deadlines are not optional in distributed systems - they cap resource consumption
- **Every external call** (LLM client, retriever, DB, tool client) must have a timeout
- Enforce **deadline propagation end-to-end**: each hop must have a timeout shorter than the caller's remaining budget, so downstream work is bounded
- If clients set unrealistically short deadlines, servers waste resources on work that will be discarded
- gRPC deadlines allow specifying how long a client will wait; when expired, the call is canceled
- When a request is canceled/deadline-exceeded: **stop ongoing work immediately** and return a clear degraded response - do not continue computation
- **Without deadline propagation**: calls hang or run too long → thread/worker exhaustion → queue buildup → cascading timeout upstream

### Stage 5: Retry Logic - Backoff, Jitter, Caps, and Retry Budgets

**5a. Retries with Exponential Backoff + Jitter:**
- Retries are dangerous without controls - retry storms amplify a partial outage or 429 throttling into a full outage
- Use **limited retries** with **exponential backoff + jitter**
- **Cap retries** (max retry count) and **cap total retry time** (max elapsed) - never retry unboundedly
- For API throttling (429s): use **random exponential backoff** and stop after a maximum number of retries
- **Jitter is mandatory** - without it, many clients retry simultaneously (synchronized retry storm)
- When rate limiting persists: move to queueing, shed load, or serve degraded results rather than continuing retries

**5b. Retry Storm Amplification Across Layers:**
- If retries exist at multiple layers (proxy + app + SDK), they compound: 5 layers × 3 retries = 243× load
- **Flag this explicitly** in every analysis - compounding retries are a top cascading failure cause
- Enforce **retry budgets** (especially in service mesh / Envoy): limit the fraction of requests that can be retries
- Monitor retry-overflow metrics; during incidents, reduce retries, increase backoff, and shed low-priority traffic

**5c. Idempotency Prerequisite:**
- Retries are only safe if the retried operation is idempotent (see Stage 8)
- Retries without idempotency guarantees → duplicate side effects (double charges, duplicate tickets)

### Stage 6: Circuit Breakers
- Circuit breakers prevent repeated calls to failing dependencies - fail fast without making the protected call
- Standard states: **Closed** (normal) → **Open** (fail-fast after threshold) → **Half-Open** (probe to test recovery)
- When open: serve **fallbacks** (cached answers, "limited mode," smaller prompt/retrieval, default values)
- Gradually probe in half-open until dependency is restored
- **Monitor and alert on circuit breaker trips** - a tripped breaker is a signal of dependency degradation
- Without circuit breakers: a failing dependency continues to be hammered → threads block on timeouts → cascading failure → self-inflicted outage

### Stage 7: Bulkhead Isolation
- Bulkheads isolate resources into pools so one failing or slow dependency doesn't take down everything (ship bulkhead analogy)
- **Isolate per dependency or per workload class**: separate thread pools, connection pools, concurrency limits
- Without bulkheads: one dependency (e.g., retrieval) consumes all workers/DB connections → unrelated endpoints time out → "everything is slow" incidents
- When a pool is saturated: shed or degrade only the impacted features; keep critical user paths alive
- Particularly important when a system has multiple dependency types (LLM, retrieval, DB, tools) with different failure characteristics

### Stage 8: Idempotency and Safe Side Effects
- Multiple identical requests must have the same intended effect as a single request (RFC 9110 definition)
- Idempotent methods can be retried automatically when communication failures occur before a response is received
- For operations that are **not naturally idempotent** (common in AI tool calls: "create ticket," "charge card," "send email"):
  - Use **idempotency keys** + server-side deduplication (Stripe pattern)
  - Add dedup tables keyed by idempotency key (or deterministic request hash)
  - Implement reconciliation scripts for legacy duplicates
- **Without idempotency**: retries replay side effects → double charges, duplicate tickets, duplicated writes → high user harm, data integrity issues, financial risk, and painful manual cleanup
- Idempotency is the foundation that makes retries safe - assess this before approving any retry logic

### Stage 9: Transaction Boundaries and Data Consistency
- Transaction isolation and retry behavior matter during concurrency
- Under stricter isolation (e.g., Serializable), transactions may be forced to abort/roll back to prevent anomalies - the application **must be able to retry safely** (idempotency + bounded retries)
- Verify:
  - Transaction boundaries are explicitly defined (not implicit or ambient)
  - Isolation level is appropriate for the invariants being protected
  - Transactional failures are retried safely with idempotency guarantees
  - Multi-step writes are atomic or compensable
- **Without explicit boundaries**: inconsistent state due to partial commits, concurrency anomalies, or unsafe retry → "the AI says it succeeded, DB says it didn't"
- Introduce reconciliation jobs and invariant checks for critical data paths

### Stage 10: Durable Execution for Multi-Step Workflows
- AI agents often execute multi-step workflows: plan → retrieve → call tool A → call tool B → finalize answer
- If the process crashes at step N, a naive retry may re-run steps 1..N-1 and cause **duplicated tool actions** unless each step is replay-safe
- Durable workflow systems (e.g., Temporal) formalize this:
  - Workflow execution is a durable, reliable function execution
  - Workflows survive infrastructure failures via event history + deterministic replay
  - On restart: replay completed steps, only execute missing steps
  - All Activities/tool calls must be idempotent
- **Without durable execution**: mid-flight crash forces full restart → repeated LLM/tool calls → duplicate side effects, wasted spend, inconsistent partial results
- For shorter workflows: at minimum, implement checkpointing / step-level state persistence

### Stage 11: Health Checks, Probes, and Graceful Lifecycle Management

**11a. Kubernetes Probes:**
- **Liveness probes**: decide when to restart a container (catches deadlocks - running but unable to make progress)
- **Readiness probes**: decide whether a container should receive traffic (prevents routing to not-ready instances)
- **Startup probes**: delay liveness checks until startup completes (prevents killing slow-starting containers)
- Without probe separation: pods receive traffic when not ready, get restarted unnecessarily, or deadlocks go undetected → elevated error rate during deploys/scale events, cascading failures if bad pods stay in rotation

**11b. Graceful Termination:**
- On shutdown: stop new traffic first → drain ongoing work → then terminate
- Lifecycle: pods removed from service endpoints → preStop hooks run → SIGTERM sent → Kubernetes waits for termination grace period (default 30s)
- The termination grace period countdown begins **before** the preStop hook executes - shutdown logic must be time-bounded
- **Without graceful termination**: in-flight requests are dropped, partial writes occur, streaming responses cut mid-answer without cleanup → user-visible errors during deploys/scale-down, possible data corruption
- Ensure retries are safe (idempotent) so clients can retry dropped requests without duplicates

**11c. Graceful Degradation During Overload:**
- It is better to serve degraded results and drop/load-limit traffic during global overload than try to serve everything beyond the breaking point
- AI-specific degradation strategies:
  - Skip expensive tool calls when dependencies are failing; return partial answer with clear message
  - Reduce retrieval depth (top-k) or disable reranking temporarily
  - Use cached answers for repetitive intents
  - Shift to "safe minimal response mode" when LLM calls fail repeatedly
- Without degradation paths: system attempts full fidelity during outages → collapses entirely → full outage instead of partial functionality

### Stage 12: Recovery Engineering and Chaos Readiness

**12a. Backup Integrity and Recovery Tests:**
- Backup is not reliable unless restore is proven
- Perform **periodic recovery tests** to validate backup integrity and verify that recovery meets RTO/RPO
- Automate restore drills in non-production; document runbooks; validate restored systems can serve production-like traffic
- Without restore drills: backups exist but restores fail → permanent data loss risk or multi-day outage during major incidents

**12b. Chaos Engineering:**
- Chaos engineering is experimenting on a system to build confidence it can withstand turbulent conditions in production
- Your first real test should NOT be a production incident
- **High-value AI chaos experiments**:
  - Inject 429s / 5xx / latency into the LLM provider call path
  - Drop retrieval service connectivity
  - Force partial host failures and validate outlier ejection + circuit breaker behavior
  - Kill pods and confirm readiness prevents routing to not-yet-ready instances
  - Simulate retry storms by throttling at multiple layers simultaneously
  - Inject disk/memory pressure and validate graceful degradation
- Turn failed experiments into prioritized reliability work; track improvements via reduced error-budget burn in subsequent experiments
