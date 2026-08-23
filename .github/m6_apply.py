from pathlib import Path


def replace_once(text: str, old: str, new: str, label: str) -> str:
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{label}: expected one anchor, found {count}")
    return text.replace(old, new, 1)


store = Path("csharp/src/BiliSubStudio.Core/Editor/EditorProjectStore.cs")
text = store.read_text(encoding="utf-8")
old = '''        if (File.Exists(projectPath))
        {
            try
            {
                var info = new FileInfo(projectPath);
                if (info.Length <= 0 || info.Length > MaxProjectBytes)
                    throw new InvalidDataException("Project Editor có kích thước không hợp lệ.");
                await using var stream = new FileStream(projectPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                var loaded = await JsonSerializer.DeserializeAsync<EditorProject>(stream, _json, cancellationToken)
                    ?? throw new InvalidDataException("Project Editor rỗng.");
                if (loaded.Schema is not (1 or 2 or 3 or 4 or CurrentSchema) || !string.Equals(loaded.Id, id, StringComparison.Ordinal))
                    throw new InvalidDataException("Project Editor không đúng phiên bản hoặc nguồn.");
                var regions = NormalizeRegions(loaded.Regions);
                return loaded with
                {
                    Schema = CurrentSchema,
                    Source = source,
                    Regions = regions,
                    Subtitle = NormalizeSubtitle(loaded.Subtitle),
                    Audio = NormalizeAudio(loaded.Audio),
                    Asr = NormalizeAsr(loaded.Asr),
                    Speech = NormalizeSpeech(loaded.Speech),
                    Tts = NormalizeTts(loaded.Tts),
                    VoiceOverrides = NormalizeVoiceOverrides(loaded.VoiceOverrides),
                };
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                Quarantine(projectPath);
            }
        }

        return new EditorProject(
            CurrentSchema,
            id,
            Path.GetFileNameWithoutExtension(source.Path),
            source,
            Path.GetFileNameWithoutExtension(source.Path) + "_edited.mp4",
            [],
            null,
            DateTimeOffset.UtcNow,
            EditorAudioSettings.Default);
'''
new = '''        if (File.Exists(projectPath))
        {
            try
            {
                var info = new FileInfo(projectPath);
                if (info.Length <= 0 || info.Length > MaxProjectBytes)
                    throw new InvalidDataException("Project Editor có kích thước không hợp lệ.");
                EditorProject loaded;
                await using (var stream = new FileStream(projectPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    loaded = await JsonSerializer.DeserializeAsync<EditorProject>(stream, _json, cancellationToken)
                        ?? throw new InvalidDataException("Project Editor rỗng.");
                }
                if (loaded.Schema is not (1 or 2 or 3 or 4 or CurrentSchema) || !string.Equals(loaded.Id, id, StringComparison.Ordinal))
                    throw new InvalidDataException("Project Editor không đúng phiên bản hoặc nguồn.");
                if (SourceFingerprintChanged(loaded.Source, source))
                {
                    ArchiveSourceChanged(projectPath);
                    return CreateFreshProject(source, id, loaded.Name, loaded.FileName);
                }
                var regions = NormalizeRegions(loaded.Regions);
                return loaded with
                {
                    Schema = CurrentSchema,
                    Source = source,
                    Regions = regions,
                    Subtitle = NormalizeSubtitle(loaded.Subtitle),
                    Audio = NormalizeAudio(loaded.Audio),
                    Asr = NormalizeAsr(loaded.Asr),
                    Speech = NormalizeSpeech(loaded.Speech),
                    Tts = NormalizeTts(loaded.Tts),
                    VoiceOverrides = NormalizeVoiceOverrides(loaded.VoiceOverrides),
                };
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                Quarantine(projectPath);
            }
        }

        return CreateFreshProject(source, id);
'''
text = replace_once(text, old, new, "LoadOrCreate source drift")

marker = '''    public async Task SaveAsync(EditorProject project, CancellationToken cancellationToken)
'''
if "private static bool SourceFingerprintChanged(" in text:
    raise RuntimeError("source fingerprint helper already exists")
