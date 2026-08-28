# ASR checkpoint replacement failures

Baseline: `369ca8c`, clean before this scoped change. No model, CUDA, OCR, TTS rate policy, release or version changes.

## Observed failure

The user's 2026-08-29 05:51:19 log fails inside `LocalAsrService.SaveCheckpointAsync` at `File.Move(temporary, path, overwrite: true)` during a Whisper segment callback. The exact log was found in the `nghi-vietnamese-runtime` workspace. Its remaining ASR checkpoint was about 11 MB, timestamped at the failure, not read-only; both file and parent directory currently grant the user FullControl. No running app was available for inspecting the historical blocking handle. These observations are consistent with transient replacement contention, not proof of the identity of a locking process or the permissions at the instant of failure.

The former code closed/flushed the temporary writer correctly but tried publication only once, then deleted the completed temporary snapshot in `finally` even when replacement failed. A transient replacement denial therefore aborted the ASR pipeline and discarded the newly serialized progress. The old log omitted the checkpoint path and native error code.

## Changed behavior

- ASR-only `AsrCheckpointFile` writes unique temporary JSON beside the target using CreateNew/WriteThrough, flushes it to disk and closes the stream before publication.
- First publication uses non-overwriting `File.Move`; replacement uses `File.Replace` with the previous committed file in `<checkpoint>.bak`, without ignoring metadata/ACL merge errors. No delete-then-move gap, in-place overwrite or permission/attribute changes are introduced. See [Microsoft File.Replace](https://learn.microsoft.com/en-us/dotnet/api/system.io.file.replace?view=net-10.0).
- Retryable access/sharing/lock errors, replacement-removal error 1175 and destination existence races get at most six attempts, with delays of 100, 200, 400, 800 and 1000 ms (2.5 seconds of delays, excluding filesystem time). Known read-only/directory targets or inaccessible attributes stop retries. Disk-full and unrelated I/O failures are not misclassified as locks. Native replacement errors 1176/1177 stop with recovery paths rather than blindly retrying a potentially altered file state; see [ReplaceFileW failure semantics](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-replacefilew).
- The first retry emits a warning with the target path and HRESULT. Exhaustion reports the path, target/backup attributes, original exception/HRESULT and the path of the complete retained snapshot, if still present. No automatic Administrator elevation or security-software changes occur.
- Failed/cancelled publication retains a fully flushed `.tmp-<guid>` snapshot for explicit recovery; incomplete serialization alone is cleaned. Complete snapshots are never silently accepted as progress. If a file operation has consumed the temporary file before failing, the diagnostic does not pretend it was retained; the committed target/backup remain the recovery candidates.
- Primary checkpoint is preferred. Missing/unreadable/invalid primary can fall back to `.bak` with the same schema, exact source/model key and single-device/hybrid validation. Null cue/word entries are rejected. Reads allow delete sharing to avoid blocking replacement by their own handles. Unpublished `.tmp` files are not auto-loaded.
- Both normal and hybrid segment/chunk saves use this writer and keep `CancellationToken.None` for already accepted segment durability. Other cancellation remains interruptible, including retry delays. User progress still says a checkpoint was saved only after publication succeeds.

The existing schema is unchanged: this changes publication/recovery, not the content or meaning of saved timing. Backup may be one successful save behind. A persistent ACL, read-only, protected-folder or long-held lock still requires resolving that external condition; this source change cannot grant missing permissions. No real project/checkpoint files or permissions were altered while implementing the fix.

## Verification boundary

**Build, automated tests, Windows lock tests, inference, UI and field testing: NOT RUN**, per user request. Only read-only inspection, source review, code-map generation and whitespace checks were performed. Installed/runtime app payloads remain unchanged. This is not a claim that the reported field failure has passed a new-build reproduction.

Regression definitions cover first write/replace/backup, real delete-sharing and restrictive Windows handles, release-and-retry, persistent denial retaining old plus new snapshots, cancellation before writing/during retry, read-only attributes remaining unchanged, error path/HRESULT, primary preference, identity-gated backup recovery and refusal to auto-load unpublished snapshots. No model download, mock inference or synthetic speech is involved.
