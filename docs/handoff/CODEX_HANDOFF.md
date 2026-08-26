# Codex handoff — BiliSub Studio

- Current main/base SHA: `d4e45ef` (public release `4.0.47`).
- Current branch: `main`.
- PR: none.
- Last completed task: `VOICE-NGOCHUYEN-01` (commit/release pending at this handoff update).
- Task in progress: none after the final Windows CI/release gate; do not start another task until that result is known.
- Exact next task: field-test this Voice flow with a real video and complete Vietsub: `Phân tích word timing` → `Tạo voice Ngọc Huyền local` → Preview → Export.

## Root cause

The Editor was still a two-route Piper/VAIS implementation: Whisper measured a
male/female-like classifier, the UI exposed per-cue gender override, and the TTS
worker chose a synthetic profile. It could not provide the requested named Ngọc
Huyền voice. Runtime testing also exposed two portable Windows failures: a temp
clip created on C: could not atomically move into an E: cache, and the temporary
FLAC filename hid its extension from FFmpeg.

## Changes made

- Replaced Piper voice synthesis with pinned local Kokoro Vietnamese ONNX and the
  verified `ngoc_huyen` voicepack. Network access is only for first installation;
  worker inference remains offline.
- Kept the existing Whisper word-timing/pause grouping and bounded FFmpeg timing
  fit. The generated master track still follows exact timeline slots for Preview
  and Export.
- Removed the visible and executable Nam/Nữ selector, its handler and the ASR
  pitch/gender computation. Whisper now supplies only word timing and pauses.
- Invalidates old Piper TTS on project reopen. Compatibility metadata retains two
  persisted fields only so old project JSON can load safely; both new values are
  always `ngoc-huyen` and are never used for routing.
- Made the worker create temp clips inside the output cache volume and preserve
  `.flac` on its temporary output filename.

## Files changed

- `internal/tts/worker.py`
- `internal/asr/worker.py`
- `csharp/src/BiliSubStudio.Core/Editor/LocalTtsInstaller.cs`
- `csharp/src/BiliSubStudio.Core/Editor/LocalTtsService.cs`
- `csharp/src/BiliSubStudio.Core/Editor/EditorProjectStore.cs`
- `csharp/src/BiliSubStudio.App/Pages/EditorPage.xaml`
- `csharp/src/BiliSubStudio.App/Pages/EditorPage.xaml.cs`
- `csharp/src/BiliSubStudio.App/Pages/EditorPage.SubtitleCueEditing.cs`
- `csharp/scripts/validate_csharp_migration.py`
- `csharp/scripts/verify_editor_voice_reopen_contract.py`
- `csharp/tests/BiliSubStudio.Core.ContractTests/EditorLicensedVoiceProfileContract.cs`

## Tests and status

- `py -3.10 -m py_compile internal/asr/worker.py internal/tts/worker.py`: PASS.
- `dotnet build csharp/src/BiliSubStudio.App/BiliSubStudio.App.csproj -c Release --no-restore`: PASS, 0 warnings / 0 errors.
- Static migration, voice Preview/Export/reopen contracts: PASS.
- Core contract tests: PASS, 71/71.
- Runtime PASS on this Windows machine under
  `E:\New folder\testrc\Temp\bilisub-ngoc-huyen-runtime-test`:
  verified model hashes, local ONNX/torch/vig2p imports, generated Ngọc Huyền
  `voice-master.flac`; ffprobe duration exactly `3.000000` seconds.
- Not yet functional PASS: the full WinUI app flow with the supplied media at
  `C:\Users\Man PC\Downloads\test`, user listening quality, full-video preview
  and actual Export. Windows CI/release also remain pending.

## Constraints to preserve

- Windows desktop app: C# + .NET 10 + WinUI 3; no web/demo replacement.
- Source media is never overwritten. Translation, ASR and TTS are local only.
- One event and one state/control owner; no event handler calls another handler.
- Fix the actual owner, not a Repair/Fix/Parity layer.
- One small task at a time. No version bump outside the release workflow.
- Commit/push each completed task; release only after the relevant Windows gate passes.
