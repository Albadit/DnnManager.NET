# UI, UX and perceived performance

Review from the perspective of someone using the application every day. The UI should be clean, consistent,
responsive, predictable, easy to navigate, visually calm and useful rather than decorative.

## Less is more

Remove or avoid what doesn't help the user decide or act:
- buttons for actions that exist elsewhere or are rarely needed
- modals and confirmations for safe or easily undone actions (keep them for destructive ones)
- loading screens where existing content could stay visible
- tooltips that repeat a label; text that restates the obvious; repeated information
- animations that don't communicate state

Before adding UI the owner previously removed, don't - their decisions win over checklist items.

## Actions

- Important actions are easy to discover; destructive ones are visually distinct (danger style), separated from
  safe ones, confirmed, and default to the safe answer.
- Buttons are enabled only when the action can run; a disabled control says why when that isn't obvious.
- Labels say what happens ("Remove project", "Keep it"), consistent capitalization and terminology everywhere.
- Keyboard: Enter/Esc behave as expected in dialogs and pages; focus lands somewhere useful after navigation;
  icon-only controls have accessible names.

## Feedback - the user always knows

- **what is happening**: immediate response to every action (state change, progress, busy indicator in place)
- **whether it succeeded**: a visible result for long or invisible operations (not just a status line vanishing)
- **why it failed**: a plain message with the cause - no stack traces - plus a link to details
- **what to do next**: the fix or the next step, in the message

## Perceived performance

- respond instantly; do the work in the background
- refresh in place and keep current content visible; show skeletons only for a genuinely empty first load
- cache what was loaded and keep already-loaded screens alive between visits
- load incrementally; show partial results as they arrive
- optimistic updates only where the action rarely fails and is easy to roll back
- never block the UI thread on I/O

## Consistency

Same spacing, typography tokens, colors (from the theme, not literals), tables, forms, dialogs and empty states
across screens. Check both themes / modes and high UI scaling or small windows for clipped or broken layouts.
