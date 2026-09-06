using System.Text.Json;
using System.Text.Json.Nodes;
using BiliSubStudio.Core.Timeline;

namespace BiliSubStudio.Core.ContractTests;

// G7 ownership contract: clip-local effect/subtitle/logo items follow their
// clip, while sequence-global logo/music items keep absolute sequence time.
internal static class TimelineItemsContract
{
    public static async Task RunAsync()
    {
        var asset = TimelineMediaAsset.Create(
            "C:/v/a.mp4", 1920, 1080, TimelineTime.FromSeconds(600), 30, hasAudio: true);
        var first = new TimelineClip(TimelineClip.NewId(), asset.Id, 0, 0, 10_000_000, 1.0);
        var second = new TimelineClip(TimelineClip.NewId(), asset.Id, 10_000_000, 20_000_000, 30_000_000, 1.0);
        var clips = new[] { first, second };

        Validation(clips);
        ReorderRidesClip(clips);
        TrimClampsItems(clips);
        SplitPreservesOwnership(clips);
        FractionalRateSplitClampsRounding(asset);
        DeleteAndUndoAreAtomic(clips);
        SubtitleUsesSelectedClip(clips);
        await PersistenceAndCompilerResolveItemsAsync(asset, clips);
    }

    private static TimelineItem Effect(string clipId, long start, long end, string payload) =>
        new(TimelineItem.NewId(), TimelineItemKind.Effect, clipId, start, end, 0, 0, payload);

    private static TimelineItem ClipLogo(string clipId, long start, long end, string payload) =>
        new(TimelineItem.NewId(), TimelineItemKind.Logo, clipId, start, end, 0, 0, payload);

    private static TimelineItem GlobalLogo(long start, long end) =>
        new(TimelineItem.NewId(), TimelineItemKind.Logo, null, 0, 0, start, end, "logo.png");

    private static TimelineItem Music(long start, long end) =>
        new(TimelineItem.NewId(), TimelineItemKind.Music, null, 0, 0, start, end, "music.mp3");

    private static void Validation(IReadOnlyList<TimelineClip> clips)
    {
        var musicWithClip = Music(5_000_000, 8_000_000) with { ClipId = clips[0].Id };
        Throws<InvalidDataException>(musicWithClip.Validate, "music accepted a clip anchor");
        Throws<InvalidDataException>(
            () => new TimelineItem(
                TimelineItem.NewId(), TimelineItemKind.Effect, clips[0].Id, 10, 5, 0, 0, "blur").Validate(),
            "inverted clip-local range accepted");
        var outside = Effect(clips[0].Id, 0, 11_000_000, "outside");
        Throws<InvalidDataException>(
            () => TimelineItemOps.ValidateAll([outside], clips),
            "item beyond the owning clip accepted");
        Throws<InvalidDataException>(
            () => Effect(clips[0].Id, 0, 1, " ").Validate(),
            "whitespace item payload accepted");

        var logo = GlobalLogo(2_000_000, 4_000_000);
        logo.Validate();
        Check(logo.IsSequenceGlobal, "logo without ClipId must be sequence-global");
    }

    private static void ReorderRidesClip(IReadOnlyList<TimelineClip> clips)
    {
        var effect = Effect(clips[0].Id, 1_000_000, 4_000_000, "reorder");
        var song = Music(3_000_000, 18_000_000);
        var logo = GlobalLogo(2_000_000, 6_000_000);
        var before = TimelineEditState.Create(clips, [effect, song, logo]);

        var after = TimelineDependencyOps.Reorder(before, clips[0].Id, 1);
        var ridden = after.Items.Single(item => item.Id == effect.Id);
        Check(ridden.ClipStartUs == 1_000_000 && ridden.ClipEndUs == 4_000_000,
            "reorder rewrote clip-local item time (DEC-009)");
        Check(string.Equals(ridden.ClipId, clips[0].Id, StringComparison.Ordinal),
            "reorder lost the owning clip anchor");
        Check(after.Items.Single(item => item.Id == song.Id).SequenceStartUs == 3_000_000,
            "music moved with a clip (DEC-011)");
        Check(after.Items.Single(item => item.Id == logo.Id).SequenceStartUs == 2_000_000,
            "sequence-global logo moved with a clip");
    }

