# Compliance: analysis stages

## Contents

- Analysis Process (12 Stages)
  - Stage 1: Data Inventory and Classification
  - Stage 2: Lawful Basis and Purpose Limitation
  - Stage 3: Data Minimization in AI Pipelines
  - Stage 4: Transparency, Notice, and AI-Specific Disclosure
  - Stage 5: User Rights as Working Technical Systems
  - Stage 6: Vendor and Processor Governance
  - Stage 7: PHI Protection (HIPAA/HITECH)
  - Stage 8: Payment Data Protection (PCI DSS)
  - Stage 9: Audit, Assurance, and Evidence (SOC 2 / SOX)
  - Stage 10: Log and Trace Hygiene
  - Stage 11: AI-Specific Compliance (EU AI Act + CNIL Guidance)
  - Stage 12: Compliance Operating Metrics

## Analysis Process (12 Stages)

### Stage 1: Data Inventory and Classification
- Identify ALL personal/sensitive data processed - prompts, uploads, session metadata, analytics identifiers, retrieved records, model outputs
- For each data element, document: what it is, why it is processed, lawful basis, who receives it, whether it reaches the model, whether it can enter training/evaluation datasets, retention period, how rights requests reach it
- Classify data: anonymous, personal, sensitive PI, PHI, payment data
- Publicly accessible data is NOT exempt - indirect collection (scraping, APIs, reused datasets, brokered sources) still triggers transparency and rights analysis
- **Without a field-level inventory**: you cannot prove necessity or purpose limitation for each field

### Stage 2: Lawful Basis and Purpose Limitation
- Verify each processing activity has an identified lawful basis (GDPR Article 6, LGPD Articles 6/7)
- **Legitimate interest is not a free pass** for AI development or deployment - requires classic three-part analysis:
  1. Real and lawful interest
  2. Necessity (could you achieve the purpose with less data or less invasive means?)
  3. Balancing test against data subject rights and reasonable expectations
- Purpose limitation: data collected for one purpose must not be silently repurposed (e.g., user prompts → training data without notice/basis)
- Check compatibility of any secondary use against original purpose
- Verify that higher-risk AI uses have structured **pre-launch assessment** (DPIA under GDPR Article 35, CPPA risk assessment for automated technologies)

### Stage 3: Data Minimization in AI Pipelines
- Every personal-data element sent into prompts, tools, retrieval, logging, monitoring, or training must have a specific justified purpose
- Put a **policy gate in front of prompt assembly**:
  - Whitelist only task-relevant fields into prompts and retrieval
  - Tokenize or redact identifiers before model calls
  - Split real-time inference from offline improvement datasets
  - Version datasets and annotations
- Raw prompts, attachments, session metadata, and retrieved records forwarded into a single shared model-and-logging path is a CRITICAL finding
- Minimization also improves performance - the system processes less irrelevant context
- NIST guidance emphasizes using the minimum level of detail needed to achieve accurate AI outputs
- CNIL guidance emphasizes preserving dataset quality through disciplined annotation and versioning

### Stage 4: Transparency, Notice, and AI-Specific Disclosure
- Individuals must be informed in a way they can understand - not just a static privacy policy
- **Notice-at-collection**: before or at the time of direct collection; within the required window for indirect collection
- For AI systems:
  - EU AI Act Article 50 transparency duties apply from August 2026 - if an AI agent interacts with natural persons or generates content, transparency rules apply
  - AI literacy duties and prohibited-practice rules are already in force
  - GPAI obligations have applied since August 2025
  - High-risk systems face additional safety and trustworthiness requirements
- CPPA rules prohibit dark patterns in consent and choice mechanisms
- CNIL says notice obligations still apply when data is collected indirectly (web scraping, APIs, reused datasets, brokered sources)

### Stage 5: User Rights as Working Technical Systems

Rights cannot live only in a privacy policy. They must be technically exercisable:

**GDPR Rights (Articles 15–22, generally within one month):**

| Right | Technical Implementation |
|-------|------------------------|
| **Access** (Art. 15) | Export all user data in machine-readable format; includes data in training datasets if model is not anonymous |
| **Rectification** (Art. 16) | Accept correction requests; propagate corrections to downstream recipients |
| **Erasure** (Art. 17) | Delete from primary DB, warehouse, object storage, vector DB, prompt logs, trace store, improvement datasets; propagate to all processors |
| **Restriction** (Art. 18) | Pause processing without deletion; hold data in restricted state |
| **Portability** (Art. 20) | Export in standard format (JSON, CSV) |
| **Objection** (Art. 21) | Opt-out of specific processing; includes training-use objection |
| **Automated Decision** (Art. 22) | For significant fully automated decisions: notice, explanation, challenge rights, human intervention safeguards |

**CCPA/CPRA Rights (generally within 45 days):**
- Right to know, delete, correct, opt out of sale/sharing, limit use of sensitive PI
- Pre-use notices and opt-out for automated decision-making technology (ADMT) where applicable

**CNIL AI Guidance:**
- Rights must extend to both the training dataset AND the model itself when the model is not anonymous
- If personal data can be extracted, regurgitated, or inferred using means reasonably likely to be used, the model may still fall within GDPR scope

**Required Implementation:**
- One identity-linked **rights ledger** tracking all requests, outcomes, and propagation
- One **deletion-orchestration service** that fans out to: primary DB, data warehouse, object storage, vector DB, prompt logs, trace store, caches, and improvement datasets
- Propagation to all downstream recipients/processors
- Human review path for legally significant automated decisions
- No dark patterns in consent/opt-out UX

