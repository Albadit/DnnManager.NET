# Performance: metrics and red flags

## Performance Metrics to Define and Track

Before optimization, establish what "fast enough" means with explicit metrics:

### Latency Metrics (User-Centric)
- **Time to First Token / First Byte (TTFT/TTFB)**: when the user first sees progress - critical for streaming/chat UX
- **Time to Last Byte (TTLB)**: when the final response arrives - critical for automation, tool-driven agents, and API consumers
- **Tail latency (p95/p99)**: averages hide the slow 1% that destroys trust - track 99th percentile as an early saturation signal
- All latency metrics should be tracked at p50/p95/p99

### Throughput and Cost Metrics
- **Requests per second at a given SLO** and **concurrency** (in-flight requests)
- **Tokens per second** and **tokens per request** (input + output) - both latency and cost are often token-driven
- **Database queries per request**, **cache hit rates**, **tool calls per request**
- **Infrastructure cost per request** (compute, model API, storage, network)

### Quality Metrics (for AI/LLM workloads)
- **Answer correctness / groundedness**, **tool success rate**, **hallucination rate**
- **Task completion rate** and **regression detection** via evals (structured tests)
- Performance optimization must be coupled with quality validation - "faster but wrong" is not an improvement

### Stage Budgets
Define a **performance budget per stage** of the request path:
`gateway + retrieval + DB/tools + model + postprocess + streaming`
Monitor p50/p95/p99 for EACH stage via distributed tracing.

## Performance Red Flags

| Issue | Impact | Solution |
|-------|--------|----------|
| **No SLOs defined** | Optimization is guesswork; regressions ship silently | Define TTFT/TTLB p50/p95/p99 + error rate SLOs |
| **No distributed tracing** | Cannot identify real bottleneck | Instrument with OpenTelemetry, emit span-level timing |
| **No CPU/memory profiling** | Optimizing the wrong thing | Add profiling to staging; short production captures |
| **N+1 Queries** | Exponential DB load | Eager loading / batch queries |
| **Unbounded collections** | Memory exhaustion, GC pressure | Pagination / streaming / hard caps |
| **Synchronous I/O in hot path** | Thread/event-loop blocking | Async I/O / worker pools |
| **Missing indexes** | Slow queries, full scans | Analyze query plans with EXPLAIN, add indexes |
| **String concatenation in loops** | O(n²) allocations, GC pressure | StringBuilder / join / pre-sized buffers |
| **Nested loops on large datasets** | O(n²) or worse | Hash maps / sorting / index-based lookups |
| **No connection pooling** | Connection storm overhead | PgBouncer or equivalent pooler |
| **Loading full objects when partial needed** | Memory/bandwidth waste | Projections / DTOs / field selection |
| **Excessive input tokens** | High latency + cost | Prompt budget, summarize history, trim context |
| **No streaming** | Users wait for full completion | SSE streaming, treat TTFT as SLO |
| **No prompt caching** | Repeated prefix costs | Stable prefix ordering, avoid noise in cached parts |
| **Sequential independent calls** | Multiplied latency | Parallelize with bounded concurrency |
| **Retry without backoff/limits** | Retry storms, cascading failure | Exponential backoff + jitter + max retries + circuit breaker |
| **No overload protection** | Cascading failure under spikes | Admission control, load shedding, circuit breakers |
| **Cache without invalidation strategy** | Stale/corrupt answers | Explicit invalidation, TTL + event-driven purge |
| **No load tests or evals** | Regressions ship silently | Load test harness + quality eval suite in CI/CD |
| **No memory leak detection** | Slow degradation → OOM | Soak tests with tracemalloc/pprof snapshots |
