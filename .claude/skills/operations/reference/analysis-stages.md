# Operations: analysis stages

## Contents

- Analysis Process (10 Stages)
  - Stage 1: Observability and Telemetry
  - Stage 2: Monitoring and Alerting
  - Stage 3: Deployment Pipelines and Release Strategy
  - Stage 4: Infrastructure as Code
  - Stage 5: Configuration and Secrets Management
  - Stage 6: Incident Response and Runbooks
  - Stage 7: Backup, Disaster Recovery, and Data Durability
  - Stage 8: Capacity Planning and Auto-Scaling
  - Stage 9: CI/CD Pipeline Security and Quality
  - Stage 10: Operational Documentation and Knowledge Management

## Analysis Process (10 Stages)

### Stage 1: Observability and Telemetry

**1a. Structured Logging:**
- Logs must be in machine-readable format (JSON) so they can be parsed and correlated - unstructured plaintext logs are hard to analyze and slow troubleshooting
- Consistent fields on every log line: `timestamp`, `service`, `request_id`, `level`, `message`, plus context-specific fields
- Log levels used correctly:

| Level | Usage |
|-------|-------|
| **ERROR** | Unrecoverable failures requiring immediate attention |
| **WARN** | Recoverable issues, degraded behavior, approaching thresholds |
| **INFO** | Significant business events, state changes, request lifecycle |
| **DEBUG** | Detailed troubleshooting (disabled in production) |

- **Never log secrets, tokens, passwords, or PII** - mask or omit sensitive data in all log output
- Without structured logging: troubleshooting is guesswork, SLO compliance is unmeasurable, correlation across services is impossible

**1b. Metrics (RED + USE + Business):**
- **RED Method** (request-scoped, for microservices): Rate (requests/sec), Errors (error rate), Duration (latency percentiles p50/p95/p99)
- **USE Method** (resource-scoped, for infrastructure): Utilization, Saturation, Errors - per CPU, memory, disk, network, connection pools
- **Business metrics**: conversion rate, transactions per minute, user signups, revenue - the metrics that connect system health to business outcomes
- Without RED/USE metrics: undetected service degradation, missed capacity limits, cost and reliability risks

**1c. Distributed Tracing:**
- Propagate trace IDs across all service boundaries (HTTP headers, gRPC metadata, message headers) so end-to-end requests can be followed
- Instrument with OpenTelemetry (or equivalent) - emit spans for key operations (DB queries, external calls, model invocations, queue operations)
- Span annotations for latency-critical operations
- Define a trace sampling strategy for high-traffic systems (head-based or tail-based sampling)
- Without tracing: cannot diagnose cross-service latency, cannot identify which service/stage is the bottleneck

### Stage 2: Monitoring and Alerting

**2a. SLO-Based Alerting:**
- Alerts must be **actionable and aligned with SLOs** - page only on high-severity indicators (service unavailable, SLO breach, error budget burn)
- Google SRE principle: "All alerts should be immediately actionable" - if an alert fires but no one needs to do anything right now, it should not page
- Every paging alert must have a corresponding **runbook/playbook entry** explaining what on-call should do
- Shift from infrastructure-metric alerts ("CPU > 80%") to symptom-based alerts (request latency p95 > threshold, error rate > SLO budget burn rate)
- Use multi-window burn-rate alerts to detect both fast catastrophes and slow burns
- Without SLO-based alerting: high noise, alert fatigue, on-call burnout, missed real incidents

**2b. Alert Hygiene:**
- Remove or adjust noisy alerts that fire on transient conditions (e.g., 500ms spike on one instance)
- Every alert must have: clear description, severity, affected service, runbook link, escalation path
- Implement synthetic checks on critical user paths (e.g., homepage load, login flow, API health)
- Test runbooks periodically - a runbook that hasn't been used is a runbook that might not work

**2c. Health Endpoints:**
- Every service must have health check endpoints (`/healthz`, `/readyz`)
- **Liveness**: detects deadlocks / hung processes - triggers restart
- **Readiness**: indicates whether the service can accept traffic - controls routing
- **Startup**: delays liveness checks for slow-starting services
- Monitor health indicators centrally (e.g., Prometheus alert if too many pods unready)
- Align alerts with meaningful symptoms (request latency, error rate) rather than low-level metrics alone
- Without health endpoints: undetected service failures, false alarms during routine events, Kubernetes kills pods unpredictably

