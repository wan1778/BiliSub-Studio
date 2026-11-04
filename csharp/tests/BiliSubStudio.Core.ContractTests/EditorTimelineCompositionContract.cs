using System.Text.Json;
using BiliSubStudio.Core.Editor;

namespace BiliSubStudio.Core.ContractTests;

// A timeline composing several videos: an old project must generate clips from the source
// videos itself, timeline time must map correctly back to time inside the source file, and
// splitting segments must not lose or overlap duration.
internal static class EditorTimelineCompositionContract
{
    public static Task RunAsync()
    {
        var source = new EditorSourceFingerprint("E:\\video\\a.mp4", 10, 0, 1920, 1080, 120);
        var fallback = EditorTimelineComposition.Resolve(null, source);
        Require(fallback.Count == 1, "a project without timeline must resolve to one clip");
        Require(fallback[0].SourcePath == source.Path, "the fallback clip must use the project source");
        Require(Close(fallback[0].Duration, 120), "the fallback clip must cover the whole source");

        var clips = new List<EditorTimelineClip>
        {
            new("a", "E:\\video\\a.mp4", 0, 30, "A"),
            new("b", "E:\\video\\b.mp4", 5, 20, "B"),
        };
        var resolved = EditorTimelineComposition.Resolve(new EditorTimelineProject(clips), source);
        Require(resolved.Count == 2, "an explicit timeline must win over the fallback clip");
        Require(Close(EditorTimelineComposition.TotalDuration(resolved), 50), "composition duration sums clip durations");

        var starts = EditorTimelineComposition.Starts(resolved);
        Require(Close(starts[0], 0) && Close(starts[1], 30), "clip starts must follow the composition order");

        var (clip, sourceTime, timelineStart) = EditorTimelineComposition.Locate(resolved, 35);
        Require(clip.Id == "b", "a time inside the second clip must resolve to that clip");
        Require(Close(sourceTime, 10), "the source time must be clip start + offset inside the clip");
        Require(Close(timelineStart, 30), "the timeline start must be the sum of the previous clips");

        var (lastClip, lastSourceTime, _) = EditorTimelineComposition.Locate(resolved, 49.9);
        Require(lastClip.Id == "b", "a time near the end must stay in the last clip");
        Require(Close(lastSourceTime, 24.9), "the source time near the end must be exact");

        var (beforeClip, beforeSourceTime, _) = EditorTimelineComposition.Locate(resolved, 0);
        Require(beforeClip.Id == "a" && Close(beforeSourceTime, 0), "the first clip must own the timeline start");

        var split = EditorTimelineComposition.SplitAtSourceTime(resolved, 0, 12);
        Require(split.Count == 3, "splitting a clip must produce two clips");
        Require(Close(split[0].Duration, 12) && Close(split[1].Duration, 18), "split durations must add up to the original");
        Require(Close(split[1].SourceIn, 12), "the second half must start where the cut happened");
        Require(split[0].Id != split[1].Id, "split clips must have distinct ids");
        Require(Close(EditorTimelineComposition.TotalDuration(split), 50), "splitting must not change the composition duration");

        Require(EditorTimelineComposition.SplitAtSourceTime(resolved, 0, 0).Count == 2,
            "splitting at the clip start must be rejected");
        Require(EditorTimelineComposition.SplitAtSourceTime(resolved, 0, 30).Count == 2,
            "splitting at the clip end must be rejected");

        // The out point is always derived from the in point + duration (like FilmCraft's source_in).
        Require(Close(resolved[1].End, 25), "the clip out point must be derived from in point plus duration");
        Require(Close(resolved[1].End, resolved[1].SourceIn + resolved[1].Duration),
            "the derived out point must follow the in point and duration");

        // The old JSON shape (start/end) is still readable, the new one stores source_in/duration.
        var legacyJson = "{\"id\":\"b\",\"source_path\":\"E:\\\\video\\\\b.mp4\",\"start\":5,\"end\":25,\"label\":\"B\"}";
        var legacy = JsonSerializer.Deserialize<EditorTimelineClip>(legacyJson, LegacyTimelineJsonOptions());
        Require(legacy is not null && Close(legacy.SourceIn, 5) && Close(legacy.Duration, 20),
            "a legacy clip (start/end) must load as source in plus duration");
        var modernJson = JsonSerializer.Serialize<EditorTimelineClip>(
            new EditorTimelineClip("b", "E:\\video\\b.mp4", 5, 20, "B"), LegacyTimelineJsonOptions());
        Require(modernJson.Contains("\"source_in\"", StringComparison.Ordinal)
            && modernJson.Contains("\"duration\"", StringComparison.Ordinal),
            "a saved clip must use the source_in/duration shape");
        Require(!modernJson.Contains("\"start\"", StringComparison.Ordinal),
            "a saved clip must not keep the legacy start shape");

        var removed = EditorTimelineComposition.RemoveAt(split, 1);
        Require(removed.Count == 2, "removing a clip must drop exactly one clip");
        Require(Close(EditorTimelineComposition.TotalDuration(removed), 32), "removing a clip must shorten the composition");
        Require(EditorTimelineComposition.RemoveAt(split, 99).Count == 3, "removing an invalid clip must be a no-op");

        // Effect regions stay anchored to the primary source but may continue
        // into the following clips; the limit must follow the composed strip.
        var extended = new EditorTimelineProject(new List<EditorTimelineClip>
        {
            new(EditorTimelineComposition.PrimaryClipId, source.Path, 0, 30, "A"),
            new("b", "E:\\video\\b.mp4", 5, 20, "B"),
        });
        Require(Close(EditorTimelineComposition.RegionTimeLimit(extended, source.Path, 30), 50),
            "an effect region must be allowed to continue into the following clip");
        var trimmedPrimary = new EditorTimelineProject(new List<EditorTimelineClip>
        {
            new(EditorTimelineComposition.PrimaryClipId, source.Path, 5, 10, "A"),
            new("b", "E:\\video\\b.mp4", 5, 20, "B"),
        });
        Require(Close(EditorTimelineComposition.RegionTimeLimit(trimmedPrimary, source.Path, 30), 35),
            "the region limit must follow the primary clip's in point on the composed strip");
        var withoutPrimary = new EditorTimelineProject(new List<EditorTimelineClip>
        {
            new("b", "E:\\video\\b.mp4", 5, 20, "B"),
        });
        Require(Close(EditorTimelineComposition.RegionTimeLimit(withoutPrimary, source.Path, 30), 30),
            "without the primary clip the region limit must stay at the source duration");

        return Task.CompletedTask;
    }

    private static bool Close(double left, double right) => Math.Abs(left - right) < .0005;

    private static JsonSerializerOptions LegacyTimelineJsonOptions() => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        Converters = { new EditorTimelineClipJsonConverter() },
    };

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
