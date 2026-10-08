# Operations: checklist

## Operational Readiness Checklist

### Observability
- [ ] Structured (JSON) logging with consistent fields (timestamp, service, request_id, level)
- [ ] No secrets, tokens, or PII in logs/metrics/traces
- [ ] RED metrics emitted per service (rate, errors, duration p50/p95/p99)
- [ ] USE metrics for infrastructure resources (utilization, saturation, errors)
- [ ] Business metrics tracked (transactions, conversions, user activity)
- [ ] Distributed tracing with trace ID propagation across all service boundaries
- [ ] Trace sampling strategy defined for high-traffic services
- [ ] Dashboards for RED, USE, and business metrics

### Monitoring & Alerting
- [ ] SLO-based alerting - page on SLO breaches and error budget burn, not infra metrics
- [ ] Every paging alert has a corresponding runbook with troubleshooting steps
- [ ] Multi-window burn-rate alerts for fast and slow burns
- [ ] Synthetic checks on critical user paths
- [ ] Noisy/transient alerts removed or tuned
- [ ] Health endpoints (/healthz, /readyz) on every service
- [ ] Liveness, readiness, and startup probes correctly separated
- [ ] Health indicators monitored centrally

### Deployment
- [ ] Zero-downtime deployment strategy (blue/green, canary, or rolling)
- [ ] Automated rollback on deployment failure
- [ ] Feature flags for gradual rollout and instant kill-switch
- [ ] Pre- and post-deployment health checks
- [ ] Database migrations reversible or forward-safe (expand-contract)
- [ ] Pipeline quality gates: tests, code review, security scans
- [ ] Deployment artifacts signed and verified

### Infrastructure as Code
- [ ] All infrastructure in version control (no manual provisioning)
- [ ] Remote state backend with locking (no local state files)
- [ ] Automated drift detection (terraform plan in CI)
- [ ] Modular, DRY configuration - no copy-paste duplication
- [ ] Environment parity: staging mirrors production configuration
- [ ] Only CI/CD pipelines can alter production resources

### Configuration & Secrets
- [ ] All secrets in vault/secret manager (not in code, env vars, or config files)
- [ ] Secrets referenced dynamically at runtime
- [ ] Secret access audit-logged and rotation scheduled
- [ ] Configuration externalized and parameterized
- [ ] Immutable config bundles (no in-place edits)
- [ ] Config changes versioned and auditable
- [ ] Least-privilege IAM/RBAC enforced per service/human
- [ ] Unused credentials and over-permissive roles audited and removed

### Incident Response
- [ ] Incident severity levels defined with response time targets
- [ ] Escalation paths documented per severity
- [ ] Runbooks for every critical alert and service - tested and up-to-date
- [ ] Communication templates and status page prepared
- [ ] Team roles assigned during incidents (commander, scribe, comms lead)
- [ ] Blameless postmortems conducted after Sev1/Sev2
- [ ] Postmortem action items tracked with owners and deadlines
- [ ] Regular fire-drill exercises

### Backup & DR
- [ ] Automated, frequent backups of all critical data
- [ ] Backups replicated to separate region/AZ
- [ ] Restore tests automated and scheduled - verified against RTO/RPO
- [ ] Restored systems validated with production-like traffic
- [ ] Recovery runbooks documented
- [ ] Failover drills conducted periodically
- [ ] RTO/RPO targets explicitly defined and measured

### Capacity
- [ ] Auto-scaling enabled with concrete thresholds
- [ ] Resource limits AND requests set on all containers
- [ ] Saturation metrics monitored (USE method) with scale-up triggers
- [ ] Headroom tracked and capacity reviewed against real traffic
- [ ] Load tests validate scaling behavior
