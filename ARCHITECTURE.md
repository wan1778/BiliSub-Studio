# BiliSub Studio — Architecture

> **Timeline V2 retired 2026-09-28.** The Timeline V2 multi-track editor
> (`csharp/src/BiliSubStudio.Core/Timeline`, `TimelineEditorPage`,
> Timeline contract tests and `verify_timeline_*` gates) was removed in full
> because it is no longer used — the classic Editor (`Chỉnh video`) is the
> single editing surface. Top-level navigation is now: Media, OCR, classic
> Editor, Settings.
>
> The previous revision of this document (graph-derived ownership map pinned
> to commit `e3fd178`, `main` 2026-09-08: 8.263 nodes · 21.130 edges ·
> 497 communities) described the Timeline-centric tree and is preserved in Git
> history only. Where any doc and the tree disagree, the tree wins.
>
> The `docs/editor-timeline/` folder was removed together with the subsystem.

## Current ownership

BiliSub Studio is a **C# / .NET 10 / WinUI 3** Windows desktop app:
Bilibili media download, local OCR/ASR/TTS and the classic clip editor,
with verified self-update from GitHub Releases.

- **Launcher** (`csharp/src/BiliSubStudio.Launcher`) — thin root launcher,
  no feature logic.
- **App** (`csharp/src/BiliSubStudio.App`) — WinUI shell, pages
  (`VideoPage`, `OcrPage`, `EditorPage`, `SettingsPage` …), pickers,
  startup. `BiliSubApplication` is constructed once by `MainWindow`.
- **Core** (`csharp/src/BiliSubStudio.Core`) — all services. The single
  composition root is `BiliSubApplication`
  (`Core/Application/BiliSubApplication.cs`).
- **tests** — contract/regression executables.

Single-gate invariants (unchanged):

- **Processes are spawned in exactly one place** (`ProcessRunner`).
- **Jobs in exactly one place** (`JobManager`).
- **FFmpeg is gated in exactly one place** (`ToolManager`).
- Every feature funnels through `JobManager` + the shared application log
  with secret redaction.

## Composed timeline (classic Editor)

The classic Editor (`Chỉnh video`) owns a composed timeline strip: a ruler plus
Video/Voice/Subtitle/Effect lanes, a playhead, and clip add/split/remove. The
rules below are the authored behaviour; the executable form lives in
`csharp/scripts/verify_editor_timeline_composition_contract.py`,
`verify_editor_timeline_ruler_contract.py`,
`verify_editor_timeline_trim_snap_contract.py`,
`verify_editor_timeline_zoom_contract.py`,
`verify_editor_timeline_playhead_contract.py`,
`verify_editor_timeline_playhead_drag_contract.py`,
`verify_editor_timeline_playhead_grab_contract.py`,
`verify_editor_timeline_lane_order_contract.py`,
`verify_editor_timeline_history_move_contract.py`,
`verify_editor_timeline_drag_feedback_contract.py`,
`verify_editor_trim_contract.py` and `verify_editor_composed_export_contract.py`.

- **Time base.** The timeline axis, the clock and the playhead are expressed in
  *composed time*. The composed total is the sum of the clip durations
  (`EditorTimelineComposition.TotalDuration`). Source time is per clip:
  `EditorTimelineClip.SourceIn/End` are coordinates inside that clip's own file.
- **Clip ownership.** A clip is *primary* when its source path is the project
  source (`EditorPage.IsPrimaryTimelineClip`). The burned subtitle, the
  blur/mosaic regions and the voice track belong to the primary clip only —
  their timecodes live in that file. Other clips keep their own picture and
  their own source audio.
- **Trim.** The project trim (`EditorProject.Trim`) is a range in the primary
  source's coordinates. It shortens the primary clip(s) only: each exported
  primary segment is the intersection of that clip with the trim. It never
  rewrites the timeline clips, so the timeline total can be slightly longer
  than the exported file. "Set the boundary at the current position" maps the
  composed position back to the primary source time
  (`EditorPage.CurrentPrimarySourceTime`) and refuses to act when the
  playhead sits on another clip, because that clip's timecodes belong to a
  different file.
