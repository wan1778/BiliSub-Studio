# BiliSub Studio UI/UX Contract

This document is the persistent design contract for BiliSub Studio. It applies the priority model and interaction guidance from nextlevelbuilder/ui-ux-pro-max-skill to a desktop utility app. The persistent token and interaction source of truth is `design-system/bilisub-studio/MASTER.md`. It is intentionally not an Awwwards/landing-page brief: usability, learnability, and operational clarity outrank visual novelty.

## Design read

BiliSub Studio is a dark-first desktop utility for people who want to download Bilibili media/subtitles, OCR hard subtitles, and perform light video cleanup without learning a professional editor.

Target language: **calm technical utility** — premium, restrained, obvious, and fast to learn.

Design posture:
- Familiar product layout; visual interest comes from hierarchy, not novelty.
- Low motion: only state/feedback/direct-manipulation motion; respect `prefers-reduced-motion`.
- Medium-high density: enough information for repeat use, with advanced/precision controls progressively disclosed.
- Accessibility-first: visible keyboard focus, 4.5:1 normal-text contrast, non-color state cues, and keyboard/numeric alternatives to drag actions.

## Non-negotiable UX principles

1. **A first-time user must know the next action within five seconds.** Every screen has one clearly dominant primary action.
2. **Use task language, not implementation language.** Prefer “Tải video”, “Quét phụ đề”, “Làm mờ vùng” over “Resolve”, “Engine”, “Filter graph”. Technical diagnostics belong in expandable details/logs.
3. **Direct manipulation beats coordinate entry.** Region selection for OCR and video editing happens on the preview. Numeric fields/sliders are secondary precision controls.
4. **Progressive disclosure.** Show safe defaults first. Advanced quality, codec, worker, threshold, OCR tuning, and diagnostics stay behind “Nâng cao”.
5. **Never make a destructive action primary.** Delete/reset/remove actions are visually secondary, require a deliberate action, and never sit beside the main CTA with equal weight.
6. **Preserve user work.** Closing/canceling an operation must not silently discard completed download checkpoints, OCR cues, or editor regions.
7. **State must be visible.** Every async action has an explicit idle/loading/success/error/cancelled state. Do not rely only on toast messages.
8. **Error messages explain the next step.** Example: “Thiếu model OCR. BiliSub sẽ tải lại bộ OCR.” rather than exposing only a raw file path.
9. **Keyboard and mouse both work.** Visible focus ring, sensible tab order, Enter for primary actions, Delete for selected editor regions, Space for preview play/pause where safe.
10. **Dark mode is the primary visual target.** Light mode is supported, but neither mode may be a simple color inversion.

## Information architecture

Use a small set of stable top-level tasks:

- **Tải xuống** — paste Bilibili URL, inspect metadata, download video/audio/subtitles.
- **OCR phụ đề** — select a local/downloaded video, choose subtitle region directly on preview, scan, review cues, export SRT.
- **Chỉnh video** — select downloaded/local video, draw one or more mask regions directly on preview, choose Blur/Mosaic/Cover, preview the effect, choose whole-video/time-range scope, then export an edited copy while preserving the source.
- **Cài đặt** — login/cookie, folders, tools, update channel, appearance, diagnostics.

Avoid adding a new top-level page for every feature. Small related operations stay inside the task that owns them.

## Screen anatomy

Each task screen follows the same hierarchy:

1. **Page title + one-sentence purpose**
2. **Primary input** (URL or file)
3. **Main preview/result surface**
4. **Primary action bar**
5. **Contextual options**
6. **Advanced/diagnostic disclosure**

The primary action must remain easy to find without scrolling on a typical desktop window.

## Visual system

### Color
- One accent family only: restrained BiliSub blue/cyan.
- Dark canvas uses charcoal/navy, never pure black.
- Surfaces use 2–3 elevation tones; avoid outlining every container.
- Red is reserved for destructive/error states; green for confirmed success; amber for warnings.
- No purple/blue AI gradients, neon glows, or decorative glassmorphism.

### Typography
- UI/body: system-native or Geist-like sans. Do not use novelty display fonts in controls.
- Use 500/600 weights for hierarchy; reserve 700+ for rare emphasis.
- Use tabular numerals/monospace for times, byte counts, percentages, frame/timecodes, and logs.
- Sentence case in Vietnamese UI; do not title-case every label.

