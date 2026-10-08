# Performance: analysis stages

## Contents

- Analysis Process (12 Stages)
  - Stage 1: SLO Definition and Performance Budgets
  - Stage 2: Distributed Tracing and Observability Baseline
  - Stage 3: CPU and Memory Profiling
  - Stage 4: Algorithmic Complexity Analysis
  - Stage 5: Model-Call Latency and Token Economics (AI/LLM Workloads)
  - Stage 6: Database and Query Performance
  - Stage 7: Retrieval and Vector Search Performance (AI/RAG Workloads)
  - Stage 8: Concurrency, Batching, and Parallelization
  - Stage 9: Memory, Allocations, and GC Pressure
  - Stage 10: Caching Strategy and Invalidation
  - Stage 11: Rate Limiting, Backpressure, and Overload Protection
  - Stage 12: Benchmarks, Load Tests, and Quality Validation

## Analysis Process (12 Stages)

### Stage 1: SLO Definition and Performance Budgets
- Define explicit SLOs (e.g., "p95 TTFT < 800ms, p95 TTLB < 8s, error rate < 0.5%")
- Without SLOs, optimization is guesswork - teams "optimize the wrong thing" and tail latency regressions slip into production
- Break the end-to-end SLO into **per-stage budgets** - which stages consume the most budget?
- Maintain SLO dashboards and on-call thresholds

### Stage 2: Distributed Tracing and Observability Baseline
- Performance engineering is impossible without **end-to-end visibility** across distributed components
- Instrument with OpenTelemetry (or equivalent) - trace IDs propagated across all boundaries (HTTP/gRPC/message)
- Emit **span-level timing** for: retrieval, DB/tool calls, model calls, serialization, queueing, postprocessing
- Context propagation is what preserves causal relationships across process and network boundaries
- Without tracing: you cannot reliably identify whether the bottleneck is queueing, retrieval, model inference, serialization, or a dependency
- **Typical first-cycle impact**: 10–50% latency reduction because the "real" hot path becomes obvious (N+1 queries, repeated retrieval, cache misses)

### Stage 3: CPU and Memory Profiling
- Add CPU + allocation profiling to staging; enable short production captures during incidents
- AI orchestration services often spend surprising CPU in JSON handling, prompt templating, regex parsing, ranking, or policy filters
- Without profiles: you may "optimize" the model call while the bottleneck is actually in the app

**Language-specific profiling tools:**
- **Go**: `go tool pprof` for CPU and heap profiles - heap profiles show live allocations and detect leaks
- **Python**: `tracemalloc` for allocation tracing and snapshot comparison - shows where objects were allocated (file/line)
- **Node.js**: V8 profiler, `--inspect` with Chrome DevTools for CPU and heap snapshots
- **Java/.NET**: JFR/async-profiler (Java), dotnet-trace/dotnet-dump (.NET)
- **Linux system-level**: `perf stat`, `perf record`, `perf report` for kernel-level cycles and hotspots
- **Allocator profiling**: jemalloc heap profiling for leak checking in long-lived services

### Stage 4: Algorithmic Complexity Analysis
For every hot-path operation, determine time and space complexity:
- Identify O(n²) or worse in loops, especially nested loops over growing datasets
- Check for hidden quadratic behavior: repeated string concatenation, repeated list scans, O(n) lookups that should be O(1) hash lookups
- Verify pagination/streaming for large result sets (no unbounded collection loading)
- Check sort/search operations use appropriate algorithms for the data size
- **Prefer algorithmic improvements over micro-optimizations** - fixing O(n²) → O(n log n) beats any micro-opt

### Stage 5: Model-Call Latency and Token Economics (AI/LLM Workloads)

