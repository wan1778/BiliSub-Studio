# Codex handoff — BiliSub Studio

- Current main/base SHA: `8eb2a8a2600b37d29bfd0deaae9eeb94b3cda635`
- Current branch: `editor-preview-blur-01-17`
- Remote branch: `origin/editor-preview-blur-01-17`
- PR: none; do not create, merge, release or bump version without explicit authorization
- Last completed task: `VOICE-11 — Thay subtitle phải invalidate voice cũ khi cần` (`500e39792c471e74ab5d2a7b3474b718d76e3575`)
- Task currently running: `VOICE-12 — Preview track Việt`
- Exact next task after this one: `VOICE-13 — Mix voice + original audio`

## VOICE-12 scope and ownership

The production call path was already present and is intentionally kept single-owner:

```text
EditorTtsResult.VoiceTrack
  -> EditorPage._voiceTrack
  -> CurrentEditRequest(...)
  -> CreatePreviewSegmentAsync
  -> BuildPreviewSlice (timeline-only; keeps Audio/VoiceTrack)
  -> BuildVoiceAudioFilter + BuildPreviewArguments
  -> rendered segment -> MediaPlayer
```

Root cause for VOICE-12 was a missing dedicated regression gate for this end-to-end
Preview handoff. The existing implementation already passed the shared AUDIO-08
Preview/Export graph checks, but no contract explicitly protected promotion of the
validated Vietnamese master into processed Preview and its MediaPlayer activation.

## VOICE-12 changes

- Added `csharp/scripts/verify_editor_voice_preview_contract.py`.
- Added the VOICE-12 contract to `csharp/scripts/verify.ps1`.
- Regenerated `docs/migration/CSHARP_CODE_MAP.generated.md` so the checked-in map
  matches the already-landed VOICE-11 LocalTtsService method list.
- No production Editor, Subtitle, TTS, audio graph or media-source behavior was changed.

## Verification status

Targeted checks completed before the Windows gate:

- `python -m py_compile csharp/scripts/verify_editor_voice_preview_contract.py`: PASS
- `python csharp/scripts/validate_csharp_migration.py`: PASS
- `python csharp/scripts/verify_editor_voice_preview_contract.py`: PASS
- `python csharp/scripts/verify_editor_audio_preview_export_contract.py`: PASS
- `python csharp/scripts/verify_editor_voice_subtitle_invalidation_contract.py`: PASS
- .NET 10.0.400 Core contract tests: `71/71` PASS
- `python csharp/scripts/generate_csharp_code_map.py --check`: PASS after regeneration
- Windows x64 `csharp/scripts/verify.ps1`: pending on the committed clean checkout

Compile/static PASS is not a functional Windows field-test. The following remain
untested here until the real WinUI app is exercised with a source video, completed
local TTS, and a valid `voice-master.flac`:

- audible Vietnamese voice in processed Preview from start and after seeking;
- voice timing across internal preview segment boundaries;
- MediaPlayer behavior on the target Windows/WinUI machine.

## Constraints to preserve

- Windows desktop app: C# + .NET 10 + WinUI 3; no web/demo replacement.
- Never overwrite source media.
- Translation, ASR and TTS remain local; Chinese -> Vietnamese uses the project translation skill.
- Preview/Export must use the same request-owned source audio and Vietnamese voice semantics.
- Keep one event/owner, one button/handler, no handler-calls-handler.
- Do not add Repair/Fix/Parity layers when the real owner can be corrected.
- One small task and one targeted regression at a time; do not reopen Subtitle/Blur without a demonstrated regression.
- No version bump, release, PR or merge without explicit authorization and passing gates.