    private static void TrimClampsItems(IReadOnlyList<TimelineClip> clips)
    {
        var removed = Effect(clips[0].Id, 500_000, 1_500_000, "removed-head");
        var crossingHead = Effect(clips[0].Id, 1_000_000, 3_000_000, "crossing-head");
        var inside = Effect(clips[0].Id, 3_000_000, 5_000_000, "inside");
        var crossingTail = Effect(clips[0].Id, 7_000_000, 9_000_000, "crossing-tail");
        var before = TimelineEditState.Create(clips, [removed, crossingHead, inside, crossingTail]);
        var after = TimelineDependencyOps.Trim(before, clips[0].Id, 2_000_000, 8_000_000);

        Check(after.Items.All(item => item.Id != removed.Id),
            "head trim kept an item entirely in removed content");
        var head = after.Items.Single(item => item.Id == crossingHead.Id);
        Check(head.ClipStartUs == 0 && head.ClipEndUs == 1_000_000,
            "head trim did not rebase and clamp a crossing item");
        var kept = after.Items.Single(item => item.Id == inside.Id);
        Check(kept.ClipStartUs == 1_000_000 && kept.ClipEndUs == 3_000_000,
            "head trim drifted an inside item from its source content");
        var tail = after.Items.Single(item => item.Id == crossingTail.Id);
        Check(tail.ClipStartUs == 5_000_000 && tail.ClipEndUs == 6_000_000,
            "tail trim did not clamp a crossing item");
        var expanded = TimelineDependencyOps.Trim(
            after, clips[0].Id, 1_000_000, 9_000_000);
        var expandedInside = expanded.Items.Single(item => item.Id == inside.Id);
        Check(expandedInside.ClipStartUs == 2_000_000 && expandedInside.ClipEndUs == 4_000_000,
            "outward head trim did not preserve the remaining item's source content");
    }

    private static void SplitPreservesOwnership(IReadOnlyList<TimelineClip> clips)
    {
        var beforeItem = Effect(clips[1].Id, 1_000_000, 4_000_000, "before");
        var afterItem = Effect(clips[1].Id, 7_000_000, 9_000_000, "after");
        var crossing = Effect(clips[1].Id, 3_000_000, 8_000_000, "crossing");
        var crossingLogo = ClipLogo(clips[1].Id, 4_000_000, 7_000_000, "clip-logo");
        var song = Music(0, 20_000_000);
        var globalLogo = GlobalLogo(2_000_000, 12_000_000);
        var before = TimelineEditState.Create(
            clips, [beforeItem, afterItem, crossing, crossingLogo, song, globalLogo]);

        var after = TimelineDependencyOps.Split(
            before, clips[1].Id, clips[1].TimelineStartUs + 5_000_000);
        var left = after.Clips.Single(clip => clip.Id == clips[1].Id);
        var right = after.Clips.Single(clip => !before.Clips.Any(old => old.Id == clip.Id));
        Check(left.DurationUs == 5_000_000 && right.TimelineStartUs == left.TimelineEndUs,
            "split halves overlap, gap, or drift duration");

        var keptLeft = after.Items.Single(item => item.Id == beforeItem.Id);
        Check(keptLeft.ClipId == left.Id && keptLeft.ClipStartUs == 1_000_000 && keptLeft.ClipEndUs == 4_000_000,
            "item before cut did not stay with the left clip");
        var movedRight = after.Items.Single(item => item.Id == afterItem.Id);
        Check(movedRight.ClipId == right.Id && movedRight.ClipStartUs == 2_000_000
            && movedRight.ClipEndUs == 4_000_000,
            "item after cut did not move into right-local time");

        var crossingLeft = after.Items.Single(item => item.Id == crossing.Id);
        var crossingRight = after.Items.Single(item => item.Payload == crossing.Payload && item.Id != crossing.Id);
        Check(crossingLeft.ClipId == left.Id && crossingLeft.ClipEndUs == 5_000_000,
            "crossing effect left half was not clamped");
        Check(crossingRight.ClipId == right.Id && crossingRight.ClipStartUs == 0
            && crossingRight.ClipEndUs == 3_000_000,
            "crossing effect right half was not rebased and clamped");
        Check(after.Items.Count(item => item.Payload == crossingLogo.Payload) == 2,
            "clip-local logo crossing the cut was not split");
        Check(after.Items.Single(item => item.Id == song.Id) == song
            && after.Items.Single(item => item.Id == globalLogo.Id) == globalLogo,
            "sequence-global items changed during split");

        var subtitle = TimelineItemOps.AttachSubtitleToClip(
            clips[1].Id, 4_000_000, 6_000_000, "spoken.srt");
        var subtitleState = TimelineEditState.Create(clips, [subtitle]);
        Throws<InvalidOperationException>(
            () => TimelineDependencyOps.Split(
                subtitleState, clips[1].Id, clips[1].TimelineStartUs + 5_000_000),
            "subtitle crossing a cut was split without an explicit policy");
        var explicitlySplitSubtitle = TimelineDependencyOps.Split(
            subtitleState,
            clips[1].Id,
            clips[1].TimelineStartUs + 5_000_000,
            TimelineSubtitleSplitPolicy.SplitAndClamp);
        Check(explicitlySplitSubtitle.Items.Count == 2,
            "explicit subtitle split policy did not produce two clamped cues");

        var history = new TimelineEditHistory();
        history.Commit(before, after);
        var undone = history.Undo(after);
        Check(undone.Clips.SequenceEqual(before.Clips) && undone.Items.SequenceEqual(before.Items),
            "split undo did not restore clips and dependencies atomically");
        var redone = history.Redo(undone);
        Check(redone.Clips.SequenceEqual(after.Clips) && redone.Items.SequenceEqual(after.Items),
            "split redo did not restore clips and dependencies atomically");
    }

