# Usability: analysis stages

## Contents

- Analysis Process (10 Stages)
  - Stage 1: User Flow Mapping and Consistency Audit
  - Stage 2: Cognitive Load and Progressive Disclosure
  - Stage 3: Accessibility - Perceivable (WCAG 2.1 AA)
  - Stage 4: Accessibility - Operable (WCAG 2.1 AA)
  - Stage 5: Accessibility - Understandable (WCAG 2.1 AA)
  - Stage 6: Accessibility - Robust (WCAG 2.1 AA)
  - Stage 7: Error Messages and User Feedback
  - Stage 8: API Ergonomics and Developer Experience
  - Stage 9: CLI Interface Ergonomics
  - Stage 10: Documentation, Onboarding, and Internationalization

## Analysis Process (10 Stages)

### Stage 1: User Flow Mapping and Consistency Audit
- Map primary user journeys (happy paths and error paths)
- Check **internal consistency**: do similar tasks have similar flows, layouts, labels, icons, and interaction patterns across all screens?
- Check **external consistency**: does the interface align with familiar industry conventions and platform standards?
- Inconsistent navigation cues slow task completion and increase cognitive load
- Verify:
  - Navigation pattern is uniform (e.g., not top navbar on some screens and side menu on others)
  - Buttons with the same function have the same label/icon everywhere (e.g., one standard "Save" icon/text)
  - Terminology is consistent across the app (no synonyms for the same concept)
  - Layout grids and spacing follow a unified design system / style guide

### Stage 2: Cognitive Load and Progressive Disclosure
- Assess whether the interface minimizes memory burden - apply **"recognition rather than recall"** heuristic
- Actions, options, and objects should be visible and self-explanatory, not hidden in menus or requiring users to remember previous steps
- Check for **progressive disclosure**: show only primary/essential options by default; reveal advanced options on demand
- Verify:
  - Screens are not cluttered with advanced options and dense text
  - Controls are visible and self-explanatory
  - Related elements are grouped logically (proximity principle)
  - Navigation affordances are clear - users can tell where they are and where they can go
  - Complex workflows use step indicators, progress bars, or breadcrumbs

### Stage 3: Accessibility - Perceivable (WCAG 2.1 AA)

**3a. Non-Text Content (WCAG 1.1.1):**
- Every informative image has descriptive alt text conveying its meaning or function
- Decorative images have empty alt (`alt=""`)
- Image-buttons have text equivalents describing the function
- Icon-only buttons have `aria-label` or visible text
- Missing or placeholder alt text ("image", "logo") is a CRITICAL finding

**3b. Color and Contrast:**
- **WCAG 1.4.3 Contrast (Minimum)**: all text meets at least 4.5:1 contrast ratio with its background (3:1 for large text ≥18pt or 14pt bold)
- **WCAG 1.4.1 Use of Color**: color is NOT the sole means of conveying information - status, errors, required fields, links must use secondary indicators (icons, underlines, text labels, patterns) in addition to color
- Low contrast text (e.g., gray on white) is a HIGH finding
- Status indicated only by red/green color without icon or text is a HIGH finding

**3c. Responsive and Zoomable:**
- Content is readable and functional at 200% zoom (WCAG 1.4.4)
- No horizontal scrolling at 320px viewport width (WCAG 1.4.10 Reflow)
- Text spacing can be adjusted without loss of content (WCAG 1.4.12)

**3d. Media:**
- Captions provided for pre-recorded audio/video (WCAG 1.2.2)
- Audio descriptions for pre-recorded video (WCAG 1.2.5)

### Stage 4: Accessibility - Operable (WCAG 2.1 AA)

**4a. Keyboard Accessibility (WCAG 2.1.1):**
- ALL functionality is operable via keyboard - no mouse-only interactions
- Tab order is logical (left-to-right, top-to-bottom or follows visual layout)
- No keyboard traps - users can navigate away from any element (WCAG 2.1.2)
- Custom components (dropdowns, modals, date pickers) are keyboard-operable
- Test with keyboard-only navigation to verify no dead-ends

**4b. Focus Management:**
- **Visible focus indicators** (outline, ring, highlight) on every interactive element (WCAG 2.4.7)
- Focus indicator contrast meets 3:1 ratio with adjacent colors (WCAG 1.4.11)
- Focus is managed correctly on dynamic content changes (modals trap focus, return focus on close)
- `:focus` styles are not suppressed (`outline: none` without replacement is a HIGH finding)

**4c. Navigation Aids:**
- Skip navigation links provided for repetitive content (WCAG 2.4.1)
- Page titles are descriptive and unique (WCAG 2.4.2)
- Headings and labels describe topic or purpose (WCAG 2.4.6)
- Sufficient time provided for interactions - no auto-advancing without user control (WCAG 2.2.1)

### Stage 5: Accessibility - Understandable (WCAG 2.1 AA)

**5a. Language and Readability:**
- Page language is identified via `lang` attribute on `<html>` (WCAG 3.1.1)
- Language changes within the page are marked (WCAG 3.1.2)
- Navigation is consistent across pages (WCAG 3.2.3)
- Components behave predictably - no unexpected context changes on focus or input (WCAG 3.2.1, 3.2.2)