- **Clip edge trim.** Every clip on the strip owns a draggable edge on each
  side, grabbed within **10 screen pixels** of the clip border
  (`EditorTimelineTrim.EdgeGrabPixels`, `EditorTimelineTrim.EdgeAt`); the
  subtitle cue edges keep their own, narrower 6 pixel zone. Hovering a grab
  zone turns the pointer into the horizontal resize cursor and draws a
  bright line on that edge (`TimelineEdgeHoverIndicator`), so the user sees
  which edge is grabbed before dragging. A drag only starts once the pointer
  moved **3 screen pixels** (`EditorTimelineTrim.MinimumDragPixels`), so a
  light click never leaves a garbage trim. Dragging the In edge cuts the head
  of that clip's source window: the
  source in point moves by the drag delta and the duration shrinks by the
  same delta, so the source out point stays put and the media that is kept is
  one continuous slice of the source file. Dragging the Out edge moves the
  source out point, so the duration grows or shrinks by the drag delta while
  the source in point stays put. The trim is clamped by the media handle
  (never before the start or past the end of the source file), by the
  neighbouring clip (Regular trim: a clip never cuts into the media of the
  adjacent clip that comes from the same file) and by one frame (the shortest
  clip keeps a single frame of its media, the frame rate of the clip's own
  media). The strip is gapless, so a trim reflows the clips that follow it
  instead of opening a hole: the In edge therefore stays on the boundary with
  the previous clip and the right boundary of the trimmed block is what moves.
  The algebra lives in `EditorTimelineTrim`
  (`ClampTrim`, `MoveEdge`, `Trim`); the page only calls it.
- **Snapping.** While an edge is dragged, the edge sticks to the nearest
  marker within **9 screen pixels** — a pixel threshold, not a time
  threshold, so the snap feels the same at every zoom level. The snap targets
  are the start and the end of the other clips, the playhead, the timeline
  markers (effect region, subtitle cue and voice window boundaries) and the
  project trim marks; the dragged clip never snaps to its own edges. The
  snapped position is drawn as a vertical guide on the strip. The math lives
  in `EditorTimelineSnapping` (`SnapTime`, `Candidates`).
- **Clip move (drag the clip body).** A composed strip is a gapless sequence,
  so moving a clip in time means moving it in the sequence: the clip is
  inserted before or after its neighbour. The edge grab zones stay untouched
  (10 screen pixels on each side, `EditorTimelineTrim.EdgeGrabPixels`), the
  body of the clip starts a move drag, and a drag only begins after the
  pointer travelled **5 screen pixels**
  (`EditorTimelineClipMove.DragThresholdPixels`), so a plain click still only
  selects the clip. Clicking the clip body **selects it and never seeks**:
  the seek belongs to the ruler and to the empty lane area
  (`EditorPage.TimelineVideoClipPressed` swallows the press, the ruler and the
  tracks keep their own `PointerPressed`). While dragging, the lane is redrawn
  for the pointer position: the other clips move to their new order and the
  dragged clip is an empty slot, while the dragged clip itself follows the
  pointer with its real length - its source in point and its duration are
  never touched, only its order changes
  (`EditorPage.ApplyTimelineClipMovePreview`,
  `EditorPage.ApplyTimelineClipPositions`). The insertion guide
  (`TimelineDropIndicator`) marks the exact boundary the clip will take **in
  the layout after the move**, computed on the moved clip list
  (`EditorTimelineClipMove.Preview`, `EditorTimelineClipMove.InsertionBoundaryTime`),
  so the guide never drifts away from the pointer and the strip the user
  sees is the strip the user gets. The insertion slot itself is decided on the
  layout the user sees: the pointer has to pass the middle of a neighbouring
  clip before the dragged clip changes slot
  (`EditorTimelineClipMove.InsertionIndex`), so dragging left and dragging
  right give the same result. Dropping the clip back on its own slot changes
  nothing and writes no history. The subtitle cue drag, the effect region drag
  and the trim edge drag keep their own gestures and are not affected.
- **Undo/redo.** The timeline keeps a real undo/redo history. Every timeline
  edit (add clip, remove clip, split clip, clip edge trim, clip body move)
  records the clip list before and after the edit and can be undone
  (`EditorTimelineHistory`: `Push`, `Undo`, `Redo`, bounded to **50** steps,
  a new edit drops the pending redo). Undo and redo restore the clip list,
  keep the selected clip selected, update the composed duration and the
  clock, refresh the preview and save the project. The history is dropped
  when another project is opened. `Ctrl+Z`/`Ctrl+Y` always prefer the
  **timeline** history and only fall back to the effect-region history when
  the timeline history has no step left
  (`EditorPage.UndoTimelineAsync`, `EditorPage.RedoTimelineAsync`,
  `EditorPage.HandleEditorRegionShortcut`); the status text names the command
  that just ran, so the user always knows which history was touched.