**5a. Input Token Reduction:**
- Excessive input tokens directly increase latency AND cost - "use fewer input tokens" is a major lever
- Implement a **prompt budget strategy**:
  - Summarize or compress long conversation history (keep only necessary state)
  - Trim retrieved context to only relevant chunks (don't dump entire documents)
  - Remove redundant instructions, examples, or schemas from prompts
  - Ensure retrieval happens only when needed (don't default to LLM/retrieval for every request)
- **Typical improvement**: 20–60% latency reduction and proportional cost reduction when prompts are reduced materially
- **Risk**: over-truncation can reduce answer quality - require evals to validate "faster" didn't become "worse"

**5b. Output Token Control:**
- Set `max_tokens` close to the real required output length
- Use stop sequences / structured outputs to limit runaway generation
- Smaller `max_tokens` reduces tail latency (fewer pathological long generations)
- **Strongest impact on p95/p99**
- Must design UX for "continue" behavior if genuinely long content is needed

**5c. Streaming:**
- Streaming (SSE, `stream: true`) reduces TTFT dramatically even if total time is unchanged
- Treat TTFT as a first-class SLO
- **Trade-offs**: streaming complicates logging, retries, usage accounting, and cancellation handling

**5d. Prompt Caching:**
- Provider-side prompt caching works for repeated prompt prefixes (system prompt, policies, tool schemas)
- Can reduce latency substantially and input token costs significantly for high-cache-hit workloads
- Structure prompts so stable content is truly stable: consistent prefix ordering, avoid injecting timestamps/noise into cached prefixes
- Less flexibility to inject per-request variability into system prompts

**5e. Request Reduction:**
- Fewer model calls = lower latency + lower cost
- Batch multiple operations into single requests where possible
- Use caching to avoid redundant model calls for repeated queries
- Parallelize independent model calls (see Stage 8)

### Stage 6: Database and Query Performance

**6a. N+1 Query Detection:**
- N+1 patterns cause massive DB load amplification - O(n) queries where 1-2 would suffice
- Switch to eager/batched loading for relationships (`selectinload()`, `joinedload()`, `include()`, or equivalent)
- Verify query count via tracing spans and DB query logs
- **Typical improvement**: 5×–50× fewer DB round trips on list-like requests; major p95 reduction

**6b. Query Plan Analysis:**
- The query planner's chosen plan is critical for performance
- Use `EXPLAIN (ANALYZE, BUFFERS)` to identify sequential scans, bad joins, missing indexes
- Add/adjust indexes based on observed plans
- **Typical improvement**: 10×+ for hot queries moving from full scans to index-driven execution
- **Trade-off**: indexes add write amplification and storage cost - careless indexing hurts write-heavy workloads

**6c. Connection Pooling:**
- Opening many DB connections is expensive - connection storms degrade performance
- Use PgBouncer (or equivalent pooler) with appropriate pooling mode (session/transaction/statement)
- Ensure pooling mode compatibility with application transaction semantics
- **Impact**: lower DB CPU overhead, better throughput under bursty traffic, fewer timeout incidents

### Stage 7: Retrieval and Vector Search Performance (AI/RAG Workloads)

- Retrieval latency adds directly to end-to-end latency - frequently the bottleneck at scale
- **ANN indexing**: use HNSW, FAISS, ScaNN, or equivalent for sub-linear approximate nearest neighbor search
  - HNSW provides logarithmic scaling (under typical conditions) vs brute-force linear scan
  - GPU-accelerated search (FAISS) targets billion-scale retrieval
  - ANN is a speed/accuracy trade - benchmark recall vs latency explicitly
- **Poor indexing makes p95/p99 blow up under load** - often order-of-magnitude improvement vs brute force as data grows
- **Trade-offs**: approximate search can reduce recall; indexing requires memory and build time; parameter tuning is non-trivial

### Stage 8: Concurrency, Batching, and Parallelization

**8a. Parallelize Independent Operations:**
- Sequential dependency calls multiply latency - parallelization is a core lever
- Parallelize: retrieval + policy checks + non-dependent tool prefetch + independent model calls
- Use bounded concurrency (semaphores, concurrency limits) to avoid overloading dependencies
- Use tracing spans to prove improvements
- **Typical improvement**: 20–50%+ latency reduction if multiple independent calls existed

**8b. Avoid Blocking I/O in Hot Paths:**
- Node.js: synchronous filesystem/network APIs block the event loop - use async equivalents
- Python asyncio: blocking operations (including some logging) can block the event loop - offload to worker pools
- Replace blocking operations with async equivalents or move to thread/process pools
- **Impact**: prevents latency spikes under load, increases throughput for async servers

**8c. Batching:**
- Batch multiple small operations into single requests where protocols support it
- DB: batch inserts/updates, use bulk APIs
- Model API: batch inference requests where supported
- Queue consumers: process messages in batches with bounded batch size

### Stage 9: Memory, Allocations, and GC Pressure

**9a. Unbounded Collection Growth:**
- Chat/session state (conversation history, retrieved docs, tool results) can grow without bound
- **O(unbounded_history + unbounded_retrieval)** space growth → GC pressure, OOM kills, severe tail-latency spikes
- Put **hard caps everywhere**: bound conversation history (summaries + windowing), bound retrieval result sets, bound caches with eviction policies
- **Impact**: stabilizes p95/p99, prevents "it gets slower over time" incidents

**9b. String/Prompt Construction:**
- Repeated string concatenation in loops creates O(n²) allocation behavior
- Use builder/join patterns, pre-size buffers, avoid repeated copies of large strings (especially prompt templates)
- Noticeable in CPU + allocation profiles when prompts are large or built frequently

**9c. Leak Detection:**
- Without snapshot-based leak detection, slow leaks ship and only appear under long uptimes
- Add leak checks to staging soak tests:
  - Python: periodic `tracemalloc` snapshots + diff
  - Go: periodic heap profiles and compare
  - jemalloc profiling for long-lived allocations in C/C++/Rust services
- **Prevents a class of high-severity production incidents**

**9d. Cache Memory:**
- In-process caches without size limits or eviction grow unboundedly
- Always set max entries / max memory, LRU/LFU eviction, explicit TTLs
- Monitor cache memory usage alongside hit rates

### Stage 10: Caching Strategy and Invalidation

**10a. Layered Caching:**
- L1: in-process cache for tiny, very hot keys (short TTL)
- L2: distributed cache (Redis, Memcached) for retrieval results, embeddings, expensive computations (cache-aside pattern)
- L3: CDN / edge cache for static assets and API responses where applicable
- Provider-level: prompt caching for repeated LLM prefixes

**10b. Cache-Aside vs Write-Through:**
- Cache-aside (lazy loading): fill cache on demand on cache miss - standard for read-heavy workloads
- Write-through: update cache on every write - ensures cache is always fresh but adds write latency
- Choose based on read/write ratio and staleness tolerance

**10c. Invalidation Strategy:**
- If invalidation is wrong, you serve stale/corrupt answers - "fast but wrong" is a failure
- **TTL-only**: acceptable only if staleness is tolerable (not for correctness-critical data)
- **Write-through / write-around**: fresh on writes but adds complexity
- **Explicit invalidation on writes/events**: preferred for correctness-critical data (requires eventing or CDC)
- Always monitor: hit rates, miss rates, stale-serve rates, invalidation latency

**10d. AI-Specific Caching:**
- Cache embeddings for repeated queries
- Cache top-k retrieval results for high-frequency intents (invalidate on corpus updates)
- Cache tool/function call results when inputs are identical and results are deterministic
- **Typical improvement**: 2×–10× reduction in retrieval load for repeated intents

### Stage 11: Rate Limiting, Backpressure, and Overload Protection

**11a. Provider Rate Limits and Retries:**
- Implement retries with **random exponential backoff + jitter** for rate-limit errors (429s)
- Respect provider SDK default retry behavior (e.g., OpenAI SDK retries 429s and >=500s with exponential backoff)
- Set explicit **max_retries** and **timeout** values - never retry unboundedly
- **Retry storms**: if retries at multiple layers multiply (5 layers × 3 retries = 243× load), the system collapses - flag this explicitly

**11b. Client-Side Request Shaping:**
- Token/request budgets per user/tenant
- Concurrency caps (max in-flight requests)
- Adaptive rate limiting based on provider response latency

**11c. Overload Controls:**
- **Admission control**: max in-flight requests - reject when at capacity
- **Load shedding**: return degraded responses under extreme load rather than cascading failure
- **Circuit breakers**: stop calling failed dependencies; allow recovery (Envoy patterns)
- **Graceful degradation**: serve partial functionality when a dependency is down
- Must define "acceptable degradation" - which features can be temporarily disabled?
- **Impact**: better p99 and uptime during spikes; fewer cascading failures

### Stage 12: Benchmarks, Load Tests, and Quality Validation

**12a. Load Testing Harness:**
- Realistic traffic mix (not just single-endpoint hammering)
- Concurrency ramp: find saturation point
- Soak tests: long-duration tests for leak/drift detection
- Track p50/p95/p99 latency - tail latency as saturation indicator
- Track error rates (429, timeouts, 5xx), retry counts, backoff behavior

**12b. Quality Evals (AI workloads):**
- Structured tests for correctness/groundedness on representative dataset
- Regression tests for prompt/model changes - ensure "faster" didn't become "worse"
- Production-to-dataset loop: add real failures as new eval cases
- Evals are the guard that allows aggressive optimization - regressions are caught early

**12c. Stage-Level Benchmarking:**
- Per-stage latency: retrieval, DB, tool calls, model, postprocessing, queueing
- Per-stage resource: CPU, memory, I/O per stage
- Identify which stage consumes the most of the performance budget

**12d. Continuous Performance Regression:**
- Integrate load tests and evals into CI/CD
- Alert on p95/p99 regressions between releases
- Track tokens per request, queries per request, cache hit rates over time
