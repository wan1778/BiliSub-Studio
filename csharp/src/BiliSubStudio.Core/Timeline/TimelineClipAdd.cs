namespace BiliSubStudio.Core.Timeline;

// P1 multi-video (27/09): pure project-level append nối tiếp + gán SRT theo
// clip. Không IO (probe/parse là việc của caller), caller persist nguyên tử.
// Mỗi video 1 asset full-range (0..DurationUs, rate 1.0); clip mới luôn nối
// đuôi (cursor = tổng duration). SRT gán clip-local (0..DurationUs của clip),
// gán lại thì thay entry cũ (không có UI xóa item riêng).
public static class TimelineClipAdd
{
    public static (TimelineProjectV2 Project, IReadOnlyList<TimelineClip> Added) AddClips(
        TimelineProjectV2 project,
        IReadOnlyList<TimelineMediaAsset> assets)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(assets);
        if (assets.Count == 0)
            throw new InvalidDataException("Chưa chọn video nào để thêm vào Timeline.");
        foreach (var asset in assets)
        {
            ArgumentNullException.ThrowIfNull(asset);
            asset.Validate();
        }
        RejectDuplicatePaths(project, assets);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var asset in project.Assets)
            ids.Add(asset.Id);
        foreach (var asset in assets)
            if (!ids.Add(asset.Id))
                throw new InvalidDataException("Timeline project chứa asset trùng ID.");

        var allAssets = project.Assets.Concat(assets).ToList();
        var clips = project.Sequence?.VideoClips.ToList() ?? new List<TimelineClip>();
        var before = clips.Count;
        foreach (var asset in assets)
            clips = TimelineSequenceOps.Add(clips, asset.Id, 0, asset.DurationUs, 1.0).ToList();

        TimelineSequence rebuilt = project.Sequence is { } existing
            ? new TimelineSequence(clips, existing.OutputWidth, existing.OutputHeight, existing.OutputFrameRate)
            : TimelineSequence.Create(clips, allAssets, assets[0].FrameRate);
        rebuilt.Validate(allAssets);
        // Mỗi clip phải có đúng một source-audio state (gain mặc định 1 cho
        // clip mới; clip cũ giữ nguyên chỉnh tay của user).
        var added = clips.Skip(before).ToArray();
        var addedIds = added.Select(clip => clip.Id).ToHashSet(StringComparer.Ordinal);
        var audio = project.SourceAudio
            .Where(state => !addedIds.Contains(state.ClipId))
            .Concat(TimelineSourceAudioState.Defaults(added))
            .ToList();
        var updated = project with { Assets = allAssets, Sequence = rebuilt, SourceAudio = audio };
        updated.Validate();
        return (updated, added);
    }

    // P1.1 (27/09): chặn file trùng path — trùng trong 1 lần chọn và trùng
    // với project đã có. Windows path không phân biệt hoa thường nên so
    // OrdinalIgnoreCase; path hai bên đã qua cùng pipeline chuẩn hóa caller.
    private static void RejectDuplicatePaths(
        TimelineProjectV2 project,
        IReadOnlyList<TimelineMediaAsset> assets)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var asset in project.Assets)
            paths.Add(asset.Path);
        foreach (var asset in assets)
            if (!paths.Add(asset.Path))
                throw new InvalidDataException(
                    $"File đã có trong project Timeline, không thêm trùng: {asset.Path}");
    }

    // PDEC-004 recommendation: SRT attaches to the selected clip by default.
    public static TimelineProjectV2 AttachClipSubtitle(
        TimelineProjectV2 project,
        string clipId,
        string srtPath)
    {
        return AttachClipSubtitles(project, [(clipId, srtPath)]);
    }

    // P1.1 (27/09): gán N SRT cho N clip trong MỘT lần rebuild — caller persist
    // nguyên tử 1 lần thay vì N lần load+save (batch 30-50 video không phải
    // ghi 30-50 lần). Gán lại thì thay entry cũ; cue gắn item bị thay bị xóa
    // theo (mirror TimelineAudioDependencyOps.Delete: không để cue không owner
    // giết Validate). Cue mới không tạo sẵn — cue sống lại từ SRT payload.
    public static TimelineProjectV2 AttachClipSubtitles(
        TimelineProjectV2 project,
        IReadOnlyList<(string ClipId, string SrtPath)> assignments)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(assignments);
        if (assignments.Count == 0)
            throw new InvalidDataException("Chưa có SRT nào để gán vào Timeline.");
        var sequence = project.Sequence
            ?? throw new InvalidOperationException("Project Timeline chưa có sequence để gán SRT.");
        var assignedClipIds = assignments.Select(item => item.ClipId).ToHashSet(StringComparer.Ordinal);
        if (assignedClipIds.Count != assignments.Count)
            throw new InvalidDataException("Gán SRT trùng clip trong 1 lượt; mỗi clip chỉ một entry.");
        var clipById = sequence.VideoClips.ToDictionary(item => item.Id, StringComparer.Ordinal);
        foreach (var (clipId, srtPath) in assignments)
        {
            if (string.IsNullOrWhiteSpace(clipId))
                throw new InvalidDataException("Chưa chọn clip Timeline để gán SRT.");
            if (string.IsNullOrWhiteSpace(srtPath))
                throw new InvalidDataException("Đường dẫn SRT gán cho clip bị rỗng.");
            if (!clipById.ContainsKey(clipId))
                throw new InvalidDataException("Timeline clip không tồn tại để gán SRT.");
        }

        // Thay item cũ: mọi subtitle item clip-local của các clip được gán bị
        // bỏ, thay bằng entry mới phủ 0..DurationUs. SRT sequence-global
        // (ClipId null) giữ nguyên. Cue gắn item bị thay bị xóa theo.
        var replacedIds = project.Items
            .Where(item => item.Kind == TimelineItemKind.Subtitle
                && item.ClipId is { } owner && assignedClipIds.Contains(owner))
            .Select(item => item.Id)
            .ToHashSet(StringComparer.Ordinal);
        var items = project.Items
            .Where(item => !replacedIds.Contains(item.Id))
            .ToList();
        foreach (var (clipId, srtPath) in assignments)
        {
            var clip = clipById[clipId];
            items.Add(TimelineItemOps.AttachSubtitleToClip(clip.Id, 0, clip.DurationUs, srtPath.Trim()));
        }
        var cues = project.SubtitleCues
            .Where(cue => !replacedIds.Contains(cue.SubtitleItemId))
            .ToList();
        TimelineItemOps.ValidateAll(items, sequence.VideoClips);
        var updated = project with { Items = items, SubtitleCues = cues };
        updated.Validate();
        return updated;
    }
}