- **Preview.** Seeking maps composed time → (clip, source time) through
  `EditorTimelineComposition.Locate`; playback rolls over into the next clip at
  the boundary and parks the playhead at the composed total. Overlay rectangles
  and burned subtitles are only drawn for the primary clip.
- **Export.** One file per export: each clip is rendered separately (normalised
  to the same size, frame rate, codec and 48 kHz stereo; silence is inserted
  when a clip has no audio), then the segments are joined with the concat
  demuxer (`-c copy`, concat list with `ffconcat version 1.0` and a `duration`
  directive per file, explicit `video_track_timescale` so every segment shares
  one time base). Image/logo overlays are applied afterwards on the joined file
  using the composed duration.
- **Export trigger.** A composed timeline is an edit on its own: the export
  button must stay enabled when the project only has clips on the timeline
  (`EditorPage.TimelineNeedsComposedExport`). The same rule decides which
  request is built - if the primary clip was removed from the timeline, the
  export still follows the remaining clips and must not silently fall back to
  rendering the original project source.
- **Zoom.** Zooming the strip keeps its anchor on screen: the anchor holds
  its screen position (`EditorTimelineLayout.ZoomAnchorOffset`), the same
  rule as FilmCraft's `zoom_about`. The zoom slider and the +/- buttons have
  no pointer on the strip, so they anchor on the **left edge of the
  viewport** (`EditorTimelineLayout.ZoomAnchorOffsetAtViewportLeft`) - the
  edge next to the ruler: the time under the left edge is the same before
  and after the zoom and the strip only grows or shrinks to the right, the
  way Premiere, Resolve, CapCut and FilmCraft's zoom bar behave. Alt + mouse
  wheel zooms about the pointer (also FilmCraft); a plain wheel keeps
  scrolling the strip. A zoom never scrolls the strip to the playhead
  (FilmCraft does not either): the anchor would jump and the zoom would feel
  wrong. The zoom slider handler never assigns the slider value again,
  because WinUI assigns it while loading the XAML (re-entrant assignment
  breaks app startup).
- **Strip geometry.** Three widths are kept apart and each one has a single
  owner in `EditorTimelineLayout`: the **viewport width** is the ScrollViewer's
  own viewport (`EditorPage.TimelineViewportWidth`, so the lane label column
  and the toolbar are not counted), the **content width** is the strip itself
  (`ContentWidth` = duration x pixels per second, it may be narrower than the
  viewport) and a **clip width** is the clip duration on the strip x pixels
  per second (`ClipWidth`, never stretched to the viewport). The strip canvas
  is `FillWidth(contentWidth, viewportWidth)` wide, so a short strip still
  fills the visible width, and the ruler band, the lane backgrounds and the
  lane separators are drawn over that whole width: the ruler reaches the
  right edge of the viewport while its ticks still mark the real content only.
- **One scale.** The ruler ticks, the clips, the cues, the markers, the
  playhead, the snap guide and the pointer hit test all convert between time
  and pixels through `EditorTimelineLayout` with the same pixels per second:
  `x_content = time x pps`, `x_screen = x_content - scrollOffset`. The zoom
  readout is that same pixels per second, in logical pixels (DIP): a
  screenshot taken at 125% display scaling is 1.25 times wider than the
  number shown, the strip itself is not.
- **Strip layout.** The scroll offset is never negative
  (`EditorTimelineLayout.MaxScrollOffset`), and a strip that is shorter than
  the viewport scrolls back to offset 0
  (`EditorPage.ApplyPendingTimelineScroll`). The strip canvas is pinned to
  the left edge of its scroll viewer (`HorizontalAlignment="Left"`): a
  `Canvas` defaults to `Stretch`, and the WinUI ScrollViewer then *centers*
  a canvas that is narrower than the viewport
  (`ScrollViewer::ComputeHorizontalAlignment`), which would leave a blank gap
  between the lane labels and the ruler and would re-center the strip on every
  zoom. The strip canvas is at least as wide as the viewport, so a zoomed out
  strip never leaves that gap. The "Fit" button measures the strip viewport
  only and scrolls back to the left edge.
