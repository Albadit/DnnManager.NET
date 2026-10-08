# Security: analysis stages

## Contents

- Analysis Process (12 Stages)
  - Stage 1: Threat Model & Attack Trees
  - Stage 2: Attack Surface Mapping
  - Stage 3: Access Control Audit (OWASP A01 - #1 risk)
  - Stage 4: Authentication & Session Audit (OWASP A07)
  - Stage 5: Injection & Input Handling Audit (OWASP A05)
  - Stage 6: CSRF & CORS Audit
  - Stage 7: Webhook & Integration Security
  - Stage 8: Supply Chain & Secrets Audit (OWASP A03, A04, A08)
  - Stage 9: Security Observability Audit (OWASP A09)
  - Stage 10: Resilience & Configuration Audit (OWASP A02, A10)
  - Stage 11: GraphQL-Specific Audit (if applicable)
  - Stage 12: LLM/AI Feature Audit (if applicable)

## Analysis Process (12 Stages)

### Stage 1: Threat Model & Attack Trees
- Identify all sensitive assets (see Sensitive Assets to Protect in [SKILL.md](../SKILL.md))
- Map all trust boundaries (see Trust Boundaries to Map in [SKILL.md](../SKILL.md))
- Build goal-oriented attack trees for ALL of the following goals:

**Goal: Account Takeover (ATO)**
- Credential stuffing / brute force (no rate limits, weak lockout, weak anomaly detection)
- Weak password policy allowing easy-to-guess passwords
- Login error messages allowing user enumeration
- Password reset abuse: token predictability, no expiry, reusable tokens
- Host header poisoning to forge password reset links
- Email verification bypass or "change email then reset" flow abuse
- XSS → steal session/refresh tokens
- Insecure cookie storage (missing Secure/HttpOnly/SameSite)
- JWT validation flaws (algorithm confusion, missing issuer/audience/expiry checks)
- OAuth/OIDC misconfig (redirect manipulation, missing state/PKCE, login CSRF)

**Goal: Tenant Data Breach / Tenant Breakout**
- BOLA/IDOR on object IDs (client-supplied IDs trusted without ownership check)
- Property-level authorization broken (excessive data exposure / mass assignment)
- Cache keying mistakes (shared caches leaking cross-tenant data)
- `tenant_id` passed by client and trusted server-side instead of derived from session
- Shared database without row-level security enforcement

**Goal: Remote Code Execution (RCE) / Infrastructure Takeover**
- File upload → polyglot + unsafe processing → code execution in worker
- Insecure deserialization / plugin loading (polymorphic type binding)
- CI/CD pipeline compromise / dependency confusion attack
- SSRF → cloud metadata → credential theft → control plane takeover
- Template injection (server-side template rendering with user input)
- OS command injection via shell-outs with user-controlled arguments
- XML External Entity (XXE) processing leading to file read or SSRF

**Goal: Payment/Billing Abuse (Business Logic)**
- Coupon/referral abuse, replay attacks, race conditions
- Idempotency flaws in payment capture/refund flows
- Role bypass to access "admin billing" endpoints
- Price/quantity manipulation in client-controlled parameters

**Goal: Silent Compromise (Persistence + Evade Detection)**
- Missing audit logs for auth events, admin actions, data exports
- Missing alerting for credential stuffing, privilege probing, anomalies
- Log tampering/log injection via unsanitized user input in log entries
- Debug endpoints and verbose errors leaking internals in production
- Weak incident response hooks / no runbooks

- Enumerate each attack from ALL five attacker perspectives

### Stage 2: Attack Surface Mapping
Map ALL of these surfaces - do not skip any category:

**External entry points (internet-facing):**
- Web UI (SPA/SSR) - all user-generated content surfaces (profiles, comments, uploads) → XSS/CSRF/session risks
- Public APIs (REST/GraphQL) → BOLA, excessive data exposure, mass assignment, rate-limit, auth issues
- Auth endpoints (/login, /logout, /reset, /verify-email, /mfa, /oauth/callback) → ATO and token risks
- File upload & download endpoints → unrestricted upload, malware persistence, path traversal, parser exploits
- Webhook receivers and callback URLs → spoofing, replay, SSRF, desync, tenant confusion
- Redirect endpoints (login redirect, deep link, "returnUrl") → open redirect + phishing + OAuth token theft
- Docs and tooling (Swagger/OpenAPI UI, GraphiQL, introspection, error pages) → information disclosure

**Internal/high-privilege surfaces (often forgotten):**
- Admin portal and internal APIs (force-browsable from external?)
- Background workers (import/export, file processing, email sending, PDF rendering)
- Queue/topic consumers (replay/idempotency, poison messages)
- Secrets stores and config services
- Build/deploy pipeline (CI/CD) - artifact signing, dependency trusts, runner isolation

### Stage 3: Access Control Audit (OWASP A01 - #1 risk)

**3a. BOLA/IDOR (API1 Broken Object Level Authorization):**
- For EVERY endpoint that takes an object ID: is ownership/tenant enforced server-side?
- Is authorization derived from session/token (NOT from request parameters)?
- Are IDs opaque (UUIDs) or sequential/guessable?
- Are list endpoints tenant-scoped and filter-proof?
- Is there a centralized authorization layer checking `(actor, action, resource)`?
- Is DB-level row-level security used where feasible?
- Automated authorization tests per endpoint and object type?

**3b. Function-level Authorization (API5):**
- Admin/privileged endpoints - can low-priv users reach them via force-browsing?
- Are admin surfaces on separate domains with separate sessions and strict MFA?
- Are debug/admin endpoints removed from public exposure?
- Check: unauthenticated, low-priv, tenant admin, platform admin - each should get correct 401/403
- Do error responses from forbidden endpoints leak route existence or sensitive details?

**3c. Property-level Authorization (API3 Mass Assignment / Excessive Data Exposure):**
- API responses - do they leak sensitive fields (role, tenantId, internal IDs, isAdmin)?
- Can update requests (PATCH/PUT) modify restricted properties not shown in UI (mass assignment)?
- Are explicit request/response DTOs used with strict field allowlists?
- Are GraphQL schemas exposing internal fields via introspection?
- Is response data minimized ("need-to-know" serialization)?

**3d. Tenant Isolation:**
- Is `tenant_id` derived from session (not client-supplied)?
- DB-level row security or application-enforced scoping on every query?
- Shared caches - are cache keys tenant-scoped to prevent cross-tenant leakage?
- Background jobs - do they enforce tenant context on every operation?

**3e. SSRF (folded into A01 in OWASP 2025):**
- Any URL-fetch features (link previews, image proxy, webhook "test," PDF renderer, RSS import, OAuth metadata)?
- Are internal/metadata destinations (169.254.169.254, link-local, private IPs) blocked?
- Is DNS rebinding protected against (validate after resolution)?
- Are redirects followed? If so, is each hop re-validated?
- Are responses from fetched URLs exposed to the user?
- Is fetching done in isolated workers with minimal IAM and strict egress?

### Stage 4: Authentication & Session Audit (OWASP A07)

**4a. Password Reset & Credential Recovery:**
- Reset tokens: short-lived, single-use, stored hashed? Invalidated on password change?
- Host header poisoning: can attacker forge reset link domains?
- "Change email then reset" flow: does changing email require step-up auth?
- User enumeration: are error messages and response timing indistinguishable for valid vs invalid accounts?

**4b. MFA & Step-up Authentication:**
- MFA enforced for: login, password change, email change, MFA enrollment/removal, API key creation, billing changes, data export?
- Step-up auth: sensitive endpoints require a recent re-auth claim (time-bound)?
- MFA bypass: can attackers skip MFA via API calls, mobile endpoints, or legacy flows?

**4c. Session Management:**
- Session rotation on login and privilege elevation? Prior sessions invalidated?
- Absolute timeout AND inactivity timeout implemented?
- Cookie flags: Secure, HttpOnly, SameSite (strict or lax as appropriate)?
- Logout: does it invalidate session AND refresh tokens server-side? Replay must fail
- "Remember me": separate long-lived token with lesser privileges?
- Session fixation: can pre-auth session ID be reused post-auth?

**4d. JWT & Token Security:**
- Algorithm explicitly enforced (no `alg: none`, no RS→HS confusion)?
- Issuer, audience, expiry strictly validated?
- Secrets/sensitive data NOT in JWT payload?
- Key rotation mechanism in place?
- Short-lived access tokens + rotating refresh tokens with server-side revocation?

**4e. Rate Limiting & Anti-Automation:**
- Rate limits on: login, password reset, MFA verification, account creation, API endpoints?
- Multi-dimensional: per-IP, per-account, per-token, per-endpoint?
- Adaptive throttling and anomaly detection?
- GraphQL: query complexity/depth limits, batching limits?
- File upload: size limits and processing time limits?
- Credential stuffing detection (velocity, geography, device fingerprint)?

**4f. OAuth/OIDC Security:**
- Strict redirect URI allowlist (no wildcards, no open redirects)?
- State parameter: generated, stored, validated (prevents CSRF)?
- PKCE enforced for public clients?
- Issuer/audience/nonce validation?
- HTTPS-only callbacks?
- No insecure flows (implicit grant removed)?
- Login CSRF prevention (attacker can't bind victim to attacker account)?

**4g. Password Storage:**
- Modern algorithm: Argon2id, bcrypt, or scrypt with tuned cost parameters?
- Per-user salt (automatic with modern algorithms)?
- Server-side pepper?
- Upgrade-on-login strategy for legacy hashes?
- Weak/fast hashes (MD5/SHA1/SHA256 without stretching) must be flagged

### Stage 5: Injection & Input Handling Audit (OWASP A05)

**5a. Database Injection (SQL/NoSQL/LDAP):**
- All database access fully parameterized? No string concatenation into queries?
- Safe ORM patterns used? Raw query escape hatches audited?
- Filter/sort keys from user input: strict allowlist enforced?
- NoSQL operator injection ($gt, $ne, $regex) prevented?

**5b. OS Command Injection:**
- Any shell-out calls (`exec`, `spawn`, `system`, `shell=True`, backticks)?
- If unavoidable: strict allowlist of commands, proper argument escaping, no user-controlled arguments?
- Prefer library functions over shell commands

**5c. Template Injection (SSTI):**
- Server-side templates rendered with user input? (Jinja2, Twig, Pug, EJS, etc.)
- User input passed as template code rather than template data?
- Sandboxed template engine if dynamic templates are required?

**5d. XSS (Stored/Reflected/DOM):**
- Context-aware output encoding for every rendering context (HTML, JS, CSS, URL, attribute)?
- CSP deployed with nonces or hashes (no `unsafe-inline`)?
- Untrusted HTML: sanitized with proven sanitizer (DOMPurify equivalent) + strict allowlist?
- All user-generated content surfaces checked: profile fields, comments, markdown, admin dashboards, notifications, rich text editors, PDF/HTML exports
- DOM XSS: client-side code audited for unsafe sinks (innerHTML, document.write, eval, location assignment)?

**5e. File Upload & Processing:**
- File type validated by content (magic bytes), NOT just MIME header or extension?
- Stored outside web root with random opaque names (no user-controlled paths)?
- Uploaded files NOT directly executable from storage?
- Content-Disposition header set for downloads (attachment, not inline)?
- Antivirus/malware scanning in place? Quarantine before processing?
- Processing done in sandboxed workers with least privilege and strict egress?
- Per-tenant authorization on every file access (ties back to BOLA)?
- Path traversal prevention: zip-slip style filenames blocked?
- Decompression bomb / archive size limits enforced?
- Image/PDF/document processing libraries up-to-date (parser exploits)?

**5f. Deserialization:**
- No polymorphic type binding of untrusted data?
- Safe formats only (JSON with strict schema validation)?
- If unavoidable: allowlist of types, dangerous features disabled?
- All deserialization points identified: API input, cookies, session stores, caches, message queues, file imports?
- Strict size limits on deserialized payloads?

**5g. XML/XXE:**
- DTD processing disabled in all XML parsers?
- External entity resolution disabled?
- XML inputs identified: SOAP integrations, SAML assertions, XML uploads/imports, document converters?
- Hardened parser configuration per OWASP XXE prevention guidance?
- Input size limits on XML payloads?

### Stage 6: CSRF & CORS Audit

- CSRF tokens enforced on ALL state-changing requests (POST/PUT/PATCH/DELETE)?
- SameSite cookie attribute set (Strict or Lax)?
- Double-submit cookie or synchronizer token pattern implemented?
- CORS: restricted to specific trusted origins only?
- CORS: wildcard origin (`*`) NEVER combined with `Access-Control-Allow-Credentials: true`?
- CORS preflight caching appropriate?
- State-changing requests via GET blocked?

### Stage 7: Webhook & Integration Security

- Webhook receivers: HMAC signature verification with per-tenant secrets?
- Timestamp validation to prevent replay attacks?
- Idempotency keys to prevent duplicate processing?
- Tenant binding: webhook events cannot be replayed cross-tenant?
- Strict schema validation on incoming webhook payloads?
- Webhook-driven URL fetching: SSRF controls applied?
- Webhook secret rotation mechanism?
- "Test webhook" feature: does it expose SSRF or bypass validation?

### Stage 8: Supply Chain & Secrets Audit (OWASP A03, A04, A08)

**8a. Dependency & Build Integrity:**
- Dependencies pinned with lockfiles (package-lock.json, yarn.lock, etc.)?
- Automated CVE scanning (Dependabot, Snyk, npm audit, etc.)?
- Private registries and scope allowlists where possible?
- Dependency confusion attack protection (reserved package names, .npmrc config)?
- Artifacts signed with provenance verification (SLSA-like controls)?
- Protected release pipeline: who can trigger releases? Review gates?
- Runner isolation: CI runners cannot access production secrets or other tenants' builds?
- Untrusted fork builds: cannot access CI secrets?

**8b. Secrets Management:**
- Secrets stored in a proper secrets manager (not in code, configs, env files, or client bundles)?
- Rotation policy enforced? Short-lived credentials preferred (workload identity)?
- No hardcoded keys in: source code, git history, CI logs, debug endpoints, client JavaScript, backups?
- Secret scanning on commits (pre-commit hooks + CI scanning)?
- Logs redact secrets (tokens, passwords, API keys never appear in log output)?
- Old/rotated secrets verified as invalid immediately?
- Client-side bundles: no secrets embedded (API keys, OAuth secrets)?

### Stage 9: Security Observability Audit (OWASP A09)

- **Events logged with context**: auth successes/failures, authz denials, admin actions, billing actions, data exports, webhook events, sensitive reads - all with correlation IDs, user context, timestamps?
- **Logs do NOT contain**: PII, passwords, tokens, session IDs, API keys, credit card numbers?
- **Log injection prevention**: user input sanitized before inclusion in log entries?
- **Alert rules configured** for: credential stuffing patterns, privilege probing, data export anomalies, auth brute force, unusual API patterns?
- **Immutable log storage**: tamper-proof, append-only, with integrity verification?
- **Incident response**: runbooks exist and are tied to alert triggers?
- **Retention policy**: logs retained long enough for forensics but comply with data protection?
- **Log access controls**: who can read/modify logs?

### Stage 10: Resilience & Configuration Audit (OWASP A02, A10)

**10a. Fail-Closed / Exception Handling (OWASP A10 - new in 2025):**
- Security controls fail CLOSED (deny on error, not allow)?
- Auth/authz dependency failures: does the app fall back to "allow" path?
- Generic error responses (no stack traces, internal paths, DB errors, framework versions)?
- Error messages consistent between valid/invalid states (no information leakage)?
- Retry logic: does it create replay opportunities?
- Chaos/fault injection tested for auth and payment dependencies?

**10b. Security Headers Baseline:**
- Content-Security-Policy (CSP) with nonces/hashes, no `unsafe-inline`?
- Strict-Transport-Security (HSTS) with appropriate max-age and includeSubDomains?
- X-Frame-Options / CSP frame-ancestors (clickjacking prevention)?
- X-Content-Type-Options: nosniff?
- Referrer-Policy: strict-origin-when-cross-origin (or stricter)?
- Permissions-Policy: restrict unnecessary browser features?
- Headers consistent across ALL routes (including admin, API, error pages)?

**10c. Production Hardening:**
- Debug endpoints and dev tooling removed from production?
- Verbose error pages disabled? Stack traces suppressed?
- Directory listing disabled?
- Default credentials changed? Unnecessary features/services disabled?
- TLS properly configured (strong ciphers, no downgrade)?
- Environment parity: staging mirrors production security config?

### Stage 11: GraphQL-Specific Audit (if applicable)

- Introspection disabled in production?
- Query complexity limits enforced?
- Query depth limits enforced?
- Batching controls (prevent mass-query execution)?
- Authorization enforced in every resolver (not just at schema level)?
- Field-level authorization for sensitive data?
- Rate limiting per query complexity cost?
- Mutation input validation as strict as REST equivalents?

### Stage 12: LLM/AI Feature Audit (if applicable)

- **Prompt injection**: user input treated as data, never as instructions? System prompts protected?
- **Tool/function calling**: tools have explicit permission gating and least-privilege access?
- **Cross-tenant context leakage**: embeddings, memory, vector DB, and RAG retrieval tenant-scoped?
- **Output handling**: model output treated as untrusted - encoded/sanitized before downstream use (HTML, SQL, commands)?
- **Data exfiltration via model**: can crafted input cause model to reveal system prompts, secrets, or other tenants' data?
- **Agent action authorization**: all tool-triggered actions require explicit user authorization and are fully logged?
