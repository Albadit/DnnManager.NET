# Usability: checklists

## Accessibility Checklist (WCAG 2.1 AA)

### Perceivable
- [ ] All informative images have meaningful, descriptive alt text (WCAG 1.1.1)
- [ ] Decorative images have `alt=""` (WCAG 1.1.1)
- [ ] Icon-only buttons have `aria-label` or visible text
- [ ] Color is not the sole means of conveying information - secondary indicators present (WCAG 1.4.1)
- [ ] Text contrast ≥ 4.5:1 for normal text, ≥ 3:1 for large text (WCAG 1.4.3)
- [ ] Non-text contrast ≥ 3:1 for UI components and graphical objects (WCAG 1.4.11)
- [ ] Content readable and functional at 200% zoom (WCAG 1.4.4)
- [ ] No horizontal scrolling at 320px width (WCAG 1.4.10)
- [ ] Captions for pre-recorded audio/video (WCAG 1.2.2)

### Operable
- [ ] All functionality operable via keyboard (WCAG 2.1.1)
- [ ] No keyboard traps (WCAG 2.1.2)
- [ ] Logical focus order matching visual layout (WCAG 2.4.3)
- [ ] Visible focus indicators with ≥ 3:1 contrast (WCAG 2.4.7, 1.4.11)
- [ ] `:focus` styles not suppressed without replacement
- [ ] Skip navigation links for repetitive content (WCAG 2.4.1)
- [ ] Page titles descriptive and unique (WCAG 2.4.2)
- [ ] Headings and labels describe topic/purpose (WCAG 2.4.6)
- [ ] No auto-advancing without user control (WCAG 2.2.1)
- [ ] Custom components keyboard-operable (dropdowns, modals, date pickers)
- [ ] Focus managed on dynamic content (modal focus trap, return focus on close)

### Understandable
- [ ] `lang` attribute on `<html>` element (WCAG 3.1.1)
- [ ] Language changes within page marked (WCAG 3.1.2)
- [ ] Navigation consistent across pages (WCAG 3.2.3)
- [ ] No unexpected context changes on focus/input (WCAG 3.2.1, 3.2.2)
- [ ] Every input has a visible `<label>` (not just placeholder) (WCAG 3.3.2)
- [ ] Labels programmatically associated with inputs (`for`/`id`)
- [ ] Errors identified in text, not just color (WCAG 3.3.1)
- [ ] Error messages suggest how to fix the problem (WCAG 3.3.3)
- [ ] Error prevention for legal/financial/data submissions (WCAG 3.3.4)

### Robust
- [ ] Valid, semantic HTML (correct heading hierarchy, landmarks, lists, tables)
- [ ] ARIA attributes correct - no conflicting or redundant ARIA
- [ ] Custom components follow WAI-ARIA Authoring Practices
- [ ] Tested with automated tools (axe, Lighthouse) AND manual screen reader testing

## API and CLI Ergonomics Checklist

### API
- [ ] Noun-based resource URIs with consistent pluralization
- [ ] Standard HTTP verbs used correctly
- [ ] Consistent parameter names, types, and casing
- [ ] Standardized error response format with code, message, and remediation hint
- [ ] All endpoints documented with schemas and example requests/responses
- [ ] Code examples in multiple languages
- [ ] Versioned API with breaking change policy
- [ ] Progressive disclosure - simple operations require minimal parameters

### CLI
- [ ] Consistent command grammar (`<tool> <resource> <action>`)
- [ ] Terminology matches product/API concepts
- [ ] `--help` and `--version` on all commands
- [ ] Long and short flag forms with mnemonic names
- [ ] Sensible defaults for common usage
- [ ] Machine-parsable output option (JSON/CSV)
- [ ] Meaningful, documented exit codes
- [ ] Interactive prompts for destructive operations
