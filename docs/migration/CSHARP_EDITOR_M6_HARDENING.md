# C# Editor M6 hardening

Status: source-fingerprint isolation checkpoint.

## Root cause

Editor project identity is path-based so a replacement video at the same path intentionally resolves to the same project file. Before this checkpoint, `LoadOrCreateAsync` refreshed the source fingerprint but retained regions, subtitle, ASR/Whisper, TTS and voice overrides. That could reuse derived state from a different video.

## Source change gate

`EditorProjectStore.LoadOrCreateAsync` compares the persisted source fingerprint with the live source before normalizing or reusing derived state. A change in normalized path, file size, last-write ticks, source dimensions or duration (> 50 ms drift) is treated as a different source.

On drift:

1. close the project JSON read handle;
2. archive the old project as `project.json.source-changed-*` when possible;
3. create a clean schema-5 project for the live source;
4. preserve only harmless project metadata (`Name` and output `FileName`);
5. reset regions, subtitle, ASR, Whisper timing, TTS, voice overrides and source-audio policy.

The archive is recovery evidence only and is never automatically reused.

## Missing-cache behavior remains selective

A missing/corrupt derived cache with an unchanged source still invalidates only that stage. For example, a missing TTS master track clears TTS state while preserving valid Whisper timing and translation state.

## Regression gate

The Editor project contract now proves both behaviors in one sequence: normal reopen preserves state, missing TTS cache invalidates only TTS, replacing the source at the same path archives/resets all source-derived state, and corrupt JSON is quarantined without blocking a fresh project.
