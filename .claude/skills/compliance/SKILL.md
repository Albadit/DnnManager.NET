---
name: compliance
description: >-
  Regulatory compliance and data protection analysis and fixes: GDPR, CCPA/CPRA, LGPD, HIPAA/HITECH, PCI DSS, SOC 2,
  SOX and the EU AI Act - field-level data inventory, lawful basis, purpose limitation and minimisation, notice, user
  rights (access, export, erasure), vendor governance, audit trails and log hygiene. Use for privacy reviews,
  compliance audits, handling of PII, health or payment data, retention and deletion logic, consent, and implementing
  compliance fixes.
---

# Compliance

Check that code and systems meet regulatory requirements, data protection standards and organisational policies.
Treat compliance as a property of the system design, not paperwork added after launch, and design for privacy by
default. The legal baseline starts with privacy law (GDPR, CCPA/CPRA, LGPD), layers on sector rules (HIPAA/HITECH,
PCI DSS), adds audit and assurance frameworks (SOC 2, SOX), and now includes AI-specific regulation (EU AI Act).
Every finding must cite the specific regulation and article violated and give actionable remediation with a risk
assessment.

## Mode

This skill both analyses and implements. Analyse and report by default. When the request is to build, fix or
change something, or carries `#fix`, also make the changes: fix Critical and High findings directly and list
Medium and Low ones in the report.

## Core Principles

The common operating rule across privacy regimes is remarkably consistent:
- **Collect only what you need** (data minimization)
- **Tell people what you are doing** (transparency)
- **Use data only for disclosed and lawful purposes** (purpose limitation)
- **Protect it appropriately** (security)
- **Be able to prove all of that later** (accountability)

The highest-performing compliant design is usually the simplest one: define the purpose before collection; send only the minimum necessary data into prompts, tools, and retrieval; keep payment data and PHI out of the model path unless clearly justified; make deletion, access, correction, and downstream propagation automatic; and keep vendor use strictly purpose-limited.

## What to cover

- Build and maintain a **field-level data inventory** - what each input is, why it is processed, who receives it, whether it reaches the model, whether it can enter training/eval datasets, retention period, and how rights requests reach it
- Assess **data protection** compliance (GDPR Articles 5/6/25/35, CCPA/CPRA §1798.100, LGPD Articles 6/7)
- Verify **lawful basis** for each processing activity - legitimate interest requires three-part analysis (real interest, necessity, balancing test); consent must not use dark patterns
- Enforce **purpose limitation and data minimization** at every stage - prompt assembly, retrieval, logging, analytics, training datasets
- Assess **user rights implementation** as working technical systems, not just policy text (GDPR Articles 12–22, CCPA rights framework, CNIL AI guidance)
- Analyse **healthcare** compliance (HIPAA Security Rule, HITECH breach notification, BAA requirements)
- Evaluate **payment** security (PCI DSS scope, SAD/CVV restrictions, cardholder data environment isolation)
- Check **audit and assurance** requirements (SOC 2 trust service criteria, SOX internal controls)
- Analyse **AI-specific regulation** (EU AI Act prohibited practices, GPAI obligations, Article 50 transparency duties, high-risk system requirements)
- Assess **vendor and processor governance** - DPAs, SCCs, BAAs, service-provider agreements, subprocessor controls, region pinning
- Evaluate **data classification and routing** - differentiated handling by data class (anonymous, personal, sensitive PI, PHI, payment data)
- Verify **evidence and audit trail** - immutable logs of access, processing, consent, deletion, and model/dataset versioning
- Check **AI literacy and staff training** obligations (AI Act, HIPAA, PCI, SOC controls)
- Assess **dark pattern avoidance** in consent/opt-out UX (CPPA rules, GDPR transparency requirements)

## Three-Plane Architecture for Compliance

The best architecture for both compliance and performance is a three-plane design:

1. **Policy Plane**: Classifies data, selects lawful basis and routing path, chooses approved vendor, decides whether a prompt may include personal/sensitive/health/payment data. This is the gate that enforces purpose limitation and minimization.

2. **Inference Plane**: Receives ONLY the minimized task payload needed for the answer. No raw identifiers, no unnecessary context, no regulated data unless positively justified.

3. **Evidence Plane**: Stores audit logs, dataset versions, model versions, risk assessments, deletion receipts, and consent records asynchronously - compliance evidence does NOT sit in the request's hot path.

## Data Class Routing

Treat data classes differently - not all data deserves the same friction:

| Data Class | Lane | Controls | Model Training |
|-----------|------|----------|----------------|
| **Anonymous / effectively anonymized** | Lowest friction | Standard security | Permitted |
| **Ordinary personal data** | Minimized lane | Rights handling, retention, recipient mapping | Requires documented lawful basis + notice |
| **Sensitive PI** (CCPA) / Special categories (GDPR) | Purpose-controlled lane | Explicit purpose controls, user limitation rights | Requires explicit justification |
| **PHI** (HIPAA) | Highest-control lane | Role-based access, audit logs, transmission security, BAAs | Off by default; requires positive justification under law + contract |
| **Payment data** (PCI DSS) | Isolated lane | Scoped CDE, tokenized flows, no CVV storage post-auth | Prohibited |

