# BiliSub Studio — Master Design System

Source: adapted from `nextlevelbuilder/ui-ux-pro-max-skill` for a desktop web UI embedded in a portable Windows utility. This is the persistent source of truth for UI changes in BiliSub Studio.

## Product read

- Product: desktop productivity / media utility.
- Audience: first-time users who should succeed without reading documentation, plus repeat users who need visible status and predictable controls.
- Primary tasks: download subtitles, download video, OCR hard subtitles, clean video regions, settings/login/update.
- Visual direction: dark-first calm technical utility. Professional and restrained; no marketing-page theatrics.
- Motion: low. Motion exists only to explain state changes and direct manipulation.
- Density: medium-high. Keep the primary action obvious; place precision and diagnostics behind progressive disclosure.

## Priority rules

1. Accessibility and state clarity before aesthetics.
2. Direct interaction must have keyboard/single-control alternatives.
3. Long operations must expose stage/progress/cancel and preserve completed work when safe.
4. One primary CTA per task area; destructive actions are subordinate.
5. Use a 4/8 px spacing rhythm and predictable desktop breakpoints.
6. Normal text contrast must meet 4.5:1 in both themes; focus state must remain visible.
7. Use semantic color tokens; no color-only state communication.
8. Respect `prefers-reduced-motion`; avoid layout-shifting animation.
9. Forms use visible labels, helper text for non-obvious controls, and inline recovery messages.
10. Technical stderr/logs live under "Chi tiết kỹ thuật", not as the primary error message.

## Tokens

### Layout
- spacing: 4 / 8 / 12 / 16 / 24 / 32 px
- control radius: 8–10 px
- container radius: 12–16 px
- desktop task max width: fluid within app shell; avoid fixed pixel page widths
- editor inspector: ~340 px at wide desktop, collapses below main preview under 1100 px

### Color roles
- canvas-dark: charcoal/navy, never pure black
- surface-dark: 2–3 restrained elevation tones
- accent: one BiliSub blue family
- success: green only with text/status context
- warning: amber only with text/status context
- error/destructive: red only with text/status context
- light mode: independent contrast mapping, not a raw inversion

### Typography
- UI: Segoe UI Variable / Segoe UI / system sans
- timecodes, byte counts, progress figures and logs: Cascadia Mono / Consolas / monospace
- sentence case for Vietnamese controls
- labels 12–13 px, body 13–16 px, task title ~22 px

## Interaction contract

### Navigation
- Stable top-level tasks only: Phụ đề / Video / OCR phụ đề / Chỉnh video / Cài đặt.
- Use plain task language. Do not expose backend terminology as navigation labels.

### Async operations
- idle → working → success/error/cancelled must be visibly distinguishable.
- Disable duplicate-submit while work is active.
- Provide Cancel for long operations.
- Keep technical logs expandable.

### Preview/direct manipulation
- Drag empty preview to create a region.
- Click region to select; drag region to move.
- Eight handles resize the selected region.
- Delete/Backspace removes; Ctrl/Cmd+Z restores the previous region state.
- Arrow keys nudge the selected region; Shift+Arrow uses a larger step.
- Numeric X/Y/W/H fields are the non-drag precision alternative.
- Space toggles play/pause in the video editor when focus is not inside a form field.
- Store coordinates normalized to the source video frame, never viewport pixels.

### Video cleanup
- Region effects: Blur / Mosaic / Cover.
- Region scope: whole video or explicit time range.
- Preview renders the effect on the current frame before export.
- Export always creates a new file and never overwrites the source video.

## Anti-patterns

- No emoji as structural UI icons.
- No neon/purple AI gradients or decorative glass effects.
- No animations that exist only for spectacle.
- No card border around every grouping when spacing/surface hierarchy is sufficient.
- No hidden destructive behavior.
- No coordinate-only editor workflow when direct preview manipulation is available.
- No drag-only critical action without keyboard/numeric alternatives.
- No raw model paths/FFmpeg stderr as the only user-facing recovery message.
