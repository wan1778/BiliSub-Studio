using BiliSubStudio.Core.Timeline;

namespace BiliSubStudio.Core.ContractTests;

internal static class TimelineEditingContract
{
    public static async Task RunAsync()
    {
        await OpenSelectEditFlowAsync();
        await FailedOpKeepsStateAsync();
        await UndoRedoRoundtripAsync();
        await SavePersistsAsync();
        await OpenValidationAsync();
        await NoOpEditsStayCleanAsync();
        await ProjectSwitchResetsHistoryAsync();
        await MultiTrackEditsStayAtomicAsync();
        await TrackClipEditsStayAtomicAsync();
    }

    // G31c: secondary track clips select/move/trim/delete through the same
    // atomic history; locked tracks and out-of-range trims reject cleanly.
    private static async Task TrackClipEditsStayAtomicAsync()
    {
        var root = TemporaryRoot();
        try
        {
            var fixture = TimelineAudioContractFixture.CreateLocalVoiceState();
            var asset = fixture.MediaAssets.Single();
            var overlay = new TimelineTrackV2(
                TimelineTrackV2.NewId(), TimelineTrackKind.VideoOverlay, "Overlay", 10,
                IsVisible: true, IsMuted: false, IsLocked: false, GainDb: 0, Clips: []);
            var clip = TimelineTrackClip.Create(
                asset.Id, overlay.Id, 1_000_000, 0, 5_000_000);
            var project = TimelineAudioContractFixture.Project(fixture.State, fixture.MediaAssets) with
            {
                Tracks = [overlay with { Clips = [clip] }],
            };
            var store = new TimelineProjectStoreV2(root);
            await store.SaveAsync(project, CancellationToken.None);
            var controller = new TimelineEditingController(store);
            await controller.OpenAsync(project.Id, CancellationToken.None);

            controller.SelectTrackClip(overlay.Id, clip.Id);
            Check(controller.SelectedTrackClip?.Id == clip.Id
                && controller.SelectedClipId is null,
                "track clip selection did not clear the primary selection");

            controller.MoveTrackClip(overlay.Id, clip.Id, 2_000_000);
            Check(controller.SelectedTrackClip?.TimelineStartUs == 2_000_000
                && controller.IsDirty && controller.UndoCount == 1,
                "track clip move did not enter atomic history");
            var undoBeforeNoOp = controller.UndoCount;
            controller.MoveTrackClip(overlay.Id, clip.Id, 2_000_000);
            Check(controller.UndoCount == undoBeforeNoOp && controller.IsDirty,
                "no-op track clip move polluted history");
            Check(controller.Undo()
                && controller.SelectedTrackClip?.TimelineStartUs == 1_000_000,
                "undo did not restore track clip position");
            Check(controller.Redo()
                && controller.SelectedTrackClip?.TimelineStartUs == 2_000_000,
                "redo did not restore track clip position");

            controller.TrimTrackClip(overlay.Id, clip.Id, 1_000_000, 5_000_000);
            Check(controller.SelectedTrackClip?.SourceInUs == 1_000_000,
                "track clip trim did not update the source range");

            var before = controller.Project;
            Throws<InvalidDataException>(
                () => controller.TrimTrackClip(overlay.Id, clip.Id, 0, 61_000_000),
                "trim beyond asset duration did not reject");
            Check(controller.Project == before && controller.IsDirty,
                "rejected trim changed controller state");
            Check(controller.UndoCount == 2,
                "rejected trim polluted history");

            controller.DeleteTrackClip(overlay.Id, clip.Id);
            Check(controller.Project.Tracks.Single().Clips.Count == 0
                && controller.SelectedTrackClip is null,
                "track clip delete did not clear selection");
            Check(controller.Undo()
                && controller.Project.Tracks.Single().Clips.Count == 1,
                "undo did not restore the deleted track clip");

            await controller.SaveAsync(CancellationToken.None);
            var saved = await store.LoadAsync(project.Id, CancellationToken.None);
            Check(saved.Tracks.Single().Clips.Single().TimelineStartUs == 2_000_000,
                "track clip edits did not persist");

            // Locked track rejects with state untouched.
            var locked = new TimelineTrackV2(
                TimelineTrackV2.NewId(), TimelineTrackKind.Music, "Locked", 0,
                IsVisible: true, IsMuted: false, IsLocked: true, GainDb: 0, Clips: []);
            var lockedClip = TimelineTrackClip.Create(
                asset.Id, locked.Id, 0, 0, 5_000_000);
            var lockedProject = TimelineAudioContractFixture.Project(
                fixture.State, fixture.MediaAssets, TimelineProjectV2.NewId()) with
            {
                Tracks = [locked with { Clips = [lockedClip] }],
            };
            await store.SaveAsync(lockedProject, CancellationToken.None);
            await controller.OpenAsync(lockedProject.Id, CancellationToken.None, discardUnsavedChanges: true);
            Throws<InvalidOperationException>(
                () => controller.MoveTrackClip(locked.Id, lockedClip.Id, 1_000_000),
                "move on a locked track did not reject");
            Check(controller.UndoCount == 0 && !controller.IsDirty,
                "locked-track rejection polluted history");
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    private static async Task MultiTrackEditsStayAtomicAsync()
    {
        var root = TemporaryRoot();
        try
        {
            var fixture = TimelineAudioContractFixture.CreateLocalVoiceState();
            var textTrack = new TimelineTrackV2(
                TimelineTrackV2.NewId(), TimelineTrackKind.Text, "Text", 20,
                IsVisible: true, IsMuted: false, IsLocked: false, GainDb: 0, Clips: []);
            var text = TimelineTextItem.Create(
                textTrack.Id, 1_000_000, 2_000_000, "G31 text");
            var project = TimelineAudioContractFixture.Project(fixture.State, fixture.MediaAssets) with
            {
                Tracks = [textTrack],
                TextItems = [text],
            };
            var store = new TimelineProjectStoreV2(root);
            await store.SaveAsync(project, CancellationToken.None);
            var controller = new TimelineEditingController(store);
            await controller.OpenAsync(project.Id, CancellationToken.None);

            controller.SetTrackState(textTrack.Id, isVisible: false, isMuted: true);
            Check(!controller.Project.Tracks.Single().IsVisible
                && controller.Project.Tracks.Single().IsMuted
                && controller.IsDirty && controller.UndoCount == 1,
                "track toggle did not enter atomic history");
            Check(controller.Undo()
                && controller.Project.Tracks.Single().IsVisible
                && !controller.Project.Tracks.Single().IsMuted,
                "undo did not restore track state");
            Check(controller.Redo()
                && !controller.Project.Tracks.Single().IsVisible
                && controller.Project.Tracks.Single().IsMuted,
                "redo did not restore track state");

            controller.UpsertTextItem(text with { Content = "G31 edited" });
            Check(controller.Project.TextItems.Single().Content == "G31 edited",
                "text update did not reach the open project");
            Check(controller.Undo()
                && controller.Project.TextItems.Single().Content == "G31 text",
                "undo did not restore text content");

            controller.MoveSelected(1);
            Check(controller.Project.Tracks.Count == 1
                && controller.Project.TextItems.Count == 1,
                "primary clip edit dropped schema-6 track state");
            await controller.SaveAsync(CancellationToken.None);
            var saved = await store.LoadAsync(project.Id, CancellationToken.None);
            Check(saved.Tracks.Count == 1 && saved.TextItems.Single().Content == "G31 text",
                "multi-track edits did not persist with the project");
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    private static async Task OpenSelectEditFlowAsync()
    {
        var root = TemporaryRoot();
        try
        {
            var fixture = TimelineAudioContractFixture.CreateLocalVoiceState();
            var project = TimelineAudioContractFixture.Project(fixture.State, fixture.MediaAssets);
            var store = new TimelineProjectStoreV2(root);
            await store.SaveAsync(project, CancellationToken.None);
            var controller = new TimelineEditingController(store);
            Check(!controller.HasOpenProject, "controller reported a project before open");

            await controller.OpenAsync(project.Id, CancellationToken.None);
            Check(controller.HasOpenProject && !controller.IsDirty,
                "open controller must start clean");
            Check(controller.Clips.Count == 2, "open controller lost clips");
            Check(controller.SelectedClipId == fixture.Clips[0].Id,
                "open controller must select the first clip");
            Check(controller.Project.Items.Count == 1
                && controller.Project.VoiceCues.Count == 1,
                "open controller lost items or voice links");

            var first = fixture.Clips[0];
            controller.TrimSelected(first.SourceInUs + 1_000_000, first.SourceOutUs);
            Check(controller.Clips.Count == 2
                && controller.Clips[0].SourceInUs == first.SourceInUs + 1_000_000
                && controller.UndoCount == 1 && controller.IsDirty,
                "trim did not update the open project through the controller");
            Check(controller.Project.VoiceCues.Count == 1,
                "head trim before the cue invalidated valid voice");

            controller.SplitSelected(5_000_000, TimelineSubtitleSplitPolicy.SplitAndClamp);
            Check(controller.Clips.Count == 3 && controller.UndoCount == 2,
                "split did not update the open project through the controller");
            Check(controller.SelectedClipId == first.Id,
                "split must keep the left clip selected");

            controller.DeleteSelected();
            Check(controller.Clips.Count == 2
                && controller.SelectedClipId == controller.Clips[0].Id,
                "delete did not fix selection onto the surviving clip");
            Check(controller.Project.Music == fixture.State.Music,
                "delete changed sequence-global music");
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    private static async Task FailedOpKeepsStateAsync()
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

            var beforeClips = controller.Clips.ToArray();
            var beforeProject = controller.Project;
            var first = fixture.Clips[0];
            Throws<InvalidOperationException>(
                () => controller.TrimSelected(first.SourceInUs + 2_000_000, first.SourceOutUs),
                "trim through voiced content did not require an explicit policy");
            Check(controller.Clips.SequenceEqual(beforeClips)
                && controller.Project == beforeProject
                && controller.UndoCount == 0 && controller.RedoCount == 0
                && !controller.IsDirty
                && controller.SelectedClipId == first.Id,
                "rejected edit changed controller state, history, selection or dirty flag");
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    private static async Task UndoRedoRoundtripAsync()
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
            var opened = controller.Project;

            controller.SelectClip(fixture.Clips[1].Id);
            controller.DeleteSelected();
            Check(controller.Clips.Count == 1 && controller.RedoCount == 0,
                "delete did not apply");
            Check(controller.Undo(), "undo did not apply");
            var restored = controller.Project;
            Check(restored.Sequence is not null && opened.Sequence is not null
                && restored.Sequence.VideoClips.SequenceEqual(opened.Sequence.VideoClips)
                && restored.Items.SequenceEqual(opened.Items)
                && restored.SubtitleCues.SequenceEqual(opened.SubtitleCues)
                && restored.AudioAssets.SequenceEqual(opened.AudioAssets)
                && restored.SourceAudio.SequenceEqual(opened.SourceAudio)
                && restored.VoiceCues.SequenceEqual(opened.VoiceCues)
                && restored.Music == opened.Music
                && controller.RedoCount == 1 && !controller.IsDirty,
                "undo did not restore the complete pre-edit state");
            Check(controller.Redo(), "redo did not apply");
            Check(controller.Clips.Count == 1 && controller.IsDirty,
                "redo did not restore the delete and dirty state");
            controller.MoveSelected(0);
            Check(controller.RedoCount == 0,
                "new edit did not invalidate the redo stack");
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    private static async Task SavePersistsAsync()
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

            var first = fixture.Clips[0];
            controller.TrimSelected(first.SourceInUs + 1_000_000, first.SourceOutUs);
            Check(controller.IsDirty, "edit did not mark the controller dirty");
            await controller.SaveAsync(CancellationToken.None);
            Check(!controller.IsDirty, "save did not clear the dirty flag");
            var reopened = await store.LoadAsync(project.Id, CancellationToken.None);
            var saved = controller.Project;
            Check(reopened.Sequence is not null && saved.Sequence is not null
                && reopened.Sequence.VideoClips.SequenceEqual(saved.Sequence.VideoClips)
                && reopened.Items.SequenceEqual(saved.Items)
                && reopened.SubtitleCues.SequenceEqual(saved.SubtitleCues)
                && reopened.VoiceCues.SequenceEqual(saved.VoiceCues)
                && reopened.SourceAudio.SequenceEqual(saved.SourceAudio)
                && reopened.Music == saved.Music,
                "saved project drifted on reopen");
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    private static async Task OpenValidationAsync()
    {
        var root = TemporaryRoot();
        try
        {
            var store = new TimelineProjectStoreV2(root);
            var empty = await store.CreateAsync("G15 empty", CancellationToken.None);
            var controller = new TimelineEditingController(store);
            await ThrowsAsync<InvalidOperationException>(
                () => controller.OpenAsync(empty.Id, CancellationToken.None),
                "project without a sequence was accepted for editing");
            Check(!controller.HasOpenProject, "failed open left a half-open controller");

            var fixture = TimelineAudioContractFixture.CreateLocalVoiceState();
            var project = TimelineAudioContractFixture.Project(fixture.State, fixture.MediaAssets);
            await store.SaveAsync(project, CancellationToken.None);
            await controller.OpenAsync(project.Id, CancellationToken.None);
            Throws<InvalidDataException>(
                () => controller.SelectClip(TimelineClip.NewId()),
                "unknown clip id was accepted");
            controller.SelectClip(null);
            Check(controller.SelectedClipId is null, "selection did not clear");
            Throws<InvalidOperationException>(
                () => controller.DeleteSelected(),
                "edit without selection was accepted");
            Check(!controller.Undo() && !controller.Redo(),
                "empty history reported an undo/redo step");
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    private static async Task NoOpEditsStayCleanAsync()
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
            var clip = controller.Clips[0];

            controller.TrimSelected(clip.SourceInUs, clip.SourceOutUs);
            controller.MoveSelected(0);
            Check(controller.UndoCount == 0 && controller.RedoCount == 0
                && controller.Revision == 0 && !controller.IsDirty,
                "no-op trim/reorder created history or dirtied the project");
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    private static async Task ProjectSwitchResetsHistoryAsync()
    {
        var root = TemporaryRoot();
        try
        {
            var fixture = TimelineAudioContractFixture.CreateLocalVoiceState();
            var first = TimelineAudioContractFixture.Project(fixture.State, fixture.MediaAssets);
            var second = TimelineAudioContractFixture.Project(fixture.State, fixture.MediaAssets);
            var store = new TimelineProjectStoreV2(root);
            await store.SaveAsync(first, CancellationToken.None);
            await store.SaveAsync(second, CancellationToken.None);
            var controller = new TimelineEditingController(store);

            await controller.OpenAsync(first.Id, CancellationToken.None);
            controller.MoveSelected(1);
            Check(controller.UndoCount == 1 && controller.IsDirty,
                "first project edit did not create history");
            await ThrowsAsync<InvalidOperationException>(
                () => controller.OpenAsync(second.Id, CancellationToken.None),
                "dirty project switch did not require an explicit discard");
            Check(controller.Project.Id == first.Id && controller.UndoCount == 1
                && controller.IsDirty,
                "rejected project switch changed the current edit state");
            await controller.OpenAsync(
                second.Id, CancellationToken.None, discardUnsavedChanges: true);
            Check(controller.Project.Id == second.Id && controller.UndoCount == 0
                && controller.RedoCount == 0 && controller.Revision == 0
                && !controller.IsDirty
                && !controller.Undo(),
                "opening another project retained stale history or dirty state");
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    private static string TemporaryRoot()
    {
        var root = Path.Combine(
            Path.GetTempPath(), "bilisub-timeline-g15-" + Guid.NewGuid().ToString("N"));
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