### Shape
- Containers: 10–14 px radius.
- Inputs/buttons: 7–10 px radius.
- Pills only when the semantics are genuinely tag/status/chip-like.
- Avoid “every section is a bordered card”. Group by spacing and surface tone first; use borders only where they clarify containment.

### Icons
- One icon family per build, preferably Phosphor/Radix style.
- Icons support labels; they do not replace unfamiliar text labels for novice-critical actions.
- Consistent stroke/weight.

## Interaction rules

### Buttons
- One filled primary CTA per local task area.
- Secondary actions are neutral/outline/text.
- Hover is subtle; pressed state uses a small scale/translate response.
- Disabled states explain why through nearby helper text when the reason is not obvious.

### Loading
- Preserve the layout while loading. Prefer inline progress/skeletons over blocking spinners.
- Long operations show: current stage, progress, speed/ETA when meaningful, and Cancel.

### Errors
- Inline at the owning control/surface.
- Raw stderr/log text goes into “Chi tiết kỹ thuật”, not the primary message.
- Offer one concrete recovery action when possible: Retry, Reinstall OCR, Open folder, Copy details.

## Preview interaction contract

OCR and video editing share the same interaction grammar.

### Region creation
- Drag on the media preview to create a rectangle.
- Click to select.
- Drag selected rectangle to move.
- Eight handles resize it.
- Delete/Backspace removes selected region with Undo available.
- Coordinates are stored normalized to the video frame, not viewport pixels.

### OCR
- Default first region suggests the lower subtitle band, but remains editable.
- Video transport controls must sit outside the rendered video pixels; native controls are forbidden on OCR preview because they obscure bottom subtitles.
- ROI overlay must compensate for letterboxing/pillarboxing and map against the actual contained source frame.
- Sliders/numeric values become optional precision controls under “Tinh chỉnh vùng”.
- Current OCR text/confidence is shown adjacent to the preview, not far below it.

### Video cleanup editor
- Multiple regions supported.
- Effect per region: Blur / Mosaic / Cover.
- Region can apply to whole video or a time range.
- Timeline visually shows region spans.
- Preview shows the effect on the current frame without rendering the full video.
- Export writes a new file by default; never overwrite source silently.

## Beginner-first defaults

A new install should not require the user to understand:
- yt-dlp
- DASH
- codec IDs
- worker counts
- FFmpeg filters
- OCR model file names
- API tokens

Expose these only in advanced/diagnostic views.

Recommended default path:

1. Paste URL
2. BiliSub reads metadata automatically
3. Choose quality from plain-language options
4. Click **Tải video**
5. When complete, show immediate next actions: **OCR phụ đề** / **Chỉnh video** / **Mở thư mục**

## Anti-slop audit before every UI release

Reject a UI change if any of these are true:
- More than one obvious primary CTA competes in the same area.
- Three or more identical cards are used merely to fill space.
- Critical actions are icon-only without labels.
- Every container has border + shadow + rounded corners.
- A toast is the only indication of failure.
- Advanced settings are visible before the basic workflow.
- The user must type X/Y/W/H values to select a visual region.
- Dark mode contains pure black slabs or oversaturated neon blue.
- Motion delays task completion or moves controls while the user is targeting them.
- The screen explains the implementation instead of the task.

## Change protocol

For every UI change:
1. Scan the existing page and interaction path.
2. Diagnose hierarchy/usability problems before styling.
3. Make the smallest change that improves the task.
4. Preserve IDs/API contracts unless the change plan explicitly includes frontend-backend migration.
5. Verify both dark and light modes.
6. Verify keyboard focus/disabled/loading/error states.
7. Verify a novice path with no developer knowledge.
8. Only then add polish/micro-motion.

## OCR scan accuracy contract

The video element is a preview/ROI editor only. Full-video OCR accuracy must never depend on browser playback rate or `requestVideoFrameCallback`. The scan action is a backend job with explicit progress, cancel feedback, and an actual measured realtime speed. Labels must describe sampling/accuracy (`Accurate 8 FPS`, etc.), not imply a playback multiplier is an accuracy guarantee.
