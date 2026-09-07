using BiliSubStudio.Core.Timeline;

namespace BiliSubStudio.Core.ContractTests;

internal static class TimelineResilienceContract
{
    public static async Task RunAsync()
    {
        await MissingAssetDetectionAndRelinkAsync();
        await RelinkRejectsWrongFileAsync();
        await RelinkRejectedWhileDirtyAsync();
        await AbandonedTemporariesAreReapedAsync();
    }

    private static async Task MissingAssetDetectionAndRelinkAsync()
    {
        var root = TemporaryRoot();
        try
        {
            // A real file on disk first: detection must report nothing missing.
            var realPath = Path.Combine(root, "real-source.mp4");
            await File.WriteAllTextAsync(realPath, "placeholder media");
            var asset = TimelineMediaAsset.Create(
                realPath, 1920, 1080, 60_000_000, 30, hasAudio: true);
            var clip = TimelineClip.Create(asset.Id, 0, 0, 10_000_000);
            var project = new TimelineProjectV2(
                TimelineProjectV2.NewId(),
                "G19 relink",
                DateTimeOffset.UtcNow,
                [asset],
                new TimelineSequence([clip], 1920, 1080, 30),
                [],
                [],
                [],
                TimelineSourceAudioState.Defaults([clip]),
                [],
                null);
            var store = new TimelineProjectStoreV2(root);
            await store.SaveAsync(project, CancellationToken.None);
            var controller = new TimelineEditingController(store);
            await controller.OpenAsync(project.Id, CancellationToken.None);

            Check(controller.FindMissingMediaAssets().Count == 0,
                "existing asset path was reported missing");

            // The stored path disappears: detection must report it.
            File.Delete(realPath);
            Check(controller.FindMissingMediaAssets().Count == 1,
                "missing asset was not detected");
            var missing = controller.FindMissingMediaAssets()[0];

            // Relink to a new real path: identity must survive, fingerprint swaps.
            var movedPath = Path.Combine(root, "moved.mp4");
            await File.WriteAllTextAsync(movedPath, "placeholder media");
            var relinked = TimelineMediaRelink.RelinkMediaAsset(
                project,
                missing.Id,
                movedPath,
                asset.Width,
                asset.Height,
                asset.DurationUs / (double)TimelineTime.MicrosecondsPerSecond,
                asset.HasAudio,
                new FileInfo(movedPath).Length,
                new FileInfo(movedPath).LastWriteTimeUtc.Ticks);
            Check(relinked.Id == project.Id
                && relinked.Assets[0].Id == asset.Id
                && relinked.Sequence is not null
                && missingProjectHasClips(relinked)
                && relinked.Sequence!.VideoClips.SequenceEqual(project.Sequence!.VideoClips),
                "relink changed project/asset/clip identity");
            Check(string.Equals(relinked.Assets[0].Path, movedPath, StringComparison.Ordinal),
                "relink did not swap the asset path");
            Check(TimelineMediaRelink.FindMissingMediaAssets(relinked).Count == 0,
                "relinked asset still reported missing");

            // Full controller roundtrip through the store.
            await store.SaveAsync(relinked, CancellationToken.None);
            await controller.OpenAsync(relinked.Id, CancellationToken.None, discardUnsavedChanges: true);
            Check(controller.Clips.Count == 1 && !controller.IsDirty,
                "reopened relinked project lost clips or started dirty");
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    private static bool missingProjectHasClips(TimelineProjectV2 project) =>
        project.Sequence is not null && project.Sequence.VideoClips.Count > 0;

    private static async Task RelinkRejectsWrongFileAsync()
    {
        var fixture = TimelineAudioContractFixture.CreateLocalVoiceState();
        var project = TimelineAudioContractFixture.Project(fixture.State, fixture.MediaAssets);
        var asset = project.Assets[0] with { Size = 12_345, LastWriteUtcTicks = 638_000_000_000_000_000 };
        project = project with { Assets = [asset] };

        Throws<InvalidDataException>(() => TimelineMediaRelink.RelinkMediaAsset(
            project, asset.Id, "C:/elsewhere/new.mp4", asset.Width + 2, asset.Height,
            asset.DurationUs / (double)TimelineTime.MicrosecondsPerSecond, asset.HasAudio, asset.Size, asset.LastWriteUtcTicks),
            "relink accepted a file with wrong geometry");
        Throws<InvalidDataException>(() => TimelineMediaRelink.RelinkMediaAsset(
            project, asset.Id, "C:/elsewhere/new.mp4", asset.Width, asset.Height,
            asset.DurationUs / (double)TimelineTime.MicrosecondsPerSecond + 3.0, asset.HasAudio, asset.Size, asset.LastWriteUtcTicks),
            "relink accepted a file with wrong duration");
        Throws<InvalidDataException>(() => TimelineMediaRelink.RelinkMediaAsset(
            project, TimelineMediaAsset.NewId(), "C:/elsewhere/new.mp4", asset.Width, asset.Height,
            asset.DurationUs / (double)TimelineTime.MicrosecondsPerSecond, asset.HasAudio, asset.Size, asset.LastWriteUtcTicks),
            "relink accepted an unknown asset id");
        var ok = TimelineMediaRelink.RelinkMediaAsset(
            project, asset.Id, "C:/elsewhere/new.mp4", asset.Width, asset.Height,
            asset.DurationUs / (double)TimelineTime.MicrosecondsPerSecond + 0.2,
            asset.HasAudio, asset.Size, asset.LastWriteUtcTicks);
        Check(ok.Assets[0].Id == asset.Id,
            "in-tolerance relink was rejected");
    }

    private static async Task RelinkRejectedWhileDirtyAsync()
    {
        var root = TemporaryRoot();
        try
        {
            var fixture = TimelineAudioContractFixture.CreateLocalVoiceState();
            var project = TimelineAudioContractFixture.Project(fixture.State, fixture.MediaAssets);
            var store = new TimelineProjectStoreV2(root);
            await store.SaveAsync(project, CancellationToken.None);
            var controller = new TimelineEditingController(store);
            await controller.OpenAsync(project.Id, CancellationToken.None);
            controller.MoveSelected(1);
            Check(controller.IsDirty, "edit did not dirty the controller");

            await ThrowsAsync<InvalidOperationException>(() => controller.RelinkMediaAssetAsync(
                project.Assets[0].Id, "C:/elsewhere/new.mp4", project.Assets[0].Width,
                project.Assets[0].Height, project.Assets[0].DurationUs / (double)TimelineTime.MicrosecondsPerSecond,
                project.Assets[0].HasAudio, project.Assets[0].Size, project.Assets[0].LastWriteUtcTicks,
                CancellationToken.None));
            Check(controller.IsDirty && controller.UndoCount == 1,
                "rejected relink changed controller state");
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    private static async Task AbandonedTemporariesAreReapedAsync()
    {
        var root = TemporaryRoot();
        try
        {
            var fixture = TimelineAudioContractFixture.CreateLocalVoiceState();
            var project = TimelineAudioContractFixture.Project(fixture.State, fixture.MediaAssets);
            var store = new TimelineProjectStoreV2(root);
            await store.SaveAsync(project, CancellationToken.None);

            var abandoned = Path.Combine(root, project.Id + ".json.tmp-deadbeef");
            await File.WriteAllTextAsync(abandoned, "crashed write");
            var unrelated = Path.Combine(root, "notes.txt");
            await File.WriteAllTextAsync(unrelated, "keep");

            var listed = await store.ListAsync(CancellationToken.None);
            Check(listed.Any(item => item.Id == project.Id) && !File.Exists(abandoned)
                && File.Exists(unrelated),
                "ListAsync did not reap only abandoned .tmp temporaries");
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    private static string TemporaryRoot()
    {
        var root = Path.Combine(
            Path.GetTempPath(), "bilisub-timeline-g19-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void TryDeleteDirectory(string path)
    {
        try { Directory.Delete(path, recursive: true); } catch { }
    }

    private static void Check(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException(message);
    }

    private static void Throws<TException>(Action action, string message)
        where TException : Exception
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

    private static async Task ThrowsAsync<TException>(Func<Task> action)
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
        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }
}
