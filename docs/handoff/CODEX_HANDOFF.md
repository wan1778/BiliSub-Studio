# Codex handoff — BiliSub Studio

- Current main/base SHA: `8eb2a8a2600b37d29bfd0deaae9eeb94b3cda635`
- Current branch: `editor-preview-blur-01-17`
- Remote branch: `origin/editor-preview-blur-01-17`
- PR: none; do not create, merge, release or bump version without explicit authorization
- Last completed task: `VOICE-12 — Preview track Việt` (`af30b463d80388ce6e060cd9c0029bc223a05c5c`)
- Task completed: `VOICE-13 — Mix voice + original audio`
- Exact next task after this one: `VOICE-14 — Preview = Export`

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
- Updated the stale AUDIO contract marker to match the current `internal`
  `BuildPreviewArguments` owner; production audio behavior is unchanged.
- Quoted the Windows startup-smoke sentinel argument so verification works from
  the current workspace path containing spaces; production startup behavior is unchanged.
- Regenerated `docs/migration/CSHARP_CODE_MAP.generated.md` so the checked-in map
  matches the already-landed VOICE-11 LocalTtsService method list.
- No production Editor, Subtitle, TTS, audio graph or media-source behavior was changed.

## VOICE-13 scope and changes

The production graph already has the correct owner and is unchanged:

```text
EditorAudioSettings + EditorVoiceTrack
  -> BuildVoiceAudioFilter
  -> Keep: source + voice
  -> Duck: attenuated source + voice
  -> Mute: voice only
  -> [aout] -> Preview/Export
```

Root cause for VOICE-13 was the absence of a task-specific contract covering all
three source-audio policies, voice timing/gain, and the requirement that Player
monitor mute/volume never enter the render graph. Added only that regression gate.

- Added `csharp/scripts/verify_editor_voice_mix_contract.py`.
- Added the VOICE-13 contract to `csharp/scripts/verify.ps1`.
- No production audio or voice behavior was changed.

## Verification status

Targeted checks:

- `python -m py_compile csharp/scripts/verify_editor_voice_preview_contract.py`: PASS
- `python csharp/scripts/validate_csharp_migration.py`: PASS
- `python csharp/scripts/verify_editor_voice_preview_contract.py`: PASS
- `python csharp/scripts/verify_editor_voice_mix_contract.py`: PASS
- `python csharp/scripts/verify_editor_audio_preview_export_contract.py`: PASS
- `python csharp/scripts/verify_editor_voice_subtitle_invalidation_contract.py`: PASS
- .NET 10.0.400 Core contract tests: `71/71` PASS
- `python csharp/scripts/generate_csharp_code_map.py --check`: PASS after regeneration
- Windows x64 `csharp/scripts/verify.ps1`: PASS
  - SDK `10.0.400`
  - Release x64 WinUI build: 0 warnings, 0 errors
  - Core contracts: `71/71` PASS
  - RangeRegression: PASS
  - self-contained WinUI publish: PASS
  - real startup smoke: PASS
  - PE32+ x64 / worker identity / checksum readback: PASS
  - published `BiliSubStudio.exe` SHA-256: `bbb0e46db7c8fba72e61ff0647797617518e24ae86323c34a05750c324b01f8c`
  - source tree SHA-256: `19b23595d579537a6252c638d38796538ee5da7301fb01f9efc9f838fc8f9757`
  - SDK `10.0.400`
  - Release x64 WinUI build: 0 warnings, 0 errors
  - Core contracts: `71/71` PASS
  - RangeRegression: PASS
  - self-contained WinUI publish: PASS
  - real startup smoke: PASS
  - PE32+ x64 / worker identity / checksum readback: PASS
  - published `BiliSubStudio.exe` SHA-256: `dce1aac6713e959546319612ac4b9beb7bc71795af4546eab445dd8c7980aabd`
  - source tree SHA-256: `73dbccb1c275482dcdb8d00fd5e140d032ae25a51b99d82c24587547a609ac4b`

Previous task commits pushed to GitHub:

- `31c5627726cb1cafafa1d08288b693422d3274c4` — VOICE-12 Preview contract + handoff/code map
- `ae0069570e104acdad3a4f89a43ddfa6f8ad67b9` — stale AUDIO contract marker alignment
- `881e28af2baadd38db703c34c85df05f73548dd2` — Windows startup-smoke path quoting
- `a8b0db2497f85aeefcdef16d6fa0dbb54906091d` — VOICE-13 voice/source mix contract

The gate gives compile/static and startup functional PASS, but it is not a voice
audio field-test. The following remain untested here until the real Editor is
exercised with a source video, completed local TTS, and a valid `voice-master.flac`:

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