### Stage 6: Vendor and Processor Governance
- Vendors receiving personal data must be under written, purpose-limited terms (DPA/service-provider agreement)
- Required contract provisions: defined use, disclosure limits, safeguards, subprocessor controls, incident reporting, rights support, return or destruction of data
- **EU outbound transfers**: require appropriate transfer mechanism (SCCs where required)
- **PHI vendors**: BAA is mandatory - must restrict uses, require safeguards, incident reporting, rights support, return/destruction, subprocessor flow-down
- **CCPA**: service-provider or contractor agreement with purpose limitations
- Each vendor must appear in a **processor register** with: function, region, subprocessor list, contract status, retention terms, deletion/return terms, and whether customer data can be used for model improvement
- **Procurement gate**: block production use until legal, security, and privacy sign-off are complete
- Pin EU and other regulated data to approved regions
- Prohibit silent downstream training uses unless documented lawful basis + notice
- Many AI stacks integrate LLM/analytics/telemetry vendors as ordinary APIs - if they receive prompts/attachments/identifiers without proper agreements, the compliance posture is structurally unsound

### Stage 7: PHI Protection (HIPAA/HITECH)
- HIPAA Security Rule requires administrative, physical, and technical safeguards for ePHI:
  - **Access controls**: role-based, minimum necessary
  - **Audit controls**: log all access to PHI
  - **Integrity controls**: protect PHI from improper alteration/destruction
  - **Authentication**: verify identity before granting access
  - **Transmission security**: encrypt ePHI in transit
- **BAA requirements**: restrict uses, require safeguards, incident reporting, rights support, return/destruction, subprocessor flow-down
- **HITECH breach notification**: applies to breaches of unsecured PHI - notification to individuals, HHS, and (for large breaches) media
- PHI handling must be **isolated to approved services** - do not let PHI leak into general logs, analytics, support chats, or model training
- HHS has warned that trackers and analytics on health-related flows can result in impermissible disclosures of PHI
- Support chats or authenticated health pages sending PHI to analytics tools is a CRITICAL finding

### Stage 8: Payment Data Protection (PCI DSS)
- PCI DSS applies to systems that store, process, or transmit cardholder data AND to systems that could affect the security of the cardholder data environment (CDE)
- **CVV/CVC must NOT be stored after authorization** - ever
- Cardholder data must be encrypted at rest and in transit
- Access to cardholder data restricted on need-to-know basis with monitoring
- **Payment capture must bypass the LLM path entirely** - use tokenized, processor-controlled flows (hosted payment fields, iframes)
- Reject PAN/CVV patterns in chat, support tools, and free-text input fields
- Scope the CDE tightly - the more systems that touch card data, the larger the compliance surface
- Strong cryptography for transmission over open networks

### Stage 9: Audit, Assurance, and Evidence (SOC 2 / SOX)
- SOC 2 evaluates controls relevant to: security, availability, processing integrity, confidentiality, and privacy
- SOX applies when systems affect internal control over financial reporting
- Required evidence:
  - Preserved, immutable audit trail for regulated access events
  - Change control with approval workflows
  - Access control with periodic review
  - Defensible operating procedures
  - Prompt, dataset, and model version evidence
- **Evidence plane** (asynchronous): audit logs, dataset versions, model versions, risk assessments, deletion receipts, consent records - stored immutably, not in the request hot path
- Structured, redacted logging by default - allowlist approach rather than catch-all payload dump

### Stage 10: Log and Trace Hygiene
- If raw prompts, outputs, headers, screenshots, or uploaded documents are copied into logs and traces, regulated data often leaks into systems never designed to hold it
- **Logs must be structured and redacted by default** - allowlist of fields, not catch-all
- Add **client-side masking and server-side redaction** before any log or trace write
- Block consumer-grade analytics from PHI surfaces unless HIPAA-permitted
- Sensitive data in logs pollutes evaluation and monitoring datasets with unnecessary regulated content
- Separate **immutable audit trail** for regulated access events (distinct from general application logs)
- Never log PII, PHI, cardholder data, or secrets in general application logs

### Stage 11: AI-Specific Compliance (EU AI Act + CNIL Guidance)
- **Prohibited practices**: already in force - verify no prohibited AI use cases (social scoring, real-time biometric in public spaces without authorization, etc.)
- **GPAI obligations**: applied since August 2025 - transparency, technical documentation, copyright compliance for general-purpose AI models
- **Article 50 transparency**: applies from August 2026 - AI interacting with natural persons or generating content must disclose AI involvement
- **High-risk systems**: additional safety, trustworthiness, conformity assessment, and human oversight requirements
- **AI literacy**: mandatory obligation - staff must understand AI systems they operate or oversee
- A model is NOT automatically outside privacy law just because personal data has been "learned" - if personal data can be extracted or inferred, GDPR scope may apply
- **Model-improvement loop must be separated from production**:
  - Dedicated offline improvement dataset with documented legal basis
  - Training and production datasets kept distinct
  - Version annotations and datasets
  - Use synthetic or dummy data for integration/security tests where possible
  - Continuously measure reliability and integrity of training sources

### Stage 12: Compliance Operating Metrics
Measure quality and compliance together - the most useful operating metrics are:
- Share of model calls that pass through input classification
- Share of prompts reaching the model without raw identifiers
- Deletion completion rate across all stores and vendors
- Rights-request SLA performance (within statutory timelines)
- Vendor coverage under DPA/BAA/SCC terms
- Immutable evidence coverage (prompt, dataset, and model version)
- Answer quality and latency by data class
- Consent validity rate (no dark patterns, proper notice)