    private static void FractionalRateSplitClampsRounding(TimelineMediaAsset asset)
    {
        var clip = new TimelineClip(TimelineClip.NewId(), asset.Id, 0, 0, 100, 1.5);
        var effect = Effect(clip.Id, 0, clip.DurationUs, "fractional-rate");
        var before = TimelineEditState.Create([clip], [effect]);
        var after = TimelineDependencyOps.Split(before, clip.Id, cutSequenceUs: 3);
        Check(after.Clips.Count == 2 && after.Items.Count == 2,
            "fractional-rate split lost a clip or dependency half");
        foreach (var item in after.Items)
        {
            var owner = after.Clips.Single(candidate => candidate.Id == item.ClipId);
            Check(item.ClipEndUs <= owner.DurationUs,
                "fractional-rate split left an item beyond its rounded child clip");
        }

        var fast = new TimelineClip(TimelineClip.NewId(), asset.Id, 0, 0, 100, 3.0);
        var fastEffect = Effect(fast.Id, 0, 10, "sub-microsecond-trim");
        var trimmed = TimelineDependencyOps.Trim(
            TimelineEditState.Create([fast], [fastEffect]), fast.Id, 1, 100);
        Check(trimmed.Items.Single().ClipStartUs == 0,
            "sub-microsecond-equivalent head trim failed or drifted the item");
    }

    private static void DeleteAndUndoAreAtomic(IReadOnlyList<TimelineClip> clips)
    {
        var effect = Effect(clips[0].Id, 0, 2_000_000, "delete");
        var clipLogo = ClipLogo(clips[0].Id, 1_000_000, 3_000_000, "clip-logo-delete");
        var song = Music(0, 20_000_000);
        var globalLogo = GlobalLogo(2_000_000, 10_000_000);
        var before = TimelineEditState.Create(clips, [effect, clipLogo, song, globalLogo]);
        var after = TimelineDependencyOps.Delete(before, clips[0].Id);

        Check(after.Items.Count == 2 && after.Items.Contains(song) && after.Items.Contains(globalLogo),
            "delete did not remove local dependencies and preserve global items");
        var history = new TimelineEditHistory();
        history.Commit(before, after);
        var restored = history.Undo(after);
        Check(restored.Clips.SequenceEqual(before.Clips) && restored.Items.SequenceEqual(before.Items),
            "delete undo did not restore the complete dependency set");

        var single = TimelineEditState.Create([clips[0]], [song, globalLogo]);
        var emptyVideo = TimelineDependencyOps.Delete(single, clips[0].Id);
        Check(emptyVideo.Clips.Count == 0 && emptyVideo.Items.SequenceEqual(single.Items),
            "deleting the final clip did not preserve sequence-global items");
    }

    private static void SubtitleUsesSelectedClip(IReadOnlyList<TimelineClip> clips)
    {
        var subtitle = TimelineItemOps.AttachSubtitleToClip(
            clips[0].Id, 0, 5_000_000, "C:/subs/vi.srt");
        Check(subtitle.Kind == TimelineItemKind.Subtitle
            && subtitle.ClipId == clips[0].Id
            && subtitle.Payload == "C:/subs/vi.srt",
            "PDEC-004 subtitle attach lost selected-clip ownership");
        TimelineItemOps.ValidateAll([subtitle], clips);
        var sequenceSubtitle = TimelineItemOps.AttachSubtitleToSequence(
            1_000_000, 6_000_000, "C:/subs/global.srt");
        Check(sequenceSubtitle.IsSequenceGlobal && sequenceSubtitle.ClipId is null,
            "sequence-global SRT import cannot be represented");
        TimelineItemOps.ValidateAll([sequenceSubtitle], clips);
    }

