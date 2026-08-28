using System.Reflection;
using BiliSubStudio.Core.Configuration;
using BiliSubStudio.Core.Editor;

namespace BiliSubStudio.Core.ContractTests;

internal static class EditorVietnameseSrtContract
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "bilisub-vietnamese-srt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "external.vi.srt");
            const string content = "1\n00:00:00,000 --> 00:00:03,000\nXin chào, đây là tiếng Việt.\n\n2\n00:00:04,000 --> 00:00:08,000\nĐạo hữu hãy bình tĩnh.\nChúng ta vẫn còn cơ hội.\n";
            await File.WriteAllTextAsync(path, content);
            var originalBytes = await File.ReadAllBytesAsync(path);
            var raw = await EditorSubtitleDocument.LoadAsync(path, CancellationToken.None);
            var source = EditorSubtitleDocument.UseVietnameseSrt(raw);
            if (source.Path != raw.Path || source.Sha256 != raw.Sha256 || source.Cues.Count != 2)
                throw new InvalidOperationException("Vietnamese import changed source identity");
            EditorSubtitleDocument.ValidateUnchangedTimeline(raw.Cues, source.Cues);
            for (var i = 0; i < source.Cues.Count; i++)
            {
                var cue = source.Cues[i];
                if (cue.Id != raw.Cues[i].Id || cue.SourceText != raw.Cues[i].SourceText || cue.VietnameseText != cue.SourceText)
                    throw new InvalidOperationException("Vietnamese import must fill spoken text without translation");
                var build = typeof(EditorTtsRequest).Assembly.GetType("BiliSubStudio.Core.Editor.LocalTtsService")!
                    .GetMethod("BuildWholeCue", BindingFlags.NonPublic | BindingFlags.Static)!;
                var timing = new EditorCueSpeechTiming(cue.Id, cue.Start, cue.End, cue.Start, cue.End, 0, 0,
                    [new EditorWordTiming("offline timing fixture", cue.Start, cue.End, 1)], [], "uncertain", 0, 0);
                var whole = build.Invoke(null, [cue, "ngoc_huyen", timing])!;
                var type = whole.GetType();
                if ((double)type.GetProperty("CueStart")!.GetValue(whole)! != cue.Start
                    || (double)type.GetProperty("CueEnd")!.GetValue(whole)! != cue.End
                    || string.IsNullOrWhiteSpace((string)type.GetProperty("Text")!.GetValue(whole)!))
                    throw new InvalidOperationException("Vietnamese SRT did not reach whole-cue TTS unchanged");
                if (EditorVietnameseSubtitleWorkflow.HasDraftChange(cue, cue.VietnameseText.Replace("\n", "\r\n")))
                    throw new InvalidOperationException("Loading text/CRLF must not mark a cue dirty");
            }
            if (EditorVietnameseSubtitleWorkflow.VoiceBlockReason(true, false, false, false, source) is not null)
                throw new InvalidOperationException("Video plus external Vietnamese SRT must enable voice without AI translation");
            foreach (var reason in new[]
            {
                EditorVietnameseSubtitleWorkflow.VoiceBlockReason(false, false, false, false, source),
                EditorVietnameseSubtitleWorkflow.VoiceBlockReason(true, true, false, false, source),
                EditorVietnameseSubtitleWorkflow.VoiceBlockReason(true, false, true, false, source),
                EditorVietnameseSubtitleWorkflow.VoiceBlockReason(true, false, false, true, source),
                EditorVietnameseSubtitleWorkflow.VoiceBlockReason(true, false, false, false, null),
                EditorVietnameseSubtitleWorkflow.VoiceBlockReason(true, false, false, false, raw),
            })
                if (string.IsNullOrWhiteSpace(reason)) throw new InvalidOperationException("Blocked voice needs an explicit reason");

            const string edit = "Xin chào, lời Việt đã được chỉnh.";
            if (!EditorVietnameseSubtitleWorkflow.HasDraftChange(source.Cues[0], edit))
                throw new InvalidOperationException("A real edit must be detected");
            var paths = AppPaths.FromRoot(root);
            paths.EnsureBootstrapDirectories();
            var manual = new EditorSubtitleManualStore(paths);
            await manual.SaveAsync(source.Sha256, new Dictionary<string, EditorManualCueState>
            {
                [source.Cues[0].Id] = new(null, edit, false),
            }, CancellationToken.None);
            var reopened = EditorSubtitleDocument.UseVietnameseSrt(await EditorSubtitleDocument.LoadAsync(path, CancellationToken.None));
            reopened = EditorSubtitleManualStore.Apply(reopened, await manual.LoadAsync(source.Sha256, CancellationToken.None));
            EditorSubtitleDocument.ValidateUnchangedTimeline(source.Cues, reopened.Cues);
            if (reopened.Cues[0].VietnameseText != edit || reopened.Cues[1] != source.Cues[1])
                throw new InvalidOperationException("Saved Vietnamese edits did not survive reopen");
            if (EditorVietnameseSubtitleWorkflow.HasDraftChange(reopened.Cues[0], edit)
                || EditorVietnameseSubtitleWorkflow.VoiceBlockReason(true, false, false, false, reopened) is not null)
                throw new InvalidOperationException("Saved edits must unlock voice again");
            if (!EditorSubtitleDocument.RenderVietnamese(reopened.Cues).Contains(edit, StringComparison.Ordinal))
                throw new InvalidOperationException("SRT export lost the edited Vietnamese text");
            var finalBytes = await File.ReadAllBytesAsync(path);
            if (!originalBytes.SequenceEqual(finalBytes))
                throw new InvalidOperationException("Import/edit/reopen overwrote the user's input SRT");
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
