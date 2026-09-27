using BiliSubStudio.Core.Timeline;

namespace BiliSubStudio.Core.ContractTests;

// P1 multi-video (27/09): nối tiếp nhiều asset + gán SRT theo clip, pure
// (không IO). Project rỗng (chưa có sequence) dựng sequence mới từ asset đầu;
// project có sẵn giữ nguyên geometry đầu ra.
internal static class TimelineMultiClipAddContract
{
    public static Task RunAsync()
    {
        AddToEmptyProjectBuildsSequence();
        AddToExistingProjectKeepsGeometry();
        AttachAndReplaceSubtitle();
        RejectsBadInput();
        RejectsDuplicatePaths();
        ReattachCleansDanglingCues();
        BatchAssignSubtitles();
        return Task.CompletedTask;
    }

    private static TimelineProjectV2 EmptyProject() => new(
        Guid.NewGuid().ToString("N"), "p1", DateTimeOffset.UtcNow,
        [], null, [], [], [], [], [], null);

    private static TimelineMediaAsset Asset(string name, double seconds, int width = 1920, int height = 1080, double fps = 30) =>
        TimelineMediaAsset.Create("C:/v/" + name, width, height, TimelineTime.FromSeconds(seconds), fps, hasAudio: true);

    private static void AddToEmptyProjectBuildsSequence()
    {
        var (project, added) = TimelineClipAdd.AddClips(EmptyProject(), [Asset("a.mp4", 10), Asset("b.mp4", 20)]);
        Check(added.Count == 2, "add mất clip");
        Check(project.Assets.Count == 2, "add không đăng ký asset");
        Check(project.Sequence is not null, "project rỗng phải dựng sequence mới");
        Check(added[0].TimelineStartUs == 0 && added[0].TimelineEndUs == 10_000_000, "clip đầu sai range");
        Check(added[1].TimelineStartUs == 10_000_000 && added[1].TimelineEndUs == 30_000_000, "clip sau không nối đuôi");
        Check(project.Sequence!.OutputWidth == 1920 && project.Sequence.OutputHeight == 1080, "sequence phải theo geometry asset đầu");
        TimelineSequence.ValidateSequential(project.Sequence.VideoClips);
    }

    private static void AddToExistingProjectKeepsGeometry()
    {
        var (project, _) = TimelineClipAdd.AddClips(EmptyProject(), [Asset("a.mp4", 10, 1280, 720, 30)]);
        var (grown, added) = TimelineClipAdd.AddClips(project, [Asset("b.mp4", 5)]);
        Check(added.Count == 1, "add lần 2 mất clip");
        Check(added[0].TimelineStartUs == 10_000_000 && added[0].TimelineEndUs == 15_000_000, "clip mới không nối đuôi");
        Check(grown.Sequence!.OutputWidth == 1280 && grown.Sequence.OutputHeight == 720, "thêm clip không được đổi geometry đầu ra");
        Check(grown.Assets.Count == 2, "asset mới phải đăng ký");
    }

    private static void AttachAndReplaceSubtitle()
    {
        var (project, added) = TimelineClipAdd.AddClips(EmptyProject(), [Asset("a.mp4", 10)]);
        var clip = added[0];
        var withSrt = TimelineClipAdd.AttachClipSubtitle(project, clip.Id, "C:/v/a.srt");
        var items = withSrt.Items.Where(item => item.Kind == TimelineItemKind.Subtitle).ToArray();
        Check(items.Length == 1, "gán SRT phải tạo đúng 1 item");
        Check(items[0].ClipId == clip.Id && items[0].ClipStartUs == 0 && items[0].ClipEndUs == clip.DurationUs,
            "item SRT phải clip-local phủ hết clip");
        var replaced = TimelineClipAdd.AttachClipSubtitle(withSrt, clip.Id, "C:/v/a2.srt");
        var again = replaced.Items.Where(item => item.Kind == TimelineItemKind.Subtitle).ToArray();
        Check(again.Length == 1 && again[0].Payload == "C:/v/a2.srt", "gán lại phải thay entry cũ, không đẻ thêm");
    }

    private static void RejectsBadInput()
    {
        var (project, added) = TimelineClipAdd.AddClips(EmptyProject(), [Asset("a.mp4", 10)]);
        Throws<InvalidDataException>(() => TimelineClipAdd.AddClips(project, []), "add rỗng phải từ chối");
        Throws<InvalidDataException>(() => TimelineClipAdd.AttachClipSubtitle(project, "missing", "C:/v/x.srt"), "gán SRT clip lạ phải từ chối");
        Throws<InvalidDataException>(() => TimelineClipAdd.AttachClipSubtitle(project, added[0].Id, "  "), "gán SRT rỗng phải từ chối");
        Throws<InvalidDataException>(() => TimelineMediaAsset.Create("C:/v/z.mp4", 1920, 1080, 0, 30, true), "asset 0 giây phải từ chối");
    }

