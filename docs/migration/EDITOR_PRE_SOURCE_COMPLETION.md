# Editor pre-SOURCE completion contract

This checkpoint must be complete before SOURCE / FILE PICKER work begins.

## Required behavior

- The Subtitle details panel exposes a static-XAML cue list and per-cue Chinese/source + Vietnamese editors.
- Selecting a cue seeks the Player to that cue's source time.
- A cue can be locked so full Vietsub does not overwrite its Vietnamese text.
- Saving a manual cue invalidates the previous translated SRT output and TTS state.
- Unsaved cue text blocks Render, TTS, karaoke ASS and opening a previous Vietnamese SRT.
- Per-cue retranslation is a fresh local-AI request; the prior checkpoint for that cue is removed before translation starts.
- Full Vietsub can resume through its content-addressed checkpoint and restores locked cues before writing the Vietnamese SRT.
- Manual cue state persists under the project data root and reapplies without changing cue order or timecodes.
- Cue state synchronization is explicit when a subtitle source is loaded/restored/generated; it must not use LayoutUpdated.
- Event handlers only call awaitable task methods; no handler calls another handler and no runtime handler replacement is permitted.

## Automated gates before merge

- C# static migration contract.
- Generated C# code map current.
- Core contract for manual cue persistence and unchanged timeline.
- Windows WinUI compile/startup/layout smoke.
- Package and exact source identity.

## Manual field checks after a test build exists

1. Import a Chinese SRT and select several cues; Player seeks to each cue.
2. Edit Chinese and Vietnamese text, save, reopen the project, and confirm the edit persists.
3. Lock one cue, run full Vietsub, and confirm the locked Vietnamese text is unchanged.
4. Retranslate one unlocked cue twice and confirm both runs execute fresh rather than immediately restoring the prior translation checkpoint.
5. After editing a cue, confirm old SRT/TTS/export actions cannot use stale subtitle output.
6. Save Vietnamese SRT and verify numbering/timecodes are unchanged from the source SRT.