    private static async Task PersistenceAndCompilerResolveItemsAsync(
        TimelineMediaAsset asset,
        IReadOnlyList<TimelineClip> clips)
    {
        var root = Path.Combine(Path.GetTempPath(), "bilisub-timeline-g7-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new TimelineProjectStoreV2(root);
            var created = await store.CreateAsync("G7 items", CancellationToken.None);
            var local = Effect(clips[1].Id, 1_000_000, 3_000_000, "compiler-local");
            var global = GlobalLogo(2_000_000, 4_000_000);
            var globalSubtitle = TimelineItemOps.AttachSubtitleToSequence(
                4_000_000, 6_000_000, "C:/subs/sequence.srt");
            var project = created with
            {
                Assets = [asset],
                Sequence = TimelineSequence.Create(clips, [asset], 30),
                Items = [local, global, globalSubtitle],
            };
            await store.SaveAsync(project, CancellationToken.None);
            var reopened = await store.LoadAsync(project.Id, CancellationToken.None);
            Check(reopened.Items.SequenceEqual(project.Items),
                "timeline items did not survive schema-3 persistence");

            var plan = TimelineCompiler.Compile(reopened);
            var resolvedLocal = plan.Items.Single(item => item.ItemId == local.Id);
            Check(resolvedLocal.SequenceStartUs == 11_000_000
                && resolvedLocal.SequenceEndUs == 13_000_000,
                "compiler did not resolve clip-local item into sequence time");
            var resolvedGlobal = plan.Items.Single(item => item.ItemId == global.Id);
            Check(resolvedGlobal.SequenceStartUs == 2_000_000
                && resolvedGlobal.SequenceEndUs == 4_000_000,
                "compiler drifted sequence-global item time");
            var resolvedSubtitle = plan.Items.Single(item => item.ItemId == globalSubtitle.Id);
            Check(resolvedSubtitle.SequenceStartUs == 4_000_000
                && resolvedSubtitle.SequenceEndUs == 6_000_000,
                "sequence-global subtitle did not persist and compile");
            var oneClipWithItem = TimelineCompiler.Compile(reopened with
            {
                Sequence = TimelineSequence.Create([clips[0]], [asset], 30),
                Items = [Effect(clips[0].Id, 1_000_000, 2_000_000, "unsupported-parity")],
            });
            Check(!TimelineOneClipParity.IsOneClip(oneClipWithItem),
                "classic one-clip parity accepted unsupported G7 items");

            Throws<InvalidOperationException>(
                () => TimelineOneClipParity.ToVideoEditRequest(
                    oneClipWithItem, asset, "C:/out", "item.mp4"),
                "classic parity mapping silently dropped G7 items");
            Throws<InvalidOperationException>(
                () => TimelineOneClipParity.ExportRange(oneClipWithItem),
                "classic parity export range accepted G7 items");

            var projectPath = Path.Combine(root, project.Id + ".json");
            var storedJson = await File.ReadAllTextAsync(projectPath);
            var mixedSchema = JsonNode.Parse(storedJson)!.AsObject();
            mixedSchema["schema"] = 2;
            await File.WriteAllTextAsync(projectPath, mixedSchema.ToJsonString());
            await ThrowsAsync<InvalidDataException>(
                () => store.LoadAsync(project.Id, CancellationToken.None),
                "schema-2 project containing G7 items was silently truncated");

            var schema2 = JsonNode.Parse(storedJson)!.AsObject();
            schema2["schema"] = 2;
            schema2.Remove("items");
            await File.WriteAllTextAsync(
                projectPath,
                schema2.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            var upgraded = await store.LoadAsync(project.Id, CancellationToken.None);
            Check(upgraded.Items.Count == 0,
                "persisted schema-2 project did not upgrade with an empty G7 item set");
            await store.SaveAsync(upgraded, CancellationToken.None);
            var schema3 = JsonNode.Parse(await File.ReadAllTextAsync(projectPath))!.AsObject();
            Check(schema3["schema"]!.GetValue<int>() == 3 && schema3["items"] is JsonArray,
                "upgraded schema-2 project did not save back as schema 3 with items");

            var globals = new TimelineItem[] { Music(0, 5_000_000), GlobalLogo(1_000_000, 3_000_000) };
            var withoutVideo = upgraded with
            {
                Sequence = upgraded.Sequence! with { VideoClips = [] },
                Items = globals,
            };
            await store.SaveAsync(withoutVideo, CancellationToken.None);
            var reopenedWithoutVideo = await store.LoadAsync(project.Id, CancellationToken.None);
            Check(reopenedWithoutVideo.Sequence is { VideoClips.Count: 0 }
                && reopenedWithoutVideo.Items.SequenceEqual(globals),
                "project could not persist globals after deleting the final clip");
            var emptyVideoPlan = TimelineCompiler.Compile(reopenedWithoutVideo);
            Check(emptyVideoPlan.Clips.Count == 0 && emptyVideoPlan.Items.Count == globals.Length,
                "compiler dropped globals after deleting the final video clip");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static void Check(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException(message);
    }

    private static void Throws<TException>(Action action, string message) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException(message);
    }

    private static async Task ThrowsAsync<TException>(Func<Task> action, string message)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException(message);
    }
}