### Stage 3: Deployment Pipelines and Release Strategy

**3a. Zero-Downtime Deployments:**
- Adopt immutable deployment strategies so traffic can be switched instantly:
  - **Blue/green**: run two identical environments, switch traffic after validation
  - **Rolling**: replace instances incrementally with health checks between batches
  - **Canary**: route a small percentage of traffic to the new version, validate, then expand
- Without zero-downtime deployments: service goes offline during releases, risk of prolonged outages on bad deploys

**3b. Feature Flags:**
- Use feature flags to test changes on a subset of users before full rollout
- Decouple deployment from release - deploy code anytime, enable features when ready
- Feature flags enable instant kill-switch for problematic features without redeployment

**3c. Automated Rollback:**
- On failure, pipeline must automatically revert to the last known good version
- Rollback should be one-click (or automatic) - not a manual process requiring deep knowledge
- Include pre-deployment and post-deployment health checks (hit `/healthz` after deploy, verify error rate)
- Database migration playbooks: migrations must be reversible or forward-safe (expand-contract pattern)
- Without automated rollback: slow recovery from failures, prolonged outages, high MTTR

**3d. Pipeline Quality Gates:**
- Automate testing in CI (unit, integration, smoke tests) - block deployment on failure
- Enforce code reviews as a merge requirement
- Run security scans (SAST, dependency audit) in pipeline
- Validate infrastructure changes (`terraform plan`) before apply

### Stage 4: Infrastructure as Code

**4a. Version-Controlled Infrastructure:**
- Store ALL infrastructure configuration (server specs, networking, IAM, monitoring rules) in version control
- No manual provisioning or console-clicking for production resources
- Use modules and DRY practices to reduce misconfiguration
- Without IaC: deployment drift, environment inconsistency, inability to reproduce environments

**4b. State Management:**
- Use a remote state backend with locking (e.g., S3 + DynamoDB for Terraform) to avoid concurrent edits and state corruption
- State files must never be committed to source control or stored locally
- Enable state encryption at rest

**4c. Drift Detection:**
- Run automated `terraform plan` (or equivalent) regularly in CI to detect drift between declared and actual state
- Drift indicates either unauthorized manual changes or configuration bugs - both must be resolved
- Use immutable patterns: replace rather than modify in-place where possible

**4d. Environment Parity:**
- Staging must mirror production configuration (same IaC modules, same scaling parameters at reduced size)
- Environment-specific values isolated to variable files - not scattered across code
- Test infrastructure changes in staging before promoting to production

### Stage 5: Configuration and Secrets Management

**5a. Secrets:**
- **All secrets (DB passwords, API keys, tokens) must be in a secure secrets manager/vault** (AWS Secrets Manager, HashiCorp Vault, GCP Secret Manager, Azure Key Vault)
- Secrets must NOT appear in code, environment variables baked into images, config files, or version control
- Reference secrets dynamically at runtime - never embed in build artifacts
- Enable audit logging for secret access
- Rotate secrets on a schedule and on compromise

**5b. Configuration:**
- Configuration is parameterized and externalized from code/artifacts
- Environment-specific settings managed separately (config files, parameter store, ConfigMaps)
- Use immutable config bundles - avoid in-place edits to running configuration
- Version and audit all configuration changes
- Without externalized config: difficulty replicating environments, risk of hard-coded production values, no change trail

**5c. Access Controls (IAM/RBAC):**
- Apply **principle of least privilege**: restrict each service account and human to only the permissions needed
- Use IAM roles or Kubernetes RBAC to isolate responsibilities
- Implement change review (pull requests) for IAM and RBAC changes
- Only CI/CD pipelines should be able to alter production resources - not individual engineers
- Periodically audit and remove unused credentials and over-permissive roles
- Without least privilege: unauthorized changes, misconfigurations leading to outages or breaches

### Stage 6: Incident Response and Runbooks

**6a. Incident Severity and Procedures:**
- Define incident severity levels (e.g., Sev1: service down/data loss, Sev2: degraded service, Sev3: minor impact) with corresponding response procedures
- Each severity level has: response time target, escalation path, communication requirements
- Assign team roles during incidents: incident commander, scribe, communications lead
- Without defined severity: slow or inconsistent response, longer MTTR, confusion during outages

