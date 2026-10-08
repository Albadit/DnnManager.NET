# Compliance: checklist

## Compliance Checklist

### Data Inventory and Classification
- [ ] Field-level data inventory with purpose, lawful basis, recipients, retention, rights reach
- [ ] Data classified by sensitivity (anonymous, personal, sensitive PI, PHI, payment)
- [ ] Indirect collection sources identified (scraping, APIs, reused datasets, brokered sources)
- [ ] Policy gate on prompt assembly - only whitelisted fields enter prompts/retrieval

### Lawful Basis and Purpose
- [ ] Each processing activity has documented lawful basis
- [ ] Legitimate interest analysis documented (three-part test) where relied upon
- [ ] Secondary uses assessed for compatibility with original purpose
- [ ] DPIA/risk assessment completed for high-risk processing before launch

### Data Minimization
- [ ] Identifiers tokenized or redacted before model calls
- [ ] Only task-relevant fields in prompts and retrieval context
- [ ] Real-time inference separated from offline improvement datasets
- [ ] Datasets and annotations versioned

### Transparency and Notice
- [ ] Notice-at-collection provided before or at direct collection
- [ ] Indirect collection notice within required window
- [ ] AI interaction disclosure where required (AI Act Article 50)
- [ ] No dark patterns in consent/opt-out UX
- [ ] Human review path for legally significant automated decisions

### User Rights
- [ ] Identity-linked rights ledger tracking all requests and outcomes
- [ ] Deletion orchestration fans out to all stores (DB, warehouse, vector DB, logs, caches, training data)
- [ ] Correction propagation to downstream recipients/processors
- [ ] Restriction capability (pause processing without deletion)
- [ ] Export in machine-readable format (JSON, CSV)
- [ ] Training-use objection flow
- [ ] Rights response within statutory timelines (GDPR: 1 month, CCPA: 45 days)

### Vendor Governance
- [ ] Processor register: function, region, subprocessor list, contract status, retention, deletion terms
- [ ] DPA/service-provider agreement for every vendor receiving personal data
- [ ] SCCs or valid transfer mechanism for EU outbound processing
- [ ] BAA for every vendor handling PHI
- [ ] Procurement gate blocking production use until sign-off complete
- [ ] Regulated data pinned to approved regions
- [ ] No silent downstream training use without documented basis + notice

### PHI Protection
- [ ] PHI isolated to approved services with role-based access
- [ ] Audit logs for all PHI access
- [ ] Transmission security (encryption in transit)
- [ ] Integrity controls for PHI
- [ ] BAAs with all business associates
- [ ] No PHI in general logs, analytics, or model training without justification
- [ ] Breach notification procedures documented and tested

### Payment Data
- [ ] CVV/CVC never stored after authorization
- [ ] Payment capture bypasses LLM path - tokenized processor-controlled flows
- [ ] PAN/CVV patterns rejected in chat/support/free-text inputs
- [ ] CDE scoped tightly with access monitoring
- [ ] Cardholder data encrypted at rest and in transit

### Audit and Evidence
- [ ] Immutable audit trail for regulated access events
- [ ] Change control with approval workflows
- [ ] Access control with periodic review
- [ ] Prompt, dataset, and model version evidence preserved
- [ ] Deletion receipts stored
- [ ] Consent records stored immutably
- [ ] Structured, redacted logging by default (allowlist, not catch-all)

### AI-Specific
- [ ] No prohibited AI use cases
- [ ] GPAI obligations met (transparency, documentation, copyright)
- [ ] AI interaction disclosure implemented
- [ ] AI literacy training for staff operating/overseeing AI systems
- [ ] Model-improvement loop separated from production
- [ ] Synthetic/dummy data used for testing where possible
- [ ] Training data sources assessed for reliability and integrity