helpers = '''    private static EditorProject CreateFreshProject(
        EditorSourceFingerprint source,
        string id,
        string? name = null,
        string? fileName = null)
    {
        var defaultName = Path.GetFileNameWithoutExtension(source.Path);
        return new EditorProject(
            CurrentSchema,
            id,
            string.IsNullOrWhiteSpace(name) ? defaultName : name.Trim(),
            source,
            string.IsNullOrWhiteSpace(fileName) ? defaultName + "_edited.mp4" : fileName.Trim(),
            [],
            null,
            DateTimeOffset.UtcNow,
            EditorAudioSettings.Default);
    }

    private static bool SourceFingerprintChanged(EditorSourceFingerprint? previous, EditorSourceFingerprint current)
    {
        if (previous is null || previous.Size <= 0 || previous.LastWriteUtcTicks <= 0
            || previous.Width <= 0 || previous.Height <= 0 || !double.IsFinite(previous.Duration) || previous.Duration < 0)
            return true;
        string previousPath;
        try { previousPath = Path.GetFullPath(previous.Path.Trim()); }
        catch { return true; }
        return !string.Equals(previousPath, current.Path, StringComparison.OrdinalIgnoreCase)
            || previous.Size != current.Size
            || previous.LastWriteUtcTicks != current.LastWriteUtcTicks
            || previous.Width != current.Width
            || previous.Height != current.Height
            || Math.Abs(previous.Duration - current.Duration) > .05;
    }

    private static void ArchiveSourceChanged(string path)
    {
        var archive = path + ".source-changed-" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        try
        {
            File.Move(path, archive, overwrite: false);
            return;
        }
        catch { }
        try { File.Copy(path, archive, overwrite: false); }
        catch { }
    }

'''
text = replace_once(text, marker, helpers + marker, "source drift helpers")
store.write_text(text, encoding="utf-8", newline="\n")


tests = Path("csharp/tests/BiliSubStudio.Core.ContractTests/Program.cs")
text = tests.read_text(encoding="utf-8")
text = replace_once(
    text,
    '("editor project persists and quarantines corrupt state", EditorProjectContractAsync),',
    '("editor project persists, isolates source drift and quarantines corrupt state", EditorProjectContractAsync),',
    "project contract name")
old = '''            File.Delete(voicePath);
            var selectivelyRecovered = await store.LoadOrCreateAsync(video, 1920, 1080, 120, CancellationToken.None);
            True(selectivelyRecovered.Tts is null, "missing TTS cache should invalidate only TTS state");
            Equal("complete", selectivelyRecovered.Speech!.Status);
            Equal("episode-edited.mp4", selectivelyRecovered.FileName);

            var projectPath = store.GetProjectPath(video);
            await File.WriteAllTextAsync(projectPath, "{broken-json");
'''
new = '''            File.Delete(voicePath);
            var selectivelyRecovered = await store.LoadOrCreateAsync(video, 1920, 1080, 120, CancellationToken.None);
            True(selectivelyRecovered.Tts is null, "missing TTS cache should invalidate only TTS state");
            Equal("complete", selectivelyRecovered.Speech!.Status);
            Equal("episode-edited.mp4", selectivelyRecovered.FileName);

            await File.WriteAllBytesAsync(voicePath, Enumerable.Repeat((byte)1, 128).ToArray());
            await store.SaveAsync(reopened, CancellationToken.None);
            var projectPath = store.GetProjectPath(video);
            await File.AppendAllTextAsync(video, "changed-source");
            var sourceChanged = await store.LoadOrCreateAsync(video, 1920, 1080, 120, CancellationToken.None);
            Equal("episode-edited.mp4", sourceChanged.FileName);
            Equal(0, sourceChanged.Regions.Count);
            True(sourceChanged.Subtitle is null, "changed source reused old subtitle state");
            True(sourceChanged.Asr is null, "changed source reused old ASR state");
            True(sourceChanged.Speech is null, "changed source reused old Whisper timing");
            True(sourceChanged.Tts is null, "changed source reused old TTS state");
            Equal(0, sourceChanged.VoiceOverrides?.Count ?? 0);
            Equal("keep", sourceChanged.Audio!.SourceMode);
            True(Directory.GetFiles(Path.GetDirectoryName(projectPath)!, Path.GetFileName(projectPath) + ".source-changed-*").Length == 1,
                "source-changed Editor project was not archived");

            await File.WriteAllTextAsync(projectPath, "{broken-json");
'''
text = replace_once(text, old, new, "source drift project contract")
tests.write_text(text, encoding="utf-8", newline="\n")


props = Path("csharp/Directory.Build.props")
text = props.read_text(encoding="utf-8")
text = replace_once(text, "4.0.0-beta.34-csharp-p5", "4.0.0-beta.35-csharp-p5", "technical version")
props.write_text(text, encoding="utf-8", newline="\n")


doc = Path("docs/migration/CSHARP_EDITOR_M6_HARDENING.md")
if doc.exists():
    raise RuntimeError("M6 hardening doc already exists")
doc.write_text("""# C# Editor M6 hardening

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
""", encoding="utf-8", newline="\n")

print("M6_PATCH_OK")