- **Toolbar groups.** The timeline toolbar is one compact row with three
  groups separated by thin rules: **clip editing** (`+ Video`, `Tách`, `Xoá`,
  `↩` hoàn tác, `↪` làm lại), **zoom and navigation** (the zoom readout, the
  zoom slider, `−`, `+`, `Vừa khung`) and **help** (`Phím tắt`). Every control
  keeps its name and its `AutomationProperties.AutomationId`, so the UIA tests
  that drive the toolbar keep working. The two history buttons are small
  icon buttons with a Vietnamese `AutomationProperties.Name`, they run the
  same commands as `Ctrl+Z`/`Ctrl+Y`, and they are enabled exactly while the
  timeline history has a step to undo or to redo
  (`EditorPage.RefreshTimelineClipTools`). The clip editing buttons (`+ Video`,
  `Tách`, `Xoá`) are locked while the app runs a job - export or preview
  render - so a running job can never be changed under the user's feet
  (`EditorPage.TimelineEditingAllowed`).

- **Ruler.** The ruler is a professional time scale. The major tick step is
  zoom driven (`EditorTimelineLayout.MajorTickSeconds`, the minor step is five
  times finer - `MinorTickSeconds`), never a hard-coded number of seconds. A
  major tick is a 2 pixel bright line, a minor tick a 1 pixel dim one, so the
  two are always distinguishable. A timestamp label is only drawn when two
  consecutive labels keep at least **72 screen pixels** between them
  (`EditorTimelineLayout.MinimumLabelPixels`, `LabelsFit`), and a label that
  would still overlap its neighbour is dropped instead of being stacked on top
  of it. The ruler band owns a background of its own, darker than the lane
  area, and a 1 pixel separator line on its bottom edge draws it off from the
  lane area. The band is drawn over the whole viewport width
  (`EditorTimelineLayout.FillWidth`), so the ruler reaches the right edge of
  the viewport while its ticks still mark the real content only, and the major
  ticks are continued as a low contrast vertical grid
  (`EditorPage.RenderTimelineGridLines`) through the lane area only - the minor
  ticks are not - so a clip can be aligned against a time mark without
  cluttering the strip. Ticks, labels and grid lines are drawn inside the
  visible window only (`EditorTimelineLayout.VisibleWindow`, `TickTimes`).
- **Lane order and lane height.** The lane order is one source of truth in
  Core (`EditorTimelineLane`): **Video** on top, **Voice** right under it, then
  **Subtitle** and **Effect** - the track order of Premiere and Resolve, where
  the video track is the top track and the audio track sits directly below it.
  The app maps its lane constants to that enum
  (`EditorPage.TimelineLaneVideo` …), the lane label column uses the same
  order, and the lanes are drawn in that order. The panel is tall enough for
  every lane: the ruler, the four lanes, the horizontal scroll bar, the
  toolbar, the gap and the panel padding are summed by
  `EditorTimelineLayout.PanelHeight`, and the XAML height is that value - a
  panel that is too short would cut the bottom lane away.
- **Lane boundaries.** The lanes are separated by
  a 1 pixel light line drawn on the lane edge with a 1 pixel dark line right
  below it, and the lane backgrounds alternate between a light and a dark
  tone, so the four tracks stay readable without covering the clips, cues or
  markers. An empty lane keeps its background. The lane label column is a
  separate grid column: it never scrolls with the strip, and every label row
  carries the same 3 pixel gap as the lane area, so a track name stays centred
  on its own row (`EditorPage.SyncTimelineHeaderRows`). A video clip shows its
  file name, trimmed with an ellipsis so it can never bleed into the
  neighbouring clip, marks its media region with a content accent bar, and the
  selected clip keeps a 2 pixel white border.