**5b. Forms - Labels, Instructions, and Errors:**
- **WCAG 3.3.2 Labels or Instructions**: every input field has a clear, visible `<label>` (or `aria-label`) describing its purpose - placeholder text alone is NOT a label
- **WCAG 3.3.1 Error Identification**: errors are identified in text (not just color) and describe what went wrong
- **WCAG 3.3.3 Error Suggestion**: error messages suggest how to fix the problem (e.g., "Password too short, minimum 8 characters")
- **WCAG 3.3.4 Error Prevention**: for legal/financial/data submissions, allow review/correction before submit
- Labels are programmatically associated with inputs (`for`/`id` or wrapping `<label>`)
- ARIA attributes are used correctly so screen readers announce field purpose

### Stage 6: Accessibility - Robust (WCAG 2.1 AA)
- Valid, semantic HTML (correct heading hierarchy, landmark regions, lists, tables)
- ARIA attributes used correctly - no conflicting or redundant ARIA
- Custom components follow WAI-ARIA Authoring Practices (roles, states, properties, keyboard interaction patterns)
- Compatible with major assistive technologies (screen readers: NVDA, JAWS, VoiceOver; magnifiers; switch devices)
- Test with axe, Lighthouse, or equivalent automated tools AND manual screen reader testing

### Stage 7: Error Messages and User Feedback
- Error messages must use **plain language**, specify what failed, and suggest actionable next steps
- Bad: "Something went wrong" or cryptic error codes with no guidance
- Good: "Unable to save. Check your internet connection and try again."
- Errors are shown prominently in the UI (not in browser console or default dialogs)
- Use color (red) AND icon AND text together to draw attention (WCAG 1.4.1 compliance)
- For validation: place error messages next to the offending field, not in a distant banner
- For system/API errors: detect and relay specific cause if possible (e.g., "Server is busy, please wait")
- Provide contextual help: tooltips on hover/focus for complex features, brief examples under inputs, "Learn more" links
- Follow the heuristic: "Help users recognize, diagnose, and recover from errors"

### Stage 8: API Ergonomics and Developer Experience

**8a. RESTful API Conventions:**
- Noun-based resource paths with consistent pluralization (e.g., `POST /orders` not `/create-order`)
- Standard HTTP verbs for CRUD operations
- Consistent parameter names, types, and casing across all endpoints
- Stable, versioned URLs - breaking changes go through versioning
- All endpoints have fully documented schemas with example requests AND responses
- Include example requests in multiple languages/tools (curl, JavaScript, Python)

**8b. API Error Responses:**
- Standardized error response format across all endpoints (consistent JSON structure)
- Error payloads include: error code, human-readable message, and **remediation hint** (what the consumer should do)
- HTTP status codes used correctly and consistently (400 ≠ 500)
- Bad: `400 Bad Request` with no body or `{"error": "invalid"}`
- Good: `{"error": "validation_error", "message": "Email format invalid", "field": "email", "hint": "Use format: user@domain.com"}`

**8c. SDK and Client Library Quality:**
- Generated or hand-written SDKs follow language idioms
- Method names are clear, descriptive, and consistent
- Parameters are in intuitive order with sensible defaults
- Type-safe where the language supports it
- Progressive disclosure: simple operations require minimal parameters; advanced options available but not mandatory

### Stage 9: CLI Interface Ergonomics
- Consistent command grammar: `<tool> <resource> <action>` or `<tool> <verb> <noun>`
- Concepts and terminology match the product/API (reduce learning curve)
- All commands support `--help` and `--version` flags
- Flags have both long (`--output`) and short (`-o`) forms with clear, mnemonic names
- Sensible defaults allow common tasks without extra parameters
- Comprehensive help text: usage, description, examples, available subcommands
- Output is machine-parsable (JSON/CSV flag) for scripting
- Exit codes are meaningful and documented
- Interactive prompts for destructive operations (with `--force` / `--yes` override for automation)

### Stage 10: Documentation, Onboarding, and Internationalization

**10a. Documentation Quality:**
- Comprehensive documentation that tells the "story" of the product - overview, concepts, quickstart, reference, tutorials
- API docs include: authentication instructions, full endpoint specs, request/response examples, common use-case tutorials
- Include code samples for clarity
- Establish a process to keep docs in sync with code (versioning, changelogs)
- Stale/outdated documentation is a HIGH finding

**10b. Onboarding and First-Run Experience:**
- Guided onboarding flow that highlights key features or setup steps
- Progressive onboarding: show only essential options on first use, inline tips or setup wizards
- Allow skipping for experienced users
- Use known UX patterns: progress bars, checklists, coach marks, tooltips
- Blank-state screens should guide action ("Click here to create your first project"), not present an empty void
- Test onboarding with actual new users to identify friction

**10c. Internationalization (i18n) Readiness:**
- All UI text externalized (no hard-coded strings) - ready for localization
- Unicode (UTF-8) used throughout
- Layouts adapt to text length changes (longer translations, RTL scripts)
- Date, number, and currency formats are locale-aware
- `lang` attribute set on `<html>` and on elements with language changes (WCAG 3.1.1, 3.1.2)
- Input fields accept international formats (names, addresses, phone numbers)
