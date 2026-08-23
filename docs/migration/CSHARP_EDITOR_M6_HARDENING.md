# C# Editor M6 hardening

Status: source-fingerprint isolation and export disk-space safety checkpoints.

## Source-fingerprint root cause

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

## Export disk-space root cause

Final Editor export previously created the sibling `.rendering` file and let FFmpeg continue until the filesystem itself rejected writes. On a long video this could waste most of the render before failing even though cleanup eventually removed the partial file.

## Export disk-space gate

`VideoEditorService.RunAsync` now applies two safety layers when the output volume exposes reliable Windows `DriveInfo` information:

1. before FFmpeg starts, free space must be at least `2 × source file size + 512 MB`;
2. during render, the output volume is checked approximately every 3 seconds;
3. if live free space drops below 512 MB, the FFmpeg progress callback throws and stops the job before the volume is exhausted.

The constants are pinned by `EditorDiskSpacePolicyContract`: 512 MB reserve, source multiplier 2, and 3000 ms live-check interval.

If the output path is on a volume for which `DriveInfo` cannot provide a reliable value (for example some network or unusual mounted paths), BiliSub Studio does not invent a capacity estimate. The guard is skipped and FFmpeg retains its normal filesystem error handling.

A disk-guard abort follows the existing ownership chain: callback failure causes `ProcessRunner` to reap the owned FFmpeg process tree, then `VideoEditorService.RunAsync` removes the sibling `.rendering` file in `finally`. Source media is never overwritten. The M5 validation/promotion gate still prevents a partial or unverified file from becoming the final output.

## Regression gate

The Editor project contract proves normal reopen, selective missing-TTS invalidation, same-path source replacement isolation/archive/reset, and corrupt JSON quarantine. The disk-space policy contract separately pins the render reserve, preflight multiplier and live-check interval without changing the established 50-test Core suite count.