- **Playhead.** The playhead is a *frame* position, not a free floating real
  number: seeking snaps it to the media's frame grid (snaptime = frame index /
  fps, the same rule as FilmCraft's `FrameRate::snap`) using the frame rate of
  the media under the playhead (`EditorPage.TimelineFrameRateAt`,
  `EditorTimelinePlayhead.SnapToFrame`). The snapped position is a composed
  time, so it stays on the strip of a multi-clip timeline. The compact seek
  slider uses the same grid: its `StepFrequency` is set to one frame
  (`EditorPage.ApplyTimelineStepFrequency`), because WinUI snaps the thumb to
  that step while the user drags it. The indicator (`TimelinePlayhead`) runs
  from the ruler down to the last lane and is drawn above the lane canvas in
  the z order, so a lane background can never hide it; a round head
  (`TimelinePlayheadHead`, a `TimelinePlayheadHandle` control) marks the
  position on the ruler edge.
- **Dragging the playhead.** The playhead head is a real handle: it is hit
  testable, its cursor turns into the horizontal drag cursor, and pressing
  within **16 DIP** of it grabs the playhead instead of seeking
  (`EditorTimelinePlayhead.GrabPixels`, `IsWithinGrabRange`). The head is a
  direct child of the strip canvas, so it swallows the ruler's press: it owns
  its own `PointerPressed` and forwards it to the same owner that decides
  between "grab the playhead" and "seek" (`EditorPage.SeekTimelineFromPointer`).
  The head is exactly as wide as the grab zone
  (`EditorTimelinePlayhead.GrabZoneWidth`), otherwise the round button would
  cover the grab zone and no press could ever start a drag. The grab zone only
  opens on the **ruler band** (`EditorTimelinePlayhead.IsInRulerBand`), so a
  press in a lane still belongs to the clip edge trim and to the cue drag.
  While the pointer is down the playhead follows the pointer on the strip,
  snapped to the frame grid of the media under it, and the player is not
  touched yet. The drag reads the **real** cursor position from the operating
  system (`EditorPage.CurrentTimelinePlayheadCursorContentX`,
  `Win32Cursor`): while a pointer is captured the position carried by the
  pointer event stays frozen at the press position, so the event position
  alone would leave the playhead behind the mouse. The pointer is captured on
  the strip canvas, so a drag that leaves the strip still ends on the right
  position, and losing that capture ends the drag instead of freezing the
  strip in the "dragging" state (`EditorPage.TimelineContentGrid_PointerCaptureLost`).
  Dropping the playhead seeks the player, queues the project write and brings
  the strip back to the playhead
  (`EditorPage.SeekEditorToTimelineAsync`). A player that is already running
  never fights the drag: the playback position update is ignored while the
  pointer is down. A press outside the grab zone keeps the old behaviour and
  seeks to the pressed position.
- **Playback starts at the playhead.** The first **Play** runs from the
  **current playhead position**, not from the head of the strip
  (`EditorTimelinePlaybackStart.Resolve`): the start is the playhead, clamped
  so it can never run before the start marker of the strip (the trim range
  that the exported file keeps). A replay after the end of the strip restarts
  from that start marker, which is what the status line promises.
- **Playhead persistence.** The playhead belongs to the project: the composed
  position is stored with it (`EditorProject.PlayheadSeconds`, normalized by
  `EditorProjectStore.NormalizePlayhead`) and restored when the project is
  opened again, then brought into the viewport. The field is optional, so a
  project saved before it existed still opens - with the playhead at the start
  of the strip - and the project schema is unchanged. A seek - the slider, the
  ruler or a lane, a cue, the keyboard - queues the position and a debounced
  timer writes it once, 800 ms after the last seek (`EditorPlayheadSavePolicy`
  owns the window, `EditorPage.PlayheadSave` owns the timer), so one drag
  never writes the project file frame by frame. Playback never writes: the
  queued position is flushed once when the preview stops, and the write is
  dropped when another project is opened meanwhile.
- **Playhead auto-follow.** When a seek leaves the playhead outside the
  viewport, the strip scrolls to bring it back into view
  (`EditorTimelinePlayhead.RevealScrollOffset`, `FollowScrollOffset`): the
  playhead ends up 5% of the viewport from the left edge, as in FilmCraft's
  `scroll_to_reveal`. A zoom is not a seek: it keeps its anchor and may leave
  the playhead outside the viewport. The strip is never dragged along
  continuously: the follow only fires when the playhead itself moved to
  another frame, so scrolling the strip by hand leaves the view where the
  user put it.

## Keyboard shortcuts (classic Editor)