    // P1.1 (27/09): chặn file trùng path — trong 1 lần chọn, với project đã
    // có, và hoa thường (Windows path không phân biệt).
    private static void RejectsDuplicatePaths()
    {
        Throws<InvalidDataException>(
            () => TimelineClipAdd.AddClips(EmptyProject(), [Asset("a.mp4", 10), Asset("a.mp4", 20)]),
            "add trùng path trong 1 lượt chọn phải từ chối");
        var (grown, _) = TimelineClipAdd.AddClips(EmptyProject(), [Asset("a.mp4", 10)]);
        Throws<InvalidDataException>(
            () => TimelineClipAdd.AddClips(grown, [Asset("a.mp4", 5)]),
            "add trùng path với project có sẵn phải từ chối");
        Throws<InvalidDataException>(
            () => TimelineClipAdd.AddClips(grown, [Asset("A.MP4", 5)]),
            "add trùng path khác hoa thường phải từ chối");
        var (withNew, _) = TimelineClipAdd.AddClips(grown, [Asset("b.mp4", 5)]);
        Check(withNew.Assets.Count == 2, "add path mới phải thêm asset");
    }

    // P1.1 (27/09): gán lại SRT vào project có SubtitleCue trỏ item cũ phải
    // xóa cue theo (không để dangling cue giết Validate) — từng để cue mồ côi.
    private static void ReattachCleansDanglingCues()
    {
        var (project, added) = TimelineClipAdd.AddClips(EmptyProject(), [Asset("a.mp4", 10)]);
        var clip = added[0];
        var withSrt = TimelineClipAdd.AttachClipSubtitle(project, clip.Id, "C:/v/a.srt");
        var srtItem = withSrt.Items.Single(item => item.Kind == TimelineItemKind.Subtitle);
        var cued = withSrt with
        {
            SubtitleCues = [TimelineSubtitleCue.Create(srtItem.Id, 0, 5_000_000, "xin chào")],
        };
        var replaced = TimelineClipAdd.AttachClipSubtitle(cued, clip.Id, "C:/v/a2.srt");
        replaced.Validate();
        Check(replaced.SubtitleCues.Count == 0, "cue cũ trỏ item bị thay phải bị xóa");
        var again = replaced.Items.Where(item => item.Kind == TimelineItemKind.Subtitle).ToArray();
        Check(again.Length == 1 && again[0].Payload == "C:/v/a2.srt", "gán lại vẫn phải thay entry cũ");
    }

    // P1.1 (27/09): gán batch N SRT cho N clip 1 lần rebuild + từ chối input xấu.
    private static void BatchAssignSubtitles()
    {
        var (project, added) = TimelineClipAdd.AddClips(EmptyProject(), [Asset("a.mp4", 10), Asset("b.mp4", 20)]);
        var updated = TimelineClipAdd.AttachClipSubtitles(project,
            [(added[0].Id, "C:/v/a.srt"), (added[1].Id, "C:/v/b.srt")]);
        var items = updated.Items.Where(item => item.Kind == TimelineItemKind.Subtitle).ToArray();
        Check(items.Length == 2, "gán batch phải tạo đúng 2 item");
        Check(items.Any(item => item.ClipId == added[0].Id && item.Payload == "C:/v/a.srt"), "clip đầu sai SRT");
        Check(items.Any(item => item.ClipId == added[1].Id && item.Payload == "C:/v/b.srt"), "clip sau sai SRT");
        Check(updated.SubtitleCues.Count == 0, "gán batch không để cue thừa");
        updated.Validate();
        Throws<InvalidDataException>(
            () => TimelineClipAdd.AttachClipSubtitles(project, [(added[0].Id, "C:/v/a.srt"), (added[0].Id, "C:/v/b.srt")]),
            "gán batch trùng clip phải từ chối");
        Throws<InvalidDataException>(() => TimelineClipAdd.AttachClipSubtitles(project, []), "gán batch rỗng phải từ chối");
        Throws<InvalidOperationException>(() => TimelineClipAdd.AttachClipSubtitles(EmptyProject(), [(added[0].Id, "C:/v/a.srt")]),
            "gán batch vào project không sequence phải từ chối");
    }

    private static void Check(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException("timeline P1 multi-video: " + message);
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
        throw new InvalidOperationException("timeline P1 multi-video: " + message);
    }
}
