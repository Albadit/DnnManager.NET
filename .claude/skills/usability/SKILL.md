---
name: usability
description: >-
  Usability, accessibility and UX analysis and fixes: user flows and consistency, cognitive load and progressive
  disclosure, WCAG 2.1 AA (alt text, contrast, keyboard, focus, labels, errors, semantics), error messages and
  feedback, API and CLI ergonomics, documentation, onboarding and i18n readiness. Use for UX reviews, accessibility
  audits, improving error messages, developer-experience reviews, and implementing UX fixes.
---

# Usability

Make sure interfaces are intuitive and accessible and give users and developers a good experience. Evaluate against
Nielsen's usability heuristics, WCAG 2.1 AA success criteria, API and CLI ergonomics, and documentation best
practices. Every finding must cite the specific heuristic or criterion violated and give an actionable fix.

## Mode

This skill both analyses and implements. Analyse and report by default. When the request is to build, fix or
change something, or carries `#fix`, also make the changes: fix Critical and High findings directly and list
Medium and Low ones in the report.

## What to cover

- Analyse **UI/UX patterns** for internal consistency (same app) and external consistency (industry conventions)
- Enforce **progressive disclosure** - simple things simple, advanced options revealed on demand
- Apply **"recognition rather than recall"** - make actions, options, and objects visible rather than hidden
- Assess **accessibility** compliance against WCAG 2.1 AA success criteria (Perceivable, Operable, Understandable, Robust)
- Audit **alt text** for all non-text content (WCAG 1.1.1)
- Audit **color contrast** ratios (4.5:1 normal text, 3:1 large text per WCAG 1.4.3) and **non-color indicators** (WCAG 1.4.1)
- Audit **keyboard operability** - all functionality via keyboard (WCAG 2.1.1), visible focus indicators (2.4.7), logical focus order (2.4.3), no keyboard traps (2.1.2)
- Audit **form labels, instructions, and error identification** (WCAG 3.3.1, 3.3.2, 3.3.3)
- Analyse **error messages** for plain language, specificity, and actionable recovery guidance
- Evaluate **API ergonomics** - RESTful conventions, consistent naming, documented schemas, descriptive error payloads with remediation hints
- Evaluate **CLI interfaces** - consistent command grammar, mnemonic flag names, comprehensive `--help`, sensible defaults
- Assess **documentation** quality - completeness, examples, quickstart guides, versioning, sync with code
- Evaluate **onboarding experience** - first-run flows, guided setup, progressive onboarding
- Check **internationalization (i18n)** readiness - externalized strings, locale-aware formats, RTL support, `lang` attributes

## Workflow

Work through these stages. The questions and checks for each are in [reference/analysis-stages.md](reference/analysis-stages.md):
read all of it for a full-scope review, or only the stages that match a focused scope.

1. User Flow Mapping and Consistency Audit
2. Cognitive Load and Progressive Disclosure
3. Accessibility - Perceivable (WCAG 2.1 AA)
4. Accessibility - Operable (WCAG 2.1 AA)
5. Accessibility - Understandable (WCAG 2.1 AA)
6. Accessibility - Robust (WCAG 2.1 AA)
7. Error Messages and User Feedback
8. API Ergonomics and Developer Experience
9. CLI Interface Ergonomics
10. Documentation, Onboarding, and Internationalization

## Reference

| File | Contents | Read |
|---|---|---|
| [reference/analysis-stages.md](reference/analysis-stages.md) | The analysis stages in full | Before analysing: all of it for a full-scope review, only the matching stages for a focused one |
| [reference/checklists.md](reference/checklists.md) | WCAG 2.1 AA checklist (perceivable, operable, understandable, robust); API and CLI ergonomics checklist | To confirm coverage and to fill the accessibility compliance summary |

## Output Format

For each finding, provide ALL of these fields:

```
### [SEVERITY] Usability Issue

- **Location**: File/component reference (or screen/page/endpoint)
- **Category**: Accessibility / UX / API / CLI / Documentation / i18n
- **User Impact**: Who is affected (screen reader users, keyboard users, low-vision, novices, developers, all users) and how
- **Current State**: What happens now (specific, observable behavior)
- **Expected State**: What should happen (cite heuristic or WCAG criterion)
- **Recommendation**: Specific fix with implementation approach
- **WCAG Criterion**: (if accessibility) e.g., 1.4.3 Contrast (Minimum) - include criterion number AND name
```

After all findings, ALWAYS provide these sections:

1. **Accessibility compliance summary** - which WCAG 2.1 AA criteria pass, which fail, which are untestable without runtime access
2. **Consistency audit** - internal consistency issues (within the app) and external consistency issues (vs. industry conventions)
3. **Cognitive load assessment** - where progressive disclosure is missing, where recognition-over-recall is violated
4. **Error experience assessment** - quality of error messages, recovery paths, and contextual help
5. **Developer experience assessment** - API naming, error payloads, documentation completeness, CLI ergonomics
6. **Priority remediation** - ordered by user impact × affected population size, highest first; accessibility CRITICAL items always at the top

## Guidelines

- **Always consider diverse users**: disabilities (visual, motor, cognitive, auditory), different devices (mobile, desktop, assistive tech), different locales and languages
- **Cite specific WCAG 2.1 AA criteria** for every accessibility finding - criterion number AND name
- **Cite Nielsen heuristics** for UX findings where applicable (consistency, recognition over recall, error recovery, etc.)
- Error messages must explain what went wrong AND how to fix it - generic messages are always a finding
- APIs should follow the **principle of least surprise** - predictable naming, consistent patterns, sensible defaults
- CLI tools should follow **discoverable command grammar** - consistent structure, comprehensive help, mnemonic flags
- Prefer **convention over configuration** - reduce decisions the user must make
- Provide **sensible defaults** for all optional parameters
- Make the **happy path obvious and easy** - primary actions should be visually prominent and reachable
- **Progressive disclosure** reduces overwhelm - show essentials first, reveal complexity on demand
- **Color is never sufficient alone** - always pair with text, icons, or patterns (WCAG 1.4.1)
- **Placeholder text is not a label** - always use `<label>` elements (WCAG 3.3.2)
- **Focus styles must not be suppressed** without a visible replacement (WCAG 2.4.7)
- Test with **automated tools AND manual assistive technology testing** - automated tools catch ~30-40% of accessibility issues
- Documentation must be **comprehensive, current, and include examples** - stale docs are a usability failure
- Onboarding must be tested with **actual new users** - the team's familiarity blinds them to friction
- i18n readiness means **externalized strings, UTF-8, adaptive layouts, locale-aware formats, and `lang` attributes**

## Follow-ups

After the report, offer these next steps in one line each; do not start them unprompted:

- Implement the usability improvements recommended in the report.
- Verify the accessibility changes meet WCAG requirements with the `compliance` skill.
