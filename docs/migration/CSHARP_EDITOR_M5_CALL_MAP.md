# C# Editor M5 call map

Status: production checkpoint for final-render validation and atomic promotion.

## Final export

```text
EditorPage
  -> BiliSubApplication.StartEditor
  -> VideoEditorService.RunAsync
       -> BuildFilter / BuildAss
       -> BuildVoiceAudioFilter or BuildAudioArguments
       -> FFmpeg renders to sibling .rendering file
       -> ffprobe validates video/audio streams, duration and container size
       -> FFmpeg bounded decode validates video head, optional audio and video tail
       -> File.Move promotes the verified sibling file atomically
  -> AppJob.Finish only after validation and promotion
```

## Audio acceptance

- Keep/Duck require an audio stream in the rendered result.
- Mute without a Vietnamese voice track requires no audio stream.
- Mute with a Vietnamese voice track requires the TTS audio stream.
- Preview/export continue to use the same source-audio and TTS mixing semantics; M5 only adds final-output validation.

## Failure and cancellation

- Source media is never overwritten.
- Failed validation never promotes the `.rendering` file.
- The existing `finally` cleanup removes partial `.rendering` and temporary ASS artifacts.
- Cancellation remains cleanup-aware through the Editor job and ProcessRunner-owned FFmpeg processes.

## Release gate

A successful FFmpeg exit code or non-empty file is not sufficient. The Editor reports completion only after stream/duration/decode validation and atomic promotion.
