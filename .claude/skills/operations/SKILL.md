---
name: operations
description: >-
  Operational readiness analysis and implementation: structured logging, metrics and tracing, SLO-based alerting and
  runbooks, health endpoints, CI/CD and release strategy, rollback, infrastructure as code, configuration and secrets,
  least privilege, incident response, backup, restore and disaster recovery, capacity and scaling. Use for DevOps
  reviews, Docker, CI/CD and deployment setup, monitoring and alerting, and implementing ops changes.
---

# Operations

Make sure systems are observable, deployable and operationally sound - aligned with SLOs, observability first. Treat
operational readiness as measurable: structured telemetry (logs, metrics, traces), SLO-based alerting with runbooks,
immutable infrastructure as code, zero-downtime deployments with automated rollback, tested backup and restore, and
documented incident response. Every finding must cite the specific operational practice violated and give an
actionable fix with a priority.

## Mode

This skill both analyses and implements. Analyse and report by default. When the request is to build, fix or
change something, or carries `#fix`, also make the changes: fix Critical and High findings directly and list
Medium and Low ones in the report.

## What to cover

- Assess **observability** completeness - structured (JSON) logging, RED/USE metrics, distributed tracing with trace ID propagation
- Ensure logs/metrics/traces **never include secrets or PII**
- Analyse **deployment pipelines** - CI/CD automation, immutable deployments (blue/green, canary, rolling), automated rollback, feature flags
- Evaluate **infrastructure as code** quality - version-controlled, modular, remote state with locking, drift detection
- Check **monitoring and alerting** - SLO-based alerting where every alert is actionable and has a runbook; eliminate alert fatigue
- Assess **health endpoints** - liveness, readiness, and startup probes correctly separated and monitored
- Analyse **configuration and secrets management** - secrets in vault (not code/env vars), externalized config, audit logging, immutable config bundles
- Enforce **least-privilege access controls** - IAM/RBAC scoped per service/human, change review for permission changes
- Assess **incident response** readiness - severity levels, escalation paths, communication templates, blameless postmortems
- Evaluate **backup and disaster recovery** - automated backups with tested restores, RTO/RPO targets, multi-region replication, failover drills
- Analyse **capacity planning and auto-scaling** - threshold-based scaling, resource limits/requests, saturation monitoring, headroom tracking

## Workflow

Work through these stages. The questions and checks for each are in [reference/analysis-stages.md](reference/analysis-stages.md):
read all of it for a full-scope review, or only the stages that match a focused scope.

1. Observability and Telemetry
2. Monitoring and Alerting
3. Deployment Pipelines and Release Strategy
4. Infrastructure as Code
5. Configuration and Secrets Management
6. Incident Response and Runbooks
7. Backup, Disaster Recovery, and Data Durability
8. Capacity Planning and Auto-Scaling
9. CI/CD Pipeline Security and Quality
10. Operational Documentation and Knowledge Management

## Reference

| File | Contents | Read |
|---|---|---|
| [reference/analysis-stages.md](reference/analysis-stages.md) | The analysis stages in full | Before analysing: all of it for a full-scope review, only the matching stages for a focused one |
| [reference/checklist.md](reference/checklist.md) | Readiness checklist: observability, monitoring and alerting, deployment, IaC, configuration and secrets, incident response, backup and DR, capacity | To confirm coverage before writing the report |

## Output Format

For each finding, provide ALL of these fields:

```
### [SEVERITY] Operational Issue

- **Location**: File/configuration reference (or system component)
- **Category**: Observability / Deployment / Monitoring / Configuration / Incident Response / DR / Capacity
- **Impact**: Operational risk - what breaks, who is affected, blast radius
- **Current State**: What exists now (specific, observable)
- **Recommendation**: Specific improvement with implementation approach
- **Priority**: Immediate / Next Sprint / Backlog (with reasoning)
```

After all findings, ALWAYS provide these sections:

1. **Observability assessment** - which pillars are present (logging, metrics, tracing), which are missing, data quality issues (unstructured logs, missing trace propagation, PII in logs)
2. **Deployment risk assessment** - rollback capability, deployment strategy, migration safety, feature flag coverage
3. **Alerting quality** - SLO-aligned vs. infra-metric alerts, alert-to-runbook coverage, noise level, synthetic check coverage
4. **Secrets and config hygiene** - secrets in code/env, access control gaps, drift detection, audit trail
5. **Incident readiness** - severity definitions, runbook coverage, communication plans, postmortem process
6. **DR and capacity posture** - backup/restore test status, RTO/RPO gaps, scaling policy, headroom
7. **Priority remediation** - ordered by operational risk (blast radius × probability), highest first; CRITICAL items always at the top

## Guidelines

- **Logs must be structured (JSON)** for machine parsing and correlation - unstructured logs are a CRITICAL finding
- **Never log secrets, tokens, or PII** - violations are CRITICAL findings
- **Every alert must be actionable with a runbook** - alerts without runbooks create noise and on-call burnout
- **Shift alerting from infrastructure metrics to SLO-based symptoms** - CPU alerts miss real user impact
- **Infrastructure must be reproducible from code alone** - manual provisioning is a HIGH finding
- **Use remote state with locking** for all IaC - local state files risk corruption and drift
- **Run drift detection regularly** - drift between declared and actual state indicates unauthorized changes or bugs
- **Prefer immutable deployments over in-place updates** - blue/green or canary with automated rollback
- **Every service needs health check endpoints** - liveness, readiness, and startup probes separated correctly
- **Secrets belong in a vault** - secrets in code, config files, or baked env vars are MEDIUM+ findings
- **Apply least privilege everywhere** - over-permissive roles are a security and operational risk
- **Backup is not reliable unless restore is tested** - untested backups are a CRITICAL finding
- **Define and test RTO/RPO** - recovery targets without drills are aspirational, not operational
- **Capacity planning requires data** - "guess and check" without metrics and load tests is inadequate
- **Incident response must be documented** - ad-hoc processes increase MTTR and reduce learning
- **Conduct blameless postmortems** - track and complete action items; untracked items recur
- **Operational knowledge must be documented** - tribal knowledge is a single point of failure

## Follow-ups

After the report, offer these next steps in one line each; do not start them unprompted:

- Implement the operational improvements recommended in the report.
- Analyse the reliability implications with the `reliability` skill.
- Analyse the infrastructure and deployment configuration for security issues with the `security` skill.
