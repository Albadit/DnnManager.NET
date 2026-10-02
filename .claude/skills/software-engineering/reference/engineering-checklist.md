# Engineering checklist

Use the sections that apply. For each finding record: where (file:line), what happens, the realistic cost or risk
(how often × how expensive, or the concrete exploit / failure), the root cause, and the smallest clean fix. Also note
what is already done well, so it isn't "fixed".

## Performance

Look for:
- work repeated per call/tick/render that could be done once: parsing, serialization, reflection, regex building,
  config/file re-reads, object graphs rebuilt
- unnecessary UI re-renders or full rebuilds of views that could update in place
- duplicate or unnecessary API requests; requests on every keystroke without debounce
- duplicate database queries, N+1 queries (a query per item in a loop), unnecessary joins, `SELECT *` where few
  columns are used, missing pagination, unbounded result sets
- inefficient algorithms on data that grows (nested scans, list removals from the front, repeated sorting)
- blocking calls on UI or request threads (file system, registry, network, database, process start, locks held
  during I/O)
- excessive file-system or network access (walking whole trees, re-downloading, polling remote state)
- large bundles / binaries, eager loading of rarely used modules
- sequential independent operations that could run concurrently (see Concurrency)

Rules: measure or estimate before optimizing (`frequency × cost × data size`); keep the optimized code readable;
don't trade correctness for speed.

## Lightweight resource usage

Check behaviour when **idle, minimized/hidden, running for hours or days, handling large data, managing many items,
and running several operations at once**:
- CPU: timers and polling that run while nothing is shown or nothing changes; animations while invisible
- memory: collections, logs, histories, scrollback and caches that only grow; event subscriptions from short-lived
  objects to long-lived ones (leaks); large objects held after use
- disk and network: writes or downloads repeated without need; temp files not cleaned up
- threads, connections, handles, processes, file watchers: created per use instead of reused; not disposed;
  not bounded
- prefer event-driven (notifications, watchers, push) over polling; when polling is unavoidable, poll only while
  someone needs the result and back off when idle

## Data access and databases

- query efficiency, indexes for real access patterns, joins, selected columns, pagination, batching
- transaction boundaries (as short as correct), connection reuse and pooling, timeouts on every query
- the **authoritative source** of each fact: read it from there rather than copying it into settings or caches
  that go stale
- ownership: application-owned vs external-system data; read-only data treated as read-only
- destructive operations (drop, delete, truncate) limited to what the application owns, scoped to the right
  server/tenant, and confirmed

## API design

- consistent routes, methods and naming; correct status codes; one error response shape
- validation on the server (never only on the client); request/response models that don't leak internals
- permissions on every endpoint; pagination and filtering for lists; idempotency where retries happen

## Caching

- cache only what is expensive and read more than it changes
- every cache has an explicit invalidation (event, version, TTL) and a bound on size
- consider the cheapest layer first: HTTP caching, in-memory, then disk or database caches
- never cache data whose staleness can cause wrong actions without a check

## Concurrency

- run independent I/O concurrently where it shortens what a user waits for
- guard shared state; avoid holding locks during I/O; no sync-over-async on UI threads (deadlocks)
- cancel superseded work (generation counters, cancellation tokens) instead of letting stale results win
- bound parallelism; avoid duplicate work when a request is already in flight (coalesce)

## Startup

- list what runs before the first screen or first request; move everything not needed for it to lazy or background
- no network calls, full scans or database loads on the startup path unless required
- defer optional warm-ups until idle

## Dependencies

For each package: used? maintained? size and transitive weight? does the platform already do this? removable?
Don't add a package for trivial functionality. Keep versions consistent; remove redundant references.

## Error handling

- separate what the **user** sees (what happened, why, what to do next - no stack traces or internals), what the
  **developer** needs (root cause, context, stack trace - in logs), and what is **logged**
- no silent swallowing (`catch {}`) without a reason in a comment; no async-void or fire-and-forget without a handler
- timeouts on external calls (HTTP, SQL, processes); failures of one item don't abort a batch silently
- last-resort handlers for unhandled exceptions on every thread, which log before the app ends

## Logging

- levels used intentionally: Debug (diagnostics), Information (lifecycle), Warning (recoverable), Error (failed
  operation), Critical (app can't continue)
- check that logging actually goes somewhere in every environment
- no noisy logs in hot paths or polling loops - rate-limit or deduplicate repeated messages
- enough context (ids, names, operation) to investigate; **never secrets** (passwords, tokens, connection strings)

## Background processing

For every timer, scheduled job, poller, watcher, worker and long-running task:
- does it need to exist? could it be event-driven?
- does it run too often? does it stop or slow down when idle, hidden or unneeded?
- does it support cancellation and stop cleanly on shutdown?
- does it release resources and survive (and report) its own failures without killing the app?

## Security

- **authentication and authorization** on every entry point; least privilege for the app and its processes
- **input validation** at boundaries (server side); one validation place per concept
- **SQL injection**: parameters for values; identifiers quoted/escaped or validated against a strict pattern -
  including identifiers read from files, backups or other databases
- **command injection**: argument lists, never shell strings built from input; tools resolved by full path when
  running elevated
- **path traversal and unsafe file access**: archive extraction confined to its target, deletes confined to owned
  folders, junctions/symlinks not followed by recursive deletes, config-referenced paths confined
- **secrets**: in a secret store or encrypted at rest; never in logs, messages, command lines, temp files or
  docs; scrubbed from errors
- **insecure defaults**: services bound to all interfaces, default passwords, disabled certificate validation
  for remote hosts, elevated auto-start of user-writable executables
- **privilege**: what runs elevated, and whether anything it starts (browser, editor, tool) inherits that unnecessarily
- writes of important files are atomic (temp file + replace) so a crash can't corrupt them

## Configuration

- separate defaults (in code), user settings, developer settings, environment and production configuration, and
  secrets
- no environment-specific values hardcoded; sensible defaults so little needs configuring
- validate configuration at load with messages that name the key and the fix; reject dangerous values (e.g. a
  drive root as a folder the app deletes from)
- versioned formats with migrations when keys change

## Testing

- business logic separated from UI, database, network, file system and external systems so it can be tested alone
- tests protect meaningful behaviour: rules, parsing, validation, security guards, regressions of fixed bugs
- a regression test must fail without the fix
- slow or environment-dependent tests marked as such and skipped cleanly when the environment is missing
- no tests written only to raise a count

## Scalability

Ask what happens at 100× today: 1 → 100 projects/tenants, 100 → millions of records, 1 → a team of developers,
1 → many concurrent users. Look for per-item work in loops, unbounded lists in memory, full scans, global locks,
and designs that only work because the data is small.
