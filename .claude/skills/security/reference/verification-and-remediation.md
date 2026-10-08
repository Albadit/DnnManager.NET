# Security: verification and remediation

## Critical Attack Chains to ALWAYS Check

Every analysis MUST evaluate these chains - individual findings are less dangerous than chains:

| Chain | Result |
|-------|--------|
| BOLA/IDOR + weak tenant isolation + excessive data exposure | Tenant data breach at scale |
| Stored XSS + weak session controls + missing CSRF | Account takeover + privileged action execution |
| SSRF + cloud metadata/IAM + over-permissive credentials | Environment takeover |
| Unrestricted file upload + unsafe parsing/deserialization | RCE or data exfiltration from workers |
| CI/CD integrity weakness + secrets leakage | Supply-chain compromise and persistent backdoor |
| Weak password reset + no step-up MFA + no session rotation | Full account takeover |
| Mass assignment + missing property-level authz | Privilege escalation via hidden fields |
| OAuth redirect abuse + missing state validation | Token theft + session swapping |
| Log injection + missing log integrity + no alerting | Attacker persistence + evasion |
| Webhook spoofing + no signature verification + SSRF | Fraud + internal pivot |
| Insecure deserialization + file upload + worker execution | RCE from processing pipeline |
| XXE + SSRF + internal service access | File read + credential theft |
| CSRF + OAuth login CSRF + session fixation | Forced account binding + impersonation |
| Race conditions + idempotency flaws + no replay protection | Payment/billing fraud |
| Dependency confusion + CI secret leak + artifact tampering | Persistent supply-chain backdoor |

## Mandatory Verification Checklist (Manual Testing)

After analysis, run these manual checks - they find the biggest issues fastest even in mature systems:

1. **BOLA sweep**: for every endpoint taking an object ID, try cross-user and cross-tenant IDs; verify strict 403/404 and no metadata leakage
2. **Privilege probing**: enumerate admin routes; invoke directly as low-priv user; verify deny-by-default
3. **Auth hardening**: test brute-force controls/rate limits on login/reset; verify enumeration resistance; verify step-up auth for sensitive actions
4. **Session lifecycle**: test session rotation on login; test absolute timeout; test refresh token revocation on logout
5. **XSS surfaces**: create benign script markers in every rendered field; validate output encoding; confirm CSP blocks inline execution
6. **CSRF/CORS**: attempt cross-origin state-changing requests; validate CSRF token enforcement and strict CORS
7. **Upload pipeline**: attempt content-type spoofing, oversized files, zip-slip paths, archive bombs, cross-user file access by ID
8. **SSRF hunt**: search for any URL inputs; attempt internal/metadata destinations (169.254.169.254); test redirect-following
9. **GraphQL-specific**: check introspection, complexity/depth limits, batching behavior, authz in resolvers
10. **OAuth**: validate strict redirect allowlists and state/PKCE; attempt login CSRF confusion flows
11. **Webhook integrity**: send unsigned, wrong-signed, replayed, and cross-tenant events; verify rejection
12. **Logging/alerting**: perform known-bad actions (failed logins, forbidden access, admin attempts); confirm logs and alerts fire
13. **Secrets scan**: scan repos, artifacts, client bundles, and logs for secret patterns; confirm no hits
14. **Fail-closed testing**: induce dependency failures in staging; confirm requests denied safely; confirm generic errors
15. **Password storage**: inspect stored hashes; confirm modern algorithms and parameters
16. **Header audit**: capture response headers for key pages; confirm CSP, HSTS, X-Frame-Options, X-Content-Type-Options present and correct

## Missing Controls Checklist

After findings, enumerate which of these evidence items exist vs are missing - this reduces unknowns:

- **AuthN/AuthZ**: documented auth flows, MFA/step-up policy, session/token TTLs, revocation rules, centralized authorization strategy
- **API**: OpenAPI/GraphQL schema, endpoint inventory (including deprecated/shadow), authorization tests for BOLA and property-level protections
- **Input handling**: proof of parameterization/safe APIs, upload validation, safe parsing evidence
- **SSRF**: inventory of URL-fetching features, allowlist/egress controls, redirect behavior tests
- **Supply chain**: dependency pinning policy, SBOM/provenance, artifact signing, protected release process, CI secret boundaries
- **Secrets**: secrets manager usage, rotation policy, scanning evidence, log redaction validation
- **Logging/monitoring**: auditable event spec, log schema/redaction, retention, alert rules, incident response runbooks
- **Secure config**: security headers baseline, TLS/HSTS policy, CORS policy, environment parity/hardening evidence
- **Webhook security**: signature verification, replay protection, schema validation, tenant binding
- **LLM/AI**: tool permission model, tenant scoping, output handling, prompt injection testing

## Remediation Priority Order (risk reduction per engineer-hour)

When reporting fixes, order them by this priority framework:

1. **Access control (BOLA + function-level)**: centralized authorization, automated tests per endpoint, block cross-tenant IDs
2. **Auth hardening against ATO**: step-up MFA, secure reset flows, rate limiting, session rotation
3. **Kill SSRF + isolate fetchers**: inventory URL-fetching features, SSRF controls, network egress, sandbox workers
4. **File upload + parsing safety**: strict type validation, private storage, scanning, sandboxed converters, per-file authz
5. **Stop mass assignment + excessive exposure**: explicit DTOs, response minimization, property-level authz
6. **Supply chain + CI/CD integrity**: signed artifacts, dependency pinning, secret isolation, protected releases
7. **Secrets management**: vault, rotation, scanning, log redaction
8. **Security observability**: structured audit logging, alerting, log integrity
9. **Platform hardening + resilience**: security headers, fail-closed handling, safe defaults
10. **Integration security**: webhook signing, OAuth hardening, CSRF/CORS

## Secure-by-Design Recommendations

Always include these architectural recommendations where applicable:

1. **Centralize authorization**: one policy engine/middleware; every data access tenant-scoped at DB/query layer; deny by default
2. **Design for compromise-containment**: isolate URL fetchers, file processors, converters into separate least-privilege workers with tight egress
3. **Make tokens boring and revocable**: short-lived access, rotating refresh, server-side revocation, strict cookie settings, absolute timeouts
4. **Safe serialization boundary**: explicit DTOs, per-field allowlists, no mass assignment, no polymorphic deserialization of untrusted data
5. **Harden the delivery pipeline**: signed artifacts, provenance verification, pinned dependencies, restricted CI secrets, vulnerability response process
6. **Security observability as a product feature**: structured audit logs for auth/admin/billing, alert rules for anomaly patterns, integrity protections for logs
7. **Browser hardening baseline**: CSP with nonces, HSTS, X-Frame-Options, X-Content-Type-Options on every response
8. **LLM/agent safety**: strict tool permissioning, treat model output as untrusted, tenant-scoped retrieval, output encoding
