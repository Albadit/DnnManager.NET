---
name: security
description: >-
  Defensive security analysis and fixes aligned to OWASP Top 10:2025, OWASP API Top 10:2023, ASVS, WSTG, CWE Top 25,
  NIST SSDF and RFC 9700: threat models and attack chains, attack surface, access control (BOLA/IDOR, tenant
  isolation, SSRF), authentication and sessions, injection and XSS, file uploads, CSRF/CORS, webhooks, supply chain
  and secrets, security logging and headers, LLM features. Use for security audits, threat modelling, penetration-test
  analysis, secure-coding reviews, and implementing security fixes.
---

# Security

Analyse defensively and adversarially, and accept false positives: it is better to over-flag high-impact risks than
to miss them. Align with OWASP Top 10:2025, OWASP API Top 10:2023, OWASP ASVS, OWASP WSTG, the OWASP Cheat Sheet
Series, NIST SSDF (SP 800-218), NIST SP 800-63B-4, MITRE CWE Top 25 and the IETF OAuth security best practices (RFC
9700).

## Mode

This skill both analyses and implements. Analyse and report by default. When the request is to build, fix or
change something, or carries `#fix`, also make the changes: fix Critical and High findings directly and list
Medium and Low ones in the report.

## Analysis Philosophy

- **Assume breach mentality**: Look for attack chains, not just individual vulnerabilities
- **Five attacker perspectives**: External attacker, low-priv user, malicious tenant, insider, API/integration abuser
- **Chain analysis**: The most dangerous findings chain together - always evaluate chain potential, not just standalone risk
- **False-positive tolerant**: Better to over-flag high-impact risks than miss them; every item is "Likely" or "Needs verification"
- **Evidence-based**: For every finding, cite code references or explicitly mark "needs verification" - never assume controls exist without proof

## Sensitive Assets to Protect (Always Enumerate)

Before analysis, identify and enumerate all of these asset classes:
- **Authentication material**: passwords, MFA factors, session cookies, refresh tokens, API keys, OAuth client secrets, signing keys
- **Authorization power**: admin actions, role assignment, tenant management, billing, webhooks, data export, user invites
- **Data**: PII, tenant data, files, backups, logs, analytics events, internal IDs (APIs commonly leak too much by default)
- **Execution surfaces**: file processing, template rendering, deserialization, CI/CD runners, plugin systems

## Trust Boundaries to Map

Always identify these boundaries - security misconfiguration happens at boundary crossings:
- Browser/mobile client ↔ edge (CDN/WAF/load balancer)
- Edge ↔ application/API servers
- Application ↔ internal services (DB/cache/queues/workers)
- Application ↔ third parties (IdP, payments, email, webhooks)
- Developer workstation ↔ CI/CD ↔ production

## Workflow

Work through these stages. The questions and checks for each are in [reference/analysis-stages.md](reference/analysis-stages.md):
read all of it for a full-scope review, or only the stages that match a focused scope.

1. Threat Model & Attack Trees
2. Attack Surface Mapping
3. Access Control Audit (OWASP A01 - #1 risk)
4. Authentication & Session Audit (OWASP A07)
5. Injection & Input Handling Audit (OWASP A05)
6. CSRF & CORS Audit
7. Webhook & Integration Security
8. Supply Chain & Secrets Audit (OWASP A03, A04, A08)
9. Security Observability Audit (OWASP A09)
10. Resilience & Configuration Audit (OWASP A02, A10)
11. GraphQL-Specific Audit (if applicable)
12. LLM/AI Feature Audit (if applicable)

## Reference

| File | Contents | Read |
|---|---|---|
| [reference/analysis-stages.md](reference/analysis-stages.md) | The analysis stages in full | Before analysing: all of it for a full-scope review, only the matching stages for a focused one |
| [reference/verification-and-remediation.md](reference/verification-and-remediation.md) | Attack chains to always check, manual verification checklist, missing controls checklist, remediation priority order, secure-by-design recommendations | Every analysis: the attack chains are mandatory, and the report sections after the findings use all of it |

## Output Format

For each finding, provide:

```
### [SEVERITY] Finding Title
- **Category**: OWASP Top 10:2025 category + API Top 10:2023 if applicable
- **CWE**: Relevant CWE number(s)
- **Confidence**: High / Medium / Low
- **Affected Component**: File, endpoint, or system
- **Why Vulnerable**: Root cause explanation
- **Attack Scenario**: Step-by-step exploitation (numbered steps, specific to this codebase)
- **Impact**: What attacker achieves (standalone + chain amplification)
- **Evidence**: Code reference with file:line or "needs verification"
- **Chain Potential**: Which other findings this chains with and what the combined impact is
- **Remediation**: Specific fix with secure implementation pattern
- **Verification**: Exact steps to confirm the fix works
```

After all findings, ALWAYS provide these sections:
1. **Attack chain analysis** - which findings chain together for maximum impact (use the chain table in [reference/verification-and-remediation.md](reference/verification-and-remediation.md) as reference)
2. **Remediation priority** - ordered by risk reduction per engineer-hour (use the priority framework in [reference/verification-and-remediation.md](reference/verification-and-remediation.md))
3. **Missing controls checklist** - what evidence should exist but doesn't (use the missing controls checklist in [reference/verification-and-remediation.md](reference/verification-and-remediation.md))
4. **Secure-by-design recommendations** - applicable architectural improvements

## Guidelines

- Never suggest disabling security controls as a fix
- Always recommend principle of least privilege and deny-by-default
- Prefer allowlists over denylists
- Flag any use of `eval()`, `exec()`, dynamic code execution, `shell=True`, `innerHTML`, `document.write`
- Flag any dynamic query construction (string concatenation into SQL/NoSQL/LDAP/commands/templates)
- Check input validation at ALL trust boundaries (not just the first one)
- Verify error messages don't leak sensitive information (stack traces, paths, DB errors, versions)
- Ensure logs don't capture sensitive data (passwords, tokens, PII, credit cards)
- Always check for both the vulnerability AND its chain potential
- Rate findings considering both standalone impact and chain amplification
- Check every finding from all five attacker perspectives
- Do NOT assume controls exist - verify them in code or mark "needs verification"
- Treat all client input as untrusted (headers, cookies, query params, body, file names, webhook payloads)
- For OAuth: always check redirect URI strictness, state parameter, PKCE, nonce, issuer validation
- For GraphQL: always check introspection, complexity limits, resolver-level authz
- For webhooks: always check signature verification, replay protection, tenant isolation
- For file uploads: always check magic bytes, storage location, processing isolation, access authz
- For AI/LLM features: always check prompt injection, tool permissions, tenant scoping, output handling

## Follow-ups

After the report, offer these next steps in one line each; do not start them unprompted:

- Fix the vulnerabilities identified, applying the recommended remediations.
- Verify the security fixes meet compliance requirements with the `compliance` skill.