The classic Editor owns one shortcut registry. The registry lives in Core
(`csharp/src/BiliSubStudio.Core/Editor/EditorShortcuts.cs`) and is the single
source of truth: the default key set, the help dialog and the conflict check
all read from it. A binding carries an action id, a key, a modifier set, a
display name, a group (Playback and navigation / Timeline editing / Zoom and
view / Project) and a scope (application, editor page, timeline strip). The
page never owns a second, static key table. The authored behaviour is locked
by `csharp/scripts/verify_editor_shortcuts_contract.py` and by the
`editor shortcuts are declared once and conflicts are detected` contract test.

- **One id, one behavior.** The page registers a mapping id → behavior
  (`EditorPage.Shortcuts`) and every behavior calls a command the page
  already has: `TogglePlaybackAsync` (the Play/Pause button),
  `SplitTimelineClipAtPlayheadAsync` (the Split button),
  `RemoveSelectedTimelineClipAsync` (the Remove button) and
  `SaveProjectNowAsync`. A shortcut never re-implements an edit, and the
  buttons never grow a second implementation.
- **Frame stepping uses the real frame rate.** One frame is one frame of the
  media under the playhead (`EditorPage.TimelineFrameRateAt`), never an
  assumed 30 fps. `Shift` steps ten frames, `Home`/`End` jump to the start and
  the end of the composed timeline, `Ctrl+B` splits at the playhead and
  `Delete` removes the selected clip.
- **Focus rules.** The shortcuts listen in the WinUI tunnel phase
  (`PreviewKeyDown`), so they run before the focused control sees the key,
  but they never act while a text input has the focus: a `TextBox`,
  `PasswordBox`, `RichEditBox`, `AutoSuggestBox`, `NumberBox` or an editable
  `ComboBox` keeps its own keys, so Space types a space and Delete/Backspace
  edits the text instead of the timeline. A shortcut never calls `Focus()`,
  so it never steals the focus of a text box. Space belongs to the focused
  control when that control activates itself with it (button, combo, list),
  so Space after a toolbar click presses that button again instead of
  toggling the preview twice.
- **Dialogs and inactive windows.** While a page dialog is open
  (`EditorPage.BeginEditorDialogScope`) the shortcuts do not touch the
  timeline behind it. A shortcut only runs while the Editor page is the
  active page (the page is loaded and the workspace is visible) and the app
  window is active - Windows does not deliver key events to an inactive
  window.
- **Conflicts fail at registration.** Registering two bindings with the same
  key and modifier inside the same scope throws; the same key in two
  different scopes is two different commands and is allowed. The order of the
  modifiers does not matter: `Ctrl+Shift` and `Shift+Ctrl` are one key set.
- **Undo/redo are real.** `Ctrl+Z` and `Ctrl+Y` are declared in the registry
  and are bound to the real timeline commands (`EditorPage.UndoTimelineAsync`,
  `EditorPage.RedoTimelineAsync`), which walk the timeline history
  (`EditorTimelineHistory`). The help dialog reads the same registry, so it
  lists them as supported. Those commands prefer the **timeline** history and
  only fall back to the effect-region history when the timeline history has
  no step left; the effect inspector keeps its own buttons, so the two
  histories stay reachable while the keys and the status text always say
  which one ran.
- **Zoom.** `Ctrl` + wheel zooms the strip about the pointer, next to the
  `Alt` + wheel zoom of FilmCraft; both go through the same pointer-anchored
  zoom path and a plain wheel still scrolls the strip. The seek slider no
  longer steps itself with the arrow keys: the arrow keys belong to the
  shortcut registry, so the playhead moves exactly one frame of the media.
- **Help dialog.** The `Phím tắt` button in the Editor header opens a dialog
  that is built from the registry (`EditorPage.RenderEditorShortcutsHelp`),
  grouped by the declared groups, with the key and the description of every
  binding and an explicit mark for the bindings that are not supported yet.
  The button carries `AutomationProperties.Name` "Phím tắt" and the
  AutomationId `EditorShortcutsButton`; the dialog and its list carry the
  AutomationIds `EditorShortcutsDialog` and `EditorShortcutsList`.

## Entry points

- `csharp/BiliSubStudio.sln` for orientation; start from
  `BiliSubStudio.Core` hub types.
- Grep callers before editing — required by `AGENTS.md`.