**6b. Runbooks:**
- For every critical alert and service, create a **runbook with troubleshooting steps and escalation contacts**
- Runbooks must be tested and updated - review after every incident
- Include: symptom, likely causes, diagnostic commands, remediation steps, escalation criteria
- Perform regular fire-drill exercises to validate runbook accuracy
- Without runbooks: engineers handle incidents ad-hoc, knowledge is tribal, response is inconsistent

**6c. Communication Plans:**
- Establish a standard incident communication workflow: who gets notified (customers, management, dependent teams) and how (email, status page, Slack)
- Prepare templates for: acknowledgment, progress updates, resolution notices
- Maintain a status page for external stakeholders
- Without communication plans: stakeholder confusion, duplicated work, loss of trust during incidents

**6d. Blameless Postmortems:**
- Conduct postmortems after every Sev1/Sev2 incident
- Focus on systemic causes, not individual blame
- Track action items with owners and deadlines
- Review action item completion in subsequent sprints
- Share postmortem learnings across teams

### Stage 7: Backup, Disaster Recovery, and Data Durability

**7a. Automated Backups:**
- Implement automated, frequent backups of all critical data (databases, state files, vector indexes, object storage)
- Replicate backups to a separate region/availability zone
- Having backups is insufficient - you must regularly test restores to verify backup integrity
- Without tested backups: discover backup failure during a real disaster → permanent data loss or multi-day outage

**7b. Restore Testing:**
- Schedule and **automate restore tests** - verify that backups are valid and that recovery meets RTO/RPO targets
- Validate that restored systems can serve production-like traffic (not just "the file exists")
- Document restore procedures in runbooks
- Without restore testing: backups exist but restores fail when needed most

**7c. RTO/RPO Targets:**
- Define explicit Recovery Time Objective (how long until service is restored) and Recovery Point Objective (how much data loss is acceptable)
- Measure actual recovery time during drills - compare against targets
- Design backup frequency and replication strategy to meet RPO

**7d. Failover Drills:**
- Test failover scenarios periodically (simulate region outage, database failure, primary service loss)
- Verify that failover is automated or well-documented with tested runbooks
- Single-region deployment without failover capability is a CRITICAL finding for production services

### Stage 8: Capacity Planning and Auto-Scaling

**8a. Auto-Scaling Policies:**
- Enable auto-scaling based on concrete thresholds (e.g., add nodes when CPU > 70%, scale down when < 30%)
- Configure horizontal pod autoscaling (HPA) or instance-level scaling policies
- Scale on application-level metrics (request rate, queue depth) not just CPU/memory where possible
- Without auto-scaling: outages from insufficient capacity during traffic spikes, or runaway costs from over-provisioning

**8b. Resource Limits and Requests:**
- Set sensible resource limits AND requests on all containers - prevents contention and ensures scheduler correctness
- Without limits: one service can starve others; without requests: scheduler cannot make informed placement decisions

**8c. Saturation Monitoring and Headroom:**
- Monitor saturation metrics (USE method) to trigger scale-ups before exhaustion
- Track headroom: how much spare capacity exists at current traffic levels
- Regularly review capacity planning assumptions against real traffic patterns
- Capacity planning is not "guess and check" - it requires data from metrics and load tests

### Stage 9: CI/CD Pipeline Security and Quality

- Run automated tests (unit, integration, smoke) as pipeline gates - block deployment on failure
- Run security scans (SAST, dependency audit, container image scanning) in pipeline
- Enforce code review as merge requirement
- Sign and verify deployment artifacts (container images, binaries)
- Pipeline credentials scoped to minimum required permissions
- Audit pipeline execution logs
- Separate build, test, and deploy stages with clear progression

### Stage 10: Operational Documentation and Knowledge Management

- Maintain a service catalog: for each service, document owner, dependencies, runbooks, SLOs, deployment procedure
- Architecture diagrams kept current (updated when services are added/changed)
- On-call handoff documentation: what's in-flight, known issues, recent changes
- Operational knowledge should not be tribal - document it or it doesn't exist
- Review and update operational docs quarterly at minimum