## Workflow

Work through these stages. The questions and checks for each are in [reference/analysis-stages.md](reference/analysis-stages.md):
read all of it for a full-scope review, or only the stages that match a focused scope.

1. Data Inventory and Classification
2. Lawful Basis and Purpose Limitation
3. Data Minimization in AI Pipelines
4. Transparency, Notice, and AI-Specific Disclosure
5. User Rights as Working Technical Systems
6. Vendor and Processor Governance
7. PHI Protection (HIPAA/HITECH)
8. Payment Data Protection (PCI DSS)
9. Audit, Assurance, and Evidence (SOC 2 / SOX)
10. Log and Trace Hygiene
11. AI-Specific Compliance (EU AI Act + CNIL Guidance)
12. Compliance Operating Metrics

## Reference

| File | Contents | Read |
|---|---|---|
| [reference/analysis-stages.md](reference/analysis-stages.md) | The analysis stages in full | Before analysing: all of it for a full-scope review, only the matching stages for a focused one |
| [reference/regulations.md](reference/regulations.md) | Core requirements of GDPR, CCPA/CPRA, LGPD, HIPAA/HITECH, PCI DSS, SOC 2/SOX and the EU AI Act timeline | When citing the regulation and article for a finding |
| [reference/checklist.md](reference/checklist.md) | Checklist per area: inventory, lawful basis, minimisation, notice, rights, vendors, PHI, payment data, evidence, AI | To confirm coverage before writing the report and to fill the regulatory coverage map |

## Output Format

For each finding, provide ALL of these fields:

```
### [SEVERITY] Compliance Finding

- **Location**: Subsystem, control point, or file reference
- **Regulation**: Applicable regulation/standard with specific article/section
- **Requirement**: Specific requirement violated (cite regulatory text)
- **Current State**: What the code/system does now (specific, observable)
- **Required State**: What compliance requires (cite guidance where applicable)
- **Remediation**: Specific fix with implementation guidance and architecture impact
- **Risk**: Regulatory exposure, penalty range, operational consequence, and downstream impact
```

After all findings, ALWAYS provide these sections:

1. **Regulatory coverage map** - which regulations apply, which requirements are met, which have gaps, which are untestable without runtime access
2. **Data flow assessment** - how personal/sensitive/PHI/payment data flows through the system; where minimization, redaction, or isolation is missing
3. **Rights implementation status** - which rights are technically exercisable, which are policy-only, which are missing entirely; deletion fan-out coverage
4. **Vendor governance status** - processor register completeness, DPA/BAA/SCC coverage, region controls, subprocessor visibility
5. **Evidence and audit posture** - what compliance evidence exists, what is missing, whether audit trail is immutable and complete
6. **AI-specific compliance** - AI Act timeline readiness, transparency obligations, model-data separation, training data governance
7. **Priority remediation** - ordered by regulatory risk (enforcement likelihood × penalty severity × data volume), CRITICAL findings always first

## Guidelines

- **Data minimization is both a legal requirement and a performance optimization** - less irrelevant context in prompts means better compliance AND better results
- **Privacy by design**: build compliance into the architecture from the start - the three-plane design (policy, inference, evidence) is the target pattern
- **Default to the most restrictive privacy setting** - then relax with documented justification
- **A model is not automatically outside privacy law** just because data has been "learned" - if personal data can be extracted or inferred, GDPR scope may apply
- **Legitimate interest is not a free pass** - always document the three-part analysis
- **User rights must be working technical systems**, not just policy text - a support inbox is not a rights implementation
- **Deletion must fan out** to all stores including vector DBs, caches, logs, and training datasets
- **Vendor compliance is your compliance** - uncontracted vendors receiving personal data make your entire posture structurally unsound
- **PHI and payment data must be isolated** from general application paths, logs, analytics, and model training
- **Never log PII, PHI, cardholder data, or secrets** in general application logs - use structured, redacted logging with allowlists
- **CVV/CVC must never be stored after authorization** - reject patterns in free-text inputs
- **Payment capture must bypass the LLM path entirely** - use tokenized, processor-controlled flows
- **Evidence must be immutable and asynchronous** - compliance proof should not sit in the request hot path
- **Dark patterns invalidate consent** - if consent is obtained through dark patterns, the related processing may be unlawful from the start
- **AI Act obligations are operational, not theoretical** - prohibited practices and GPAI rules are already in force; transparency duties from August 2026
- **AI literacy is a legal obligation** - staff must understand the AI systems they operate
- **Separate model-improvement from production** - dedicated offline datasets with documented lawful basis, versioned annotations, synthetic test data
- **Measure compliance operationally** - track classification coverage, identifier-free prompt rate, deletion completion, rights SLA, vendor coverage, evidence completeness

## Follow-ups

After the report, offer these next steps in one line each; do not start them unprompted:

- Implement the compliance fixes recommended in the report.
- Analyse the security implications of the compliance issues with the `security` skill.
